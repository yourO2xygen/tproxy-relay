using System.Buffers.Text;
using System.Net;
using System.Text.Json;

namespace TproxyRelay;

/// <summary>Management subsystem toggles: everything is opt-in and fail-closed.</summary>
public sealed record ManagementApiConfig(bool Enabled, string AdminToken)
{
    public static ManagementApiConfig Disabled => new(false, "");
}

public sealed record ManagementBotConfig(bool Enabled, string Token, string AdminChatIds)
{
    public static ManagementBotConfig Disabled => new(false, "", "");
    public string[] AdminChats => AdminChatIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// Relay configuration. Precedence: built-in defaults &lt; config.json (TPROXY_CONFIG)
/// &lt; environment variables. The client secret stays environment/file-only and is
/// never accepted from config.json.
/// </summary>
public sealed class RelayOptions
{
    public string PublicHostname { get; init; } = "proxy.example.com";
    public string BasePath { get; init; } = "";     // "" = root deployment
    public string BackendHost { get; init; } = "backend-stub:9000";
    public string CarrierMode { get; init; } = "https"; // https | websocket
    public int ListenPort { get; init; } = 8080;
    public int AdminPort { get; init; } = 8081;
    public IPAddress? ListenAddress { get; init; }   // null = any interface
    public IPAddress? AdminAddress { get; init; }    // null = any interface
    public string TokenKeyPath { get; init; } = "token.key";
    public string KeysDbPath { get; init; } = "keys.db";  // SQLite registry of managed keys
    public string PublicDir { get; init; } = "";          // static site, read once at startup
    public string PublicUpstream { get; init; } = "";     // reverse proxy to a loopback app
    public bool StaticRoutesLegacy { get; init; }         // extensionless aliases for the static site
    public byte[] Secret { get; init; } = [];

    // -- limits (upstream tproxy-server capacity matrix) -----------------------
    public int MaxSessionsGlobal { get; init; } = 128;
    public int MaxStreamsGlobal { get; init; } = 4096;
    public int MaxStreamsPerSession { get; init; } = 128;
    public int MaxBackendDialsInFlight { get; init; } = 256;
    public int MaxBootstrapsGlobal { get; init; } = 512;
    public long MaxPendingBytesGlobal { get; init; } = 512 * 1024 * 1024;
    public long MaxPendingItemsGlobal { get; init; } = 262144;
    public long MaxPendingBytesPerSession { get; init; } = 32 * 1024 * 1024;
    public int MaxFramePayload { get; init; } = 1024 * 1024;
    public int DownBatchTargetBytes { get; init; } = 2 * 1024 * 1024;
    public double NewSessionsPerMinute { get; init; } = 600;
    public int NewSessionsBurst { get; init; } = 128;
    public double NewStreamsPerMinute { get; init; } = 6000;
    public int NewStreamsBurst { get; init; } = 512;
    public double NewBootstrapsPerMinute { get; init; } = 1200;
    public int NewBootstrapsBurst { get; init; } = 256;
    public int MaxSessionsPerIp { get; init; } = 0;      // 0 = disabled
    public int MaxBootstrapsPerIp { get; init; } = 0;    // 0 = disabled

    // -- timeouts ---------------------------------------------------------------
    public int LongPollSeconds { get; init; } = 25;
    public int BootstrapTtlSeconds { get; init; } = 120;
    public int ReconnectGraceSeconds { get; init; } = 120;
    public bool RequireHost { get; init; } = true;
    public ManagementApiConfig ManagementApi { get; init; } = ManagementApiConfig.Disabled;
    public ManagementBotConfig ManagementBot { get; init; } = ManagementBotConfig.Disabled;

    public string BackendHostName { get; }
    public int BackendPort { get; }

    public RelayOptions(string backendHost)
    {
        BackendHost = backendHost;
        var parts = backendHost.Split(':', 2);
        BackendHostName = parts[0];
        BackendPort = parts.Length == 2 ? int.Parse(parts[1]) : 2398;
    }

    public byte[] CapabilityBytes =>
        Base64Url.DecodeFromChars(CapabilityDeriver.Derive(PublicHostname, Secret, BasePath));

    // ---------------------------------------------------------------- load ----

    public static RelayOptions Load() => Load(Environment.GetEnvironmentVariable);

