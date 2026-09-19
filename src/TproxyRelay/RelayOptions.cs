using System.Buffers.Text;

namespace TproxyRelay;

public sealed class RelayOptions
{
    public string PublicHostname { get; init; } = "proxy.example.com";
    public string BackendHost { get; init; } = "backend-stub:9000";
    public string CarrierMode { get; init; } = "https"; // https | websocket
    public int ListenPort { get; init; } = 8080;
    public int AdminPort { get; init; } = 8081;
    public string TokenKeyPath { get; init; } = "token.key";
    public byte[] Secret { get; init; } = [];
    public int MaxSessionsGlobal { get; init; } = 128;
    public int MaxStreamsGlobal { get; init; } = 4096;
    public int MaxStreamsPerSession { get; init; } = 128;
    public int MaxBootstrapsGlobal { get; init; } = 512;
    public long MaxPendingBytesPerSession { get; init; } = 32 * 1024 * 1024;
    public int DownBatchTargetBytes { get; init; } = 2 * 1024 * 1024;
    public int LongPollSeconds { get; init; } = 25;
    public int BootstrapTtlSeconds { get; init; } = 120;
    public int SessionIdleTtlSeconds { get; init; } = 600;
    public bool RequireHost { get; init; } = true;

    public string BackendHostName { get; }
    public int BackendPort { get; }

    private RelayOptions(string backendHost)
    {
        var parts = backendHost.Split(':', 2);
        BackendHostName = parts[0];
        BackendPort = parts.Length == 2 ? int.Parse(parts[1]) : 2398;
    }

    public byte[] CapabilityBytes =>
        Base64Url.DecodeFromChars(CapabilityDeriver.Derive(PublicHostname, Secret));

    public static RelayOptions Load()
    {
        var secretHex = Environment.GetEnvironmentVariable("TPROXY_SECRET_HEX");
        if (string.IsNullOrWhiteSpace(secretHex))
            throw new InvalidOperationException("TPROXY_SECRET_HEX is required (openssl rand -hex 16): refusing to start with no secret");
        byte[] secret;
        try { secret = Convert.FromHexString(secretHex); }
        catch (Exception e) { throw new InvalidOperationException("TPROXY_SECRET_HEX must be valid hex", e); }
        if (secret.Length is not (16 or 17))
            throw new InvalidOperationException("TPROXY_SECRET_HEX must decode to 16 (or 17 with dd-prefix) bytes");
        var backend = Env("TPROXY_BACKEND_HOST", "backend-stub:9000");
        var opt = new RelayOptions(backend)
        {
            PublicHostname = Env("TPROXY_PUBLIC_HOSTNAME", "proxy.example.com").ToLowerInvariant(),
            BackendHost = backend,
            CarrierMode = Env("TPROXY_CARRIER_MODE", "https"),
            TokenKeyPath = Env("TPROXY_TOKEN_KEY_PATH", "token.key"),
            Secret = secret,
            ListenPort = int.Parse(Env("TPROXY_LISTEN_PORT", "8080")),
            AdminPort = int.Parse(Env("TPROXY_ADMIN_PORT", "8081")),
            MaxSessionsGlobal = int.Parse(Env("TPROXY_MAX_SESSIONS", "128")),
            MaxStreamsGlobal = int.Parse(Env("TPROXY_MAX_STREAMS", "4096")),
            MaxStreamsPerSession = int.Parse(Env("TPROXY_MAX_STREAMS_PER_SESSION", "128")),
            MaxBootstrapsGlobal = int.Parse(Env("TPROXY_MAX_BOOTSTRAPS", "512")),
            RequireHost = Env("TPROXY_REQUIRE_HOST", "true") is not ("false" or "0"),
        };
        if (opt.CarrierMode is not ("https" or "websocket"))
            throw new InvalidOperationException($"unsupported carrier mode {opt.CarrierMode}");
        return opt;
    }

    private static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;
}
