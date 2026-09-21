using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace TproxyRelay.Tests.Integration;

/// <summary>
/// Boots the real application (RelayApp.Build) on free loopback ports with a
/// temp key store and an echo backend: full-fidelity integration tests —
/// real sockets, real admin/public listener separation.
/// </summary>
public sealed class RelayHost : IAsyncDisposable
{
    public HttpClient Public { get; }
    public HttpClient Admin { get; }
    public RelayHub Hub { get; } = null!;
    public KeyStore Store { get; } = null!;
    public ProfileRegistry Registry { get; } = null!;
    public RelayOptions Opt { get; } = null!;
    public string PublicBase { get; }
    public string AdminBase { get; }
    public string DataDir { get; }

    private readonly WebApplication _app;
    private readonly EchoBackend _backend;

    private RelayHost(WebApplication app, EchoBackend backend, string dataDir, int publicPort, int adminPort)
    {
        _app = app;
        _backend = backend;
        DataDir = dataDir;
        PublicBase = $"http://127.0.0.1:{publicPort}";
        AdminBase = $"http://127.0.0.1:{adminPort}";
        Hub = app.Services.GetRequiredService<RelayHub>();
        Store = app.Services.GetRequiredService<KeyStore>();
        Registry = app.Services.GetRequiredService<ProfileRegistry>();
        Opt = app.Services.GetRequiredService<RelayOptions>();
        Public = new HttpClient { BaseAddress = new Uri(PublicBase) };
        Admin = new HttpClient { BaseAddress = new Uri(AdminBase) };
    }

    public static async Task<RelayHost> StartAsync(
        string carrierMode = "https",
        string apiToken = "testtoken123",
        IDictionary<string, string>? extraEnv = null)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "tproxy-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var backend = await EchoBackend.StartAsync();
        var env = new Dictionary<string, string>
        {
            ["TPROXY_SECRET_HEX"] = "000102030405060708090a0b0c0d0e0f",
            // The websocket client cannot override the Host header, so the
            // hostname is the literal listen address.
            ["TPROXY_PUBLIC_HOSTNAME"] = "127.0.0.1",
            ["TPROXY_LISTEN"] = $"127.0.0.1:{FreePort()}",
            ["TPROXY_ADMIN_LISTEN"] = $"127.0.0.1:{FreePort()}",
            ["TPROXY_BACKEND_HOST"] = $"127.0.0.1:{backend.Port}",
            ["TPROXY_TOKEN_KEY_PATH"] = Path.Combine(dataDir, "token.key"),
            ["TPROXY_KEYS_DB"] = Path.Combine(dataDir, "keys.db"),
            ["TPROXY_CONFIG"] = Path.Combine(dataDir, "none.json"),
            ["TPROXY_API_ENABLED"] = "true",
            ["TPROXY_API_TOKEN"] = apiToken,
            ["TPROXY_CARRIER_MODE"] = carrierMode,
        };
        if (extraEnv != null)
            foreach (var kv in extraEnv)
                env[kv.Key] = kv.Value;
        var app = RelayApp.Build([], k => env.TryGetValue(k, out var v) ? v : null);
        await app.StartAsync();
        var listenPort = app.Services.GetRequiredService<RelayOptions>().ListenPort;
        var adminPort = app.Services.GetRequiredService<RelayOptions>().AdminPort;
        return new RelayHost(app, backend, dataDir, listenPort, adminPort);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Bootstrap token for the built-in profile (bypasses the bridge URL).</summary>
    public string MintBootstrap() =>
        Hub.MintBootstrap(IPAddress.Loopback, Registry.All.First(p => p.KeyId == "builtin"))
            ?? throw new InvalidOperationException("bootstrap mint refused");

    public async ValueTask DisposeAsync()
    {
        Public.Dispose();
        Admin.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _backend.DisposeAsync();
        try { Directory.Delete(DataDir, recursive: true); } catch { /* best effort */ }
    }
}
