using System.Collections.Concurrent;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using TproxyRelay;

namespace TproxyRelay.Tests;

public sealed class KeyStoreTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "tproxy-ks-" + Guid.NewGuid().ToString("N")[..8] + ".db");
    private KeyStore Store() => new(_db);

    [Fact]
    public void Parallel_Creates_Allocate_Distinct_Ports()
    {
        // API-004/REL-016: concurrent Create() calls must never collide on a
        // backend port (the unique index turns the race into a retry).
        var store = Store();
        var ports = new ConcurrentBag<int>();
        Parallel.ForEach(Enumerable.Range(0, 16),
            i => ports.Add(store.Create($"race{i}").BackendPort));
        Assert.Equal(16, ports.Distinct().Count());
    }

    [Fact]
    public void Create_List_Get_Revoke()
    {
        var store = Store();
        var key = store.Create("ivan");
        Assert.Equal("ivan", key.Name);
        Assert.Equal(32, key.SecretHex.Length);
        Assert.True(key.Active);
        Assert.Equal(KeyStore.BasePort + 1, key.BackendPort); // 2398 is the builtin

        var second = store.Create("maria");
        Assert.Equal(KeyStore.BasePort + 2, second.BackendPort);

        Assert.Equal(2, store.ListKeys().Count);
        Assert.NotNull(store.GetByName("maria"));
        Assert.Equal(second.Id, store.Get(second.Id)!.Id);

        Assert.True(store.Revoke(key.Id));
        Assert.False(store.Revoke(key.Id)); // idempotent
        Assert.False(store.Get(key.Id)!.Active);
        // Revoked ports are freed for reuse.
        var third = store.Create("pavel");
        Assert.Equal(KeyStore.BasePort + 1, third.BackendPort);
    }

    [Fact]
    public void Duplicate_Name_Is_Rejected()
    {
        var store = Store();
        store.Create("ivan");
        Assert.Throws<InvalidOperationException>(() => store.Create("ivan"));
    }

    [Fact]
    public void Pause_Flags_Without_Revoking()
    {
        var store = Store();
        var key = store.Create("ivan");
        Assert.True(store.SetPaused(key.Id, true));
        Assert.False(store.Get(key.Id)!.Active);
        Assert.True(store.SetPaused(key.Id, false));
        Assert.True(store.Get(key.Id)!.Active);
    }

    [Fact]
    public void Explicit_Secret_Must_Look_Valid()
    {
        var store = Store();
        Assert.Throws<ArgumentException>(() => store.Create("bad", "zz"));
        Assert.Throws<ArgumentException>(() => store.Create("zero", "00000000000000000000000000000000"));
        var ok = store.Create("ok", "0123456789abcdef0123456789abcdef");
        Assert.Equal("0123456789abcdef0123456789abcdef", ok.SecretHex);
    }

    [Fact]
    public void Traffic_Aggregates_Per_Day_And_Key()
    {
        var store = Store();
        var key = store.Create("ivan");
        store.AggregateTraffic(key.Id, 100, 200);
        store.AggregateTraffic(key.Id, 50, 0);
        store.AggregateTraffic("builtin", 1, 2);
        var rows = store.Traffic(7);
        Assert.Equal(2, rows.Count);
        var ivan = rows.Single(r => r.KeyId == key.Id);
        Assert.Equal(150, ivan.UpBytes);
        Assert.Equal(200, ivan.DownBytes);
        Assert.Equal(2, rows.Single(r => r.KeyId == "builtin").DownBytes);
    }

    [Fact]
    public void Seed_Import_Is_Idempotent_By_Name()
    {
        var store = Store();
        var seed = Path.Combine(store.DataDirectory, "seed.json");
        File.WriteAllText(seed, """[{"name":"ivan"},{"name":"maria","secret_hex":"0123456789abcdef0123456789abcdef"}]""");
        var first = store.ImportSeed(seed);
        Assert.Equal(2, first.Count);
        var second = store.ImportSeed(seed);
        Assert.Empty(second); // already present
        Assert.NotNull(store.GetByName("ivan"));
        Assert.Equal("0123456789abcdef0123456789abcdef", store.GetByName("maria")!.SecretHex);
    }

    [Fact]
    public void Registry_Export_Contains_Builtin_And_Active_Only()
    {
        var store = Store();
        var keep = store.Create("keep");
        var gone = store.Create("gone");
        store.Revoke(gone.Id);
        store.ExportRegistry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var lines = File.ReadAllLines(Path.Combine(store.DataDirectory, "registry.txt"));
        Assert.Contains("2398:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", lines);
        Assert.Contains($"{keep.BackendPort}:{keep.SecretHex}", lines);
        Assert.DoesNotContain(lines, l => l.Contains(gone.SecretHex));
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_db)!, false); } catch { /* temp cleanup */ }
        try { File.Delete(_db); } catch { }
    }
}

