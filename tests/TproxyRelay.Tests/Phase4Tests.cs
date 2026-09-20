using System.Net;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using TproxyRelay;

namespace TproxyRelay.Tests;

public class RateBucketTests
{
    [Fact]
    public void Burst_Caps_Concurrent_Takes()
    {
        var b = new RateBucket(600, 2);
        Assert.True(b.TryTake());
        Assert.True(b.TryTake());
        Assert.False(b.TryTake());
    }

    [Fact]
    public async Task Refills_Over_Time()
    {
        var b = new RateBucket(6000, 1); // 100/sec
        Assert.True(b.TryTake());
        Assert.False(b.TryTake());
        await Task.Delay(120);
        Assert.True(b.TryTake());
    }

    [Fact]
    public void Take_Larger_Than_Burst_Never_Succeeds()
    {
        var b = new RateBucket(6000, 4);
        Assert.False(b.TryTake(5));
    }
}

public class RelayOptionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tproxy-opt-" + Guid.NewGuid().ToString("N")[..8]);
    private string WriteConfig(string json)
    {
        Directory.CreateDirectory(_dir);
        var p = Path.Combine(_dir, "config.json");
        File.WriteAllText(p, json);
        return p;
    }

    [Fact]
    public void ConfigFile_Sets_Limits_And_Env_Overrides_Win()
    {
        var path = WriteConfig("""
            {
              "public_hostname": "Config.Example.Com",
              "carrier_mode": "websocket",
              "limits": { "max_streams_global": 1024, "new_sessions_burst": 7 }
            }
            """);
        var opt = RelayOptions.Load(k => k switch
        {
            "TPROXY_SECRET_HEX" => "000102030405060708090a0b0c0d0e0f",
            "TPROXY_CONFIG" => path,
            "TPROXY_MAX_STREAMS" => "2048",
            _ => null,
        });
        Assert.Equal("config.example.com", opt.PublicHostname); // lowercased
        Assert.Equal("websocket", opt.CarrierMode);
        Assert.Equal(2048, opt.MaxStreamsGlobal);              // env beats config (1024)
        Assert.Equal(7, opt.NewSessionsBurst);
    }

    [Fact]
    public void Oversized_CarrierBatch_Refuses_Startup()
    {
        var path = WriteConfig("""{ "limits": { "carrier_batch_bytes": 3145728 } }""");
        Assert.Throws<InvalidOperationException>(() => RelayOptions.Load(k => k switch
        {
            "TPROXY_SECRET_HEX" => "000102030405060708090a0b0c0d0e0f",
            "TPROXY_CONFIG" => path,
            _ => null,
        }));
    }

    [Fact]
    public void Control_Reserve_Overflow_Refuses_Startup()
    {
        var path = WriteConfig("""{ "limits": { "max_streams_per_session": 65536 } }""");
        Assert.Throws<InvalidOperationException>(() => RelayOptions.Load(k => k switch
        {
            "TPROXY_SECRET_HEX" => "000102030405060708090a0b0c0d0e0f",
            "TPROXY_CONFIG" => path,
            _ => null,
        }));
    }

    [Fact]
    public void Missing_Secret_Refuses_Startup()
    {
        Assert.Throws<InvalidOperationException>(() => RelayOptions.Load(_ => null));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* temp cleanup */ }
    }
}

public class Phase4HubTests : IAsyncLifetime
{
    private EchoBackend _backend = null!;
    private RelayOptions _opt = null!;
    private RelayHub _hub = null!;

    public async Task InitializeAsync()
    {
        _backend = await EchoBackend.StartAsync();
        _opt = new RelayOptions($"127.0.0.1:{_backend.Port}")
        {
            Secret = Convert.FromHexString("000102030405060708090a0b0c0d0e0f"),
        };
        _hub = new RelayHub(_opt, new TokenMinter(new byte[32]), NullLogger.Instance);
    }

    public async Task DisposeAsync() => await _backend.DisposeAsync();

    private static byte[] Hello() => FrameCodec.Encode(FrameType.Hello, 0, [1]);

    private Session NewSession(RelayHub? hub = null)
    {
        hub ??= _hub;
        var bootstrap = hub.MintBootstrap();
        Assert.NotNull(bootstrap);
        Assert.Equal(RedeemResult.Ok, hub.TryRedeemBootstrap(bootstrap!, Hello(), null, out var session));
        return session!;
    }