    public static RelayOptions Load(Func<string, string?> env)
    {
        // Fail-closed secret: environment or file, never config.json.
        var secretHex = env("TPROXY_SECRET_HEX");
        if (string.IsNullOrWhiteSpace(secretHex) &&
            env("TPROXY_SECRET_HEX_FILE") is { Length: > 0 } path)
            secretHex = File.ReadAllText(path).Trim();
        if (string.IsNullOrWhiteSpace(secretHex))
            throw new InvalidOperationException(
                "TPROXY_SECRET_HEX (or TPROXY_SECRET_HEX_FILE) is required (openssl rand -hex 16): refusing to start with no secret");
        byte[] secret;
        try { secret = Convert.FromHexString(secretHex); }
        catch (Exception e) { throw new InvalidOperationException("TPROXY_SECRET_HEX must be valid hex", e); }
        if (secret.Length is not (16 or 17))
            throw new InvalidOperationException("TPROXY_SECRET_HEX must decode to 16 (or 17 with dd-prefix) bytes");

        // Layer 2: config.json.
        var overrides = new Dictionary<string, string>();
        var configPath = env("TPROXY_CONFIG") ?? "config.json";
        if (File.Exists(configPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name is "limits" or "timeouts")
                {
                    foreach (var q in p.Value.EnumerateObject())
                        overrides[$"{p.Name}.{q.Name}"] = q.Value.ToString();
                }
                else if (p.Name == "management" && p.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var sub in p.Value.EnumerateObject())
                    {
                        if (sub.Value.ValueKind == JsonValueKind.Object)
                            foreach (var q in sub.Value.EnumerateObject())
                                overrides[$"management.{sub.Name}.{q.Name}"] = q.Value.ToString();
                        else
                            overrides[$"management.{sub.Name}"] = sub.Value.ToString();
                    }
                }
                else if (p.Value.ValueKind != JsonValueKind.Object)
                {
                    overrides[p.Name] = p.Value.ToString();
                }
            }
        }

        string Ov(string key, string? envValue, string fallback) =>
            envValue is { Length: > 0 } ? envValue : (overrides.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback);