public class ProfileRegistryTests
{
    private static RelayOptions Opt() => new("127.0.0.1:2398")
    {
        Secret = Convert.FromHexString("000102030405060708090a0b0c0d0e0f"),
    };

    [Fact]
    public void Builtin_Matches_By_Capability()
    {
        var opt = Opt();
        var registry = new ProfileRegistry(opt, new RelayProfile(
            "builtin", "builtin", opt.Secret, "127.0.0.1", 2398, "https"));
        var cap = CapabilityDeriver.Derive(opt.PublicHostname, opt.Secret);
        var hit = registry.Match(cap);
        Assert.NotNull(hit);
        Assert.Equal("builtin", hit!.KeyId);

        // A managed key gets its own capability.
        var otherSecret = Convert.FromHexString("0123456789abcdef0123456789abcdef");
        registry.ReplaceManaged([new RelayProfile("k1", "ivan", otherSecret, "127.0.0.1", 2399, "https")]);
        var hit2 = registry.Match(CapabilityDeriver.Derive(opt.PublicHostname, otherSecret));
        Assert.NotNull(hit2);
        Assert.Equal("k1", hit2!.KeyId);
        Assert.Equal(2399, hit2.BackendPort);

        // The builtin still matches after the managed replacement.
        Assert.Equal("builtin", registry.Match(cap)!.KeyId);
        Assert.Null(registry.Match("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
        Assert.Null(registry.Match("short"));
    }
}

public class KeyedSessionTests : IAsyncLifetime
{
    private EchoBackend _backend = null!;

    public async Task InitializeAsync() => _backend = await EchoBackend.StartAsync();
    public async Task DisposeAsync() => await _backend.DisposeAsync();

    [Fact]
    public async Task Sessions_Dial_Their_Own_Profile_Backend()
    {
        // A "managed" profile pointing at the same echo backend but a distinct port value.
        var opt = new RelayOptions($"127.0.0.1:{_backend.Port}")
        {
            Secret = Convert.FromHexString("000102030405060708090a0b0c0d0e0f"),
        };
        var store = new KeyStore(Path.Combine(Path.GetTempPath(), "tproxy-ks-" + Guid.NewGuid().ToString("N")[..8] + ".db"));
        var hub = new RelayHub(opt, new TokenMinter(new byte[32]), NullLogger.Instance, store);
        var profile = new RelayProfile("k1", "ivan", opt.Secret, "127.0.0.1", _backend.Port, "https");
        var boot = hub.MintBootstrap(null, profile);
        Assert.NotNull(boot);
        Assert.Equal(RedeemResult.Ok,
            hub.TryRedeemBootstrap(boot!, FrameCodec.Encode(FrameType.Hello, 0, [1]), null, out var s));
        Assert.Equal("k1", s!.KeyId);
        Assert.Equal(_backend.Port, s.BackendPort);

        // Echo flows through the profile's backend.
        var body = FrameCodec.Encode(FrameType.Open, 5)
            .Concat(FrameCodec.Encode(FrameType.Data, 5, new byte[100])).ToArray();
        Assert.Equal(UpOutcome.Acked, (await hub.ApplyUp(s, 1, body)).Outcome);

        // Revocation closes the key's sessions.
        Assert.Equal(1, hub.CloseAllSessionsForKey("k1", "revoked"));
        Assert.True(s.Dead);
        Assert.Equal(0, hub.CloseAllSessionsForKey("k1", "revoked"));
    }
}