    [Fact]
    public async Task Stream_Rate_Limit_Closes_Only_That_Stream()
    {
        var opt = new RelayOptions($"127.0.0.1:{_backend.Port}")
        {
            Secret = Convert.FromHexString("000102030405060708090a0b0c0d0e0f"),
            NewStreamsPerMinute = 0.001, // effectively no refill
            NewStreamsBurst = 1,
        };
        var hub = new RelayHub(opt, new TokenMinter(new byte[32]), NullLogger.Instance);
        var session = NewSession(hub);

        var ok = await hub.ApplyUp(session, 1, FrameCodec.Encode(FrameType.Open, 5));
        Assert.Equal(UpOutcome.Acked, ok.Outcome);
        var rejected = await hub.ApplyUp(session, 2, FrameCodec.Encode(FrameType.Open, 6));
        Assert.Equal(UpOutcome.Acked, rejected.Outcome); // the BATCH is fine...

        // ...but stream 6 receives CLOSE, session and stream 5 live on.
        using var cts = new CancellationTokenSource(5000);
        var sawClose6 = false;
        for (var i = 0; i < 10 && !sawClose6; i++)
        {
            var down = await hub.GetDown(session, 0, cts.Token);
            if (!down.HasBatch) continue;
            foreach (var f in FrameCodec.ParseAll(down.Body!))
                if (f.Type == FrameType.Close && f.StreamId == 6)
                    sawClose6 = true;
        }
        Assert.True(sawClose6, "CLOSE for rate-limited stream not received");
        Assert.False(session.Dead);
        Assert.Contains(5u, session.Streams.Keys);
    }

    [Fact]
    public async Task Global_Pending_Gate_Closes_Session()
    {
        var opt = new RelayOptions($"127.0.0.1:{_backend.Port}")
        {
            Secret = Convert.FromHexString("000102030405060708090a0b0c0d0e0f"),
            MaxPendingBytesGlobal = 16 * 1024, // far below one echo batch
        };
        var hub = new RelayHub(opt, new TokenMinter(new byte[32]), NullLogger.Instance);
        var session = NewSession(hub);

        var payload = new byte[64 * 1024];
        var body = FrameCodec.Encode(FrameType.Open, 5)
            .Concat(FrameCodec.Encode(FrameType.Data, 5, payload)).ToArray();
        Assert.Equal(UpOutcome.Acked, (await hub.ApplyUp(session, 1, body)).Outcome);

        for (var i = 0; i < 50 && !session.Dead; i++)
            await Task.Delay(50);
        Assert.True(session.Dead, "global pending gate did not close the session");
    }

    [Fact]
    public void Bootstrap_PerIp_Cap_Rejects_Second()
    {
        var opt = new RelayOptions($"127.0.0.1:{_backend.Port}")
        {
            Secret = Convert.FromHexString("000102030405060708090a0b0c0d0e0f"),
            MaxBootstrapsPerIp = 1,
        };
        var hub = new RelayHub(opt, new TokenMinter(new byte[32]), NullLogger.Instance);
        var ip = IPAddress.Loopback;
        Assert.NotNull(hub.MintBootstrap(ip));
        Assert.Null(hub.MintBootstrap(ip));
        Assert.NotNull(hub.MintBootstrap(IPAddress.Parse("127.0.0.2")));
    }

    [Fact]
    public void Session_PerIp_Cap_Returns_RetryLater()
    {
        var opt = new RelayOptions($"127.0.0.1:{_backend.Port}")
        {
            Secret = Convert.FromHexString("000102030405060708090a0b0c0d0e0f"),
            MaxSessionsPerIp = 1,
        };
        var hub = new RelayHub(opt, new TokenMinter(new byte[32]), NullLogger.Instance);
        var ip = IPAddress.Loopback;
        var b1 = hub.MintBootstrap(ip)!;
        var b2 = hub.MintBootstrap(IPAddress.Parse("127.0.0.2"))!;
        Assert.Equal(RedeemResult.Ok, hub.TryRedeemBootstrap(b1, Hello(), ip, out _));
        Assert.Equal(RedeemResult.RetryLater,
            hub.TryRedeemBootstrap(b2, Hello(), ip, out _)); // same accounting ip
    }

    [Fact]
    public async Task Pending_Charges_Release_After_Ack()
    {
        var baseBytes = Counters.PendingBytesGauge;
        var baseItems = Counters.PendingItemsGauge;
        var session = NewSession();

        var payload = new byte[2000];
        var body = FrameCodec.Encode(FrameType.Open, 5)
            .Concat(FrameCodec.Encode(FrameType.Data, 5, payload)).ToArray();
        Assert.Equal(UpOutcome.Acked, (await _hub.ApplyUp(session, 1, body)).Outcome);

        // Drain: poll until a batch with DATA arrives, then acknowledge it.
        using var cts = new CancellationTokenSource(5000);
        DownResult down = default;
        for (var i = 0; i < 20; i++)
        {
            down = await _hub.GetDown(session, 0, cts.Token);
            if (down.HasBatch && FrameCodec.ParseAll(down.Body!).Any(f => f.Type == FrameType.Data))
                break;
        }
        Assert.True(down.HasBatch);
        var acked = await _hub.GetDown(session, down.Cursor, cts.Token); // cursor acknowledges
        Assert.False(acked.HasBatch);

        Assert.Equal(0, session.PendingBytes);
        Assert.Equal(0, session.PendingItems);
        Assert.Equal(baseBytes, Counters.PendingBytesGauge);
        Assert.Equal(baseItems, Counters.PendingItemsGauge);
    }
}