        string Endpoint(string configKey, string? envEndpoint, string? envPort, int defaultPort, out IPAddress? address)
        {
            address = null;
            var value = envEndpoint is { Length: > 0 } ? envEndpoint
                : (overrides.TryGetValue(configKey, out var v) && v.Length > 0 ? v : null);
            if (value == null)
                return envPort is { Length: > 0 } ? envPort : defaultPort.ToString();
            var parts = value.Split(':', 2);
            if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var ip) && int.TryParse(parts[1], out var port))
            {
                address = ip;
                return port.ToString();
            }
            if (int.TryParse(value, out var portOnly))
                return portOnly.ToString();
            throw new InvalidOperationException($"{configKey} must be host:port or port");
        }

        var listen = Endpoint("listen", env("TPROXY_LISTEN"), env("TPROXY_LISTEN_PORT"), 8080, out var listenAddr);
        var admin = Endpoint("admin_listen", env("TPROXY_ADMIN_LISTEN"), env("TPROXY_ADMIN_PORT"), 8081, out var adminAddr);

        var opt = new RelayOptions(Ov("backend", env("TPROXY_BACKEND_HOST"), "backend-stub:9000"))
        {
            PublicHostname = Ov("public_hostname", env("TPROXY_PUBLIC_HOSTNAME"), "proxy.example.com").ToLowerInvariant(),
            BasePath = Ov("base_path", env("TPROXY_BASE_PATH"), "").Trim('/'),
            CarrierMode = Ov("carrier_mode", env("TPROXY_CARRIER_MODE"), "https"),
            PublicDir = Ov("public_dir", env("TPROXY_PUBLIC_DIR"), ""),
            PublicUpstream = Ov("public_upstream", env("TPROXY_PUBLIC_UPSTREAM"), ""),
            StaticRoutesLegacy = Ov("static_routes", env("TPROXY_STATIC_ROUTES"), "exact") == "legacy",
            TokenKeyPath = Ov("token_key_file", env("TPROXY_TOKEN_KEY_PATH"), "token.key"),
            KeysDbPath = Ov("keys_db", env("TPROXY_KEYS_DB"), "keys.db"),
            Secret = secret,
            ListenPort = int.Parse(listen),
            AdminPort = int.Parse(admin),
            ListenAddress = listenAddr,
            AdminAddress = adminAddr,
            MaxSessionsGlobal = Int(Ov("limits.max_sessions_global", env("TPROXY_MAX_SESSIONS"), "128")),
            MaxStreamsGlobal = Int(Ov("limits.max_streams_global", env("TPROXY_MAX_STREAMS"), "4096")),
            MaxStreamsPerSession = Int(Ov("limits.max_streams_per_session", env("TPROXY_MAX_STREAMS_PER_SESSION"), "128")),
            MaxBackendDialsInFlight = Int(Ov("limits.max_backend_dials_in_flight", env("TPROXY_MAX_DIALS_IN_FLIGHT"), "256")),
            MaxBootstrapsGlobal = Int(Ov("limits.max_bootstraps_global", env("TPROXY_MAX_BOOTSTRAPS"), "512")),
            MaxPendingBytesGlobal = Long(Ov("limits.max_pending_global_bytes", env("TPROXY_MAX_PENDING_GLOBAL_BYTES"), (512 * 1024 * 1024).ToString())),
            MaxPendingItemsGlobal = Long(Ov("limits.max_pending_global_items", env("TPROXY_MAX_PENDING_GLOBAL_ITEMS"), "262144")),
            MaxPendingBytesPerSession = Long(Ov("limits.max_pending_per_session", env("TPROXY_MAX_PENDING_PER_SESSION_BYTES"), (32 * 1024 * 1024).ToString())),
            MaxFramePayload = Int(Ov("limits.max_frame_payload", env("TPROXY_MAX_FRAME_PAYLOAD"), (1024 * 1024).ToString())),
            DownBatchTargetBytes = Int(Ov("limits.carrier_batch_bytes", env("TPROXY_CARRIER_BATCH_BYTES"), (2 * 1024 * 1024).ToString())),
            NewSessionsPerMinute = Dbl(Ov("limits.new_sessions_per_minute", env("TPROXY_NEW_SESSIONS_PER_MINUTE"), "600")),
            NewSessionsBurst = Int(Ov("limits.new_sessions_burst", env("TPROXY_NEW_SESSIONS_BURST"), "128")),
            NewStreamsPerMinute = Dbl(Ov("limits.new_streams_per_minute", env("TPROXY_NEW_STREAMS_PER_MINUTE"), "6000")),
            NewStreamsBurst = Int(Ov("limits.new_streams_burst", env("TPROXY_NEW_STREAMS_BURST"), "512")),
            NewBootstrapsPerMinute = Dbl(Ov("limits.new_bootstraps_per_minute", env("TPROXY_NEW_BOOTSTRAPS_PER_MINUTE"), "1200")),
            NewBootstrapsBurst = Int(Ov("limits.new_bootstraps_burst", env("TPROXY_NEW_BOOTSTRAPS_BURST"), "256")),
            MaxSessionsPerIp = Int(Ov("limits.max_sessions_per_ip", env("TPROXY_MAX_SESSIONS_PER_IP"), "0")),
            MaxBootstrapsPerIp = Int(Ov("limits.max_bootstraps_per_ip", env("TPROXY_MAX_BOOTSTRAPS_PER_IP"), "0")),
            LongPollSeconds = Int(Ov("timeouts.long_poll_seconds", env("TPROXY_LONG_POLL_SECONDS"), "25")),
            BootstrapTtlSeconds = Int(Ov("timeouts.bootstrap_ttl_seconds", env("TPROXY_BOOTSTRAP_TTL_SECONDS"), "120")),
            ReconnectGraceSeconds = Int(Ov("timeouts.reconnect_grace_seconds", env("TPROXY_RECONNECT_GRACE") ?? env("TPROXY_SESSION_IDLE_TTL"), "120")),
            RequireHost = Ov("require_host", env("TPROXY_REQUIRE_HOST"), "true") is not ("false" or "0"),
            ManagementApi = new ManagementApiConfig(
                Ov("management.api.enabled", env("TPROXY_API_ENABLED"), "false") is "true" or "1",
                Ov("management.api.admin_token", env("TPROXY_API_TOKEN"), "")),
            ManagementBot = new ManagementBotConfig(
                Ov("management.bot.enabled", env("TPROXY_BOT_ENABLED"), "false") is "true" or "1",
                Ov("management.bot.token", env("TPROXY_BOT_TOKEN"), ""),
                Ov("management.bot.admin_chat_ids", env("TPROXY_BOT_ADMINS"), "")),
        };
        opt.Validate();
        return opt;
    }

    private void Validate()
    {
        if (!BasePaths.IsValid(BasePath))
            throw new InvalidOperationException(
                "base_path must be one or more '/'-separated segments of [A-Za-z0-9][A-Za-z0-9_-]*, at most 128 characters");
        if (CarrierMode is not ("https" or "websocket" or "https-lanes" or "websocket-lanes"))
            throw new InvalidOperationException($"unsupported carrier mode {CarrierMode}");
        // 2 MiB is the desktop client's loopback-fallback message cap.
        if (DownBatchTargetBytes is < 4096 or > 2 * 1024 * 1024)
            throw new InvalidOperationException("carrier_batch_bytes must be between 4 KiB and 2 MiB");
        if (MaxFramePayload is < 64 or > 1024 * 1024)
            throw new InvalidOperationException("max_frame_payload must be between 64 and 1048576");
        if (MaxSessionsPerIp < 0 || MaxBootstrapsPerIp < 0)
            throw new InvalidOperationException("per-IP limits must be >= 0 (0 disables)");
        // Startup reserve: the per-session control reserve (one coalesced WINDOW
        // plus one CLOSE per stream, each charged FrameCost) multiplied by
        // max_sessions_global must leave the global pending budget room for data.
        var controlItems = 2L * MaxStreamsPerSession + 8;
        var controlBytes = controlItems * (8 + RelayHub.ItemOverhead);
        if (controlBytes * MaxSessionsGlobal > MaxPendingBytesGlobal / 2)
            throw new InvalidOperationException(
                $"per-session control reserve ({controlBytes} bytes) x max_sessions_global ({MaxSessionsGlobal}) leaves max_pending_global_bytes no room for data");
        if (controlItems * MaxSessionsGlobal > MaxPendingItemsGlobal / 2)
            throw new InvalidOperationException(
                $"per-session control reserve ({controlItems} items) x max_sessions_global ({MaxSessionsGlobal}) leaves max_pending_global_items no room for data");
    }

    private static int Int(string s) => int.Parse(s);
    private static long Long(string s) => long.Parse(s);
    private static double Dbl(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
}
