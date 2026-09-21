using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;

namespace TproxyRelay;

/// <summary>
/// Owns the full application composition, extracted from the top-level
/// Program so integration tests can boot the real pipeline with their own
/// environment (free ports, temp key store, echo backend).
/// </summary>
public static class RelayApp
{
    public static WebApplication Build(string[] args, Func<string, string?> env)
    {
        var opt = RelayOptions.Load(env);

        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
        // Request logs at Information would leak bridge URLs (capability!) into
        // container logs; keep framework noise at Warning.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(k =>
        {
            if (opt.ListenAddress is { } la) k.Listen(la, opt.ListenPort);
            else k.ListenAnyIP(opt.ListenPort);
            if (opt.AdminAddress is { } aa) k.Listen(aa, opt.AdminPort);
            else k.Listen(IPAddress.Loopback, opt.AdminPort); // SEC-001: admin defaults to loopback
            k.Limits.MaxRequestBodySize = 4 * 1024 * 1024;
            k.Limits.MaxRequestLineSize = 16 * 1024;
            // PERF-008: scale with the configured session/stream budgets instead of
            // hard-coded 256/64 (the websocket carriers hold one upgraded connection
            // per session — ws-lanes even one per stream).
            k.Limits.MaxConcurrentConnections = Math.Max(256, opt.MaxSessionsGlobal * 2);
            k.Limits.MaxConcurrentUpgradedConnections = Math.Max(64, opt.MaxStreamsGlobal);
        });
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

        var minter = new TokenMinter(TokenMinter.LoadOrCreateKey(opt.TokenKeyPath));
        // Managed keys: SQLite registry + seed import + the registry file the
        // MTProxy container supervisor reconciles against. The environment secret
        // remains the built-in profile on the default backend port.
        var store = new KeyStore(opt.KeysDbPath);
        var seeded = store.ImportSeed(Path.Combine(store.DataDirectory, "seed.json"));
        var builtinProfile = new RelayProfile("builtin", "builtin", opt.Secret,
            opt.BackendHostName, opt.BackendPort, opt.CarrierMode);
        var registry = new ProfileRegistry(opt, builtinProfile);
        registry.ReplaceManaged(store.ListKeys(includeRevoked: false).Where(k => k.Active).Select(k =>
            new RelayProfile(k.Id, k.Name, Convert.FromHexString(k.SecretHex),
                opt.BackendHostName, k.BackendPort, opt.CarrierMode)));

        // Registered as factories so the hub gets the real host logger once the
        // container exists (and tests can resolve everything from app.Services).
        builder.Services.AddSingleton(opt);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton<RelayHub>(_ => new RelayHub(
            opt, minter, _.GetRequiredService<ILogger<RelayHub>>(), store));

        var app = builder.Build();
        var hub = app.Services.GetRequiredService<RelayHub>();
        var publicContent = PublicContent.Create(opt, app.Logger);
        if (seeded.Count > 0)
            app.Logger.LogInformation("event=keys_seeded count={Count}", seeded.Count);
        store.ExportRegistry(Convert.ToHexString(opt.Secret).ToLowerInvariant());
        AdminApi.Map(app, opt, hub, store, registry);
        TelegramBot.ValidateAndRegister(app, opt, hub, store, registry);
        _ = hub.StartReaper(app.Lifetime.ApplicationStopping);
        // Graceful shutdown: close all sessions so carriers observe a clean end
        // (Close frames / cancelled polls) instead of TCP resets; long polls (25s)
        // fit into the explicit 30s drain window.
        app.Lifetime.ApplicationStopping.Register(() => hub.CloseAllSessions("shutdown"));

        app.Logger.LogInformation(
            "event=started hostname={Host} backend={Backend} carrier={Mode} listen={Port} admin={AdminPort}",
            opt.PublicHostname, opt.BackendHost, opt.CarrierMode, opt.ListenPort, opt.AdminPort);

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

        // Base-path routing (BASE_PATH.md): at the root the web path is "/", with a
        // prefix every transport endpoint moves under "/<base>/". Only the
        // trailing-slash form is served; "/<base>" without the slash is not
        // special-cased and gets the ordinary public 404.
        var webRoot = BasePaths.WebPath(opt.BasePath);

        // Admin listener lives on its own loopback port and never sees public traffic.
        // /admin/* (when enabled) falls through to the AdminApi endpoints below.
        app.Use(async (ctx, next) =>
        {
            if (ctx.Connection.LocalPort == opt.AdminPort && !ctx.Request.Path.StartsWithSegments("/admin"))
            {
                switch (ctx.Request.Path.Value)
                {
                    case "/healthz":
                        ctx.Response.StatusCode = 200;
                        await ctx.Response.WriteAsync("ok\n");
                        return;
                    case "/readyz":
                        ctx.Response.StatusCode = await BackendReachable(opt) ? 200 : 503;
                        await ctx.Response.WriteAsync(ctx.Response.StatusCode == 200 ? "ready\n" : "backend unreachable\n");
                        return;
                    case "/metrics":
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "text/plain; version=0.0.4";
                        await ctx.Response.WriteAsync(Counters.Render());
                        return;
                    default:
                        ctx.Response.StatusCode = 404;
                        return;
                }
            }
            // Carrier surface hygiene (PROTOCOL.md): HTTP API requests carry no
            // cookies, and capacity accounting needs exactly one client address.
            if (ctx.Request.Path.StartsWithSegments(webRoot + "api/v1"))
            {
                if (ctx.Request.Headers.ContainsKey("Cookie"))
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.Headers["X-Error"] = "cookie_rejected";
                    return;
                }
            }
            var ip = ClientIp(ctx);
            if (ip is null)
            {
                // X-Forwarded-For carried a list or an unparsable value.
                ctx.Response.StatusCode = 400;
                ctx.Response.Headers["X-Error"] = "bad_xff";
                return;
            }
            ctx.Items["ClientIp"] = ip;
            await next();
        });

        // -- public root: bridge selection or ordinary site -------------------------

        app.MapGet(webRoot, async (HttpContext ctx) =>
        {
            if (!HostOk(ctx, opt))
            {
                await publicContent.NotFoundAsync(ctx);
                return;
            }
            if (ExactBridgeQuery(ctx, out var value))
            {
                var profile = registry.Match(value);
                if (profile != null)
                {
                    var bootstrap = hub.MintBootstrap(ctx.Items["ClientIp"] as IPAddress, profile);
                    if (bootstrap == null)
                    {
                        ctx.Response.StatusCode = 503; // bootstrap cap or creation-rate bucket
                        ctx.Response.Headers["Retry-After"] = "2";
                        return;
                    }
                    await BridgePage.Write(ctx, bootstrap, profile.CarrierMode, opt.PublicHostname, opt.BasePath);
                    return;
                }
            }
            if (opt.BasePath.Length > 0)
            {
                // With a base path the root is not a transport path. An authentic
                // capability offered here fails locally with an uncacheable 404;
                // everything else is the public home page.
                if (ExactBridgeQuery(ctx, out var v2) && registry.Match(v2) != null)
                    await publicContent.NotFoundAsync(ctx);
                else
                    await publicContent.HomeAsync(ctx);
                return;
            }
            await publicContent.HomeAsync(ctx); // missing/wrong/augmented bridge query: same home page
        });

        // -- carrier API -------------------------------------------------------------

        app.MapPost(webRoot + "api/v1/session", async (HttpContext ctx) =>
        {
            if (!HostOk(ctx, opt))
            {
                await publicContent.FallbackAsync(ctx);
                return;
            }
            var token = Bearer(ctx);
            if (token == null || !minter.TryValidate(token, TokenMinter.KindBootstrap))
            {
                await publicContent.FallbackAsync(ctx); // random credentials follow the public site
                return;
            }
            if (ctx.Request.ContentLength is > 64)
            {
                ctx.Response.StatusCode = 400; // create body is a single HELLO frame
                ctx.Response.Headers["X-Error"] = "body_too_large";
                return;
            }
            // Small body (≤64 B): a plain exact-size read, no pooling needed.
            var hello = new byte[64];
            var helloLen = 0;
            int n2;
            while ((n2 = await ctx.Request.Body.ReadAsync(hello.AsMemory(helloLen), ctx.RequestAborted)) > 0)
            {
                helloLen += n2;
                if (helloLen > 64)
                {
                    ctx.Response.StatusCode = 400; // oversized, chunked included
                    ctx.Response.Headers["X-Error"] = "body_too_large";
                    return;
                }
            }
            var body = hello[..helloLen];
            if (!FrameCodec.IsValidHello(body))
            {
                app.Logger.LogWarning("event=session_400 reason=invalid_hello len={Len}", body.Length);
                ctx.Response.StatusCode = 400;
                ctx.Response.Headers["X-Error"] = "invalid_hello";
                return;
            }
            switch (hub.TryRedeemBootstrap(token, body, ctx.Items["ClientIp"] as IPAddress, out var session))
            {
                case RedeemResult.Ok when session != null:
                    ctx.Response.StatusCode = 200;
                    ctx.Response.Headers["X-Session-Token"] = session.Token;
                    ctx.Response.Headers["X-Down-Cursor"] = "0";
                    // API-006: the session's carrier, not the process-wide default.
                    ctx.Response.Headers["X-Carrier-Mode"] = session.CarrierMode;
                    ctx.Response.Headers.CacheControl = "no-store";
                    ctx.Response.ContentType = "application/octet-stream";
                    await ctx.Response.Body.WriteAsync(FrameCodec.Encode(FrameType.Welcome, 0, []));
                    return;
                case RedeemResult.RetryLater:
                    ctx.Response.StatusCode = 503;
                    ctx.Response.Headers["Retry-After"] = "1";
                    return;
                default:
                    app.Logger.LogWarning("event=session_400 reason=redeem_invalid");
                    ctx.Response.StatusCode = 400;
                    ctx.Response.Headers["X-Error"] = "redeem_invalid";
                    return;
            }
        });

        app.MapPost(webRoot + "api/v1/up", async (HttpContext ctx) =>
        {
            if (!HostOk(ctx, opt))
            {
                await publicContent.FallbackAsync(ctx);
                return;
            }
            var session = hub.AuthSession(Bearer(ctx));
            if (session == null)
            {
                await publicContent.FallbackAsync(ctx);
                return;
            }
            if (!ctx.Request.Headers.TryGetValue("X-Up-Seq", out var seqStr) ||
                !int.TryParse(seqStr, out var seq) || seq < 1)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Headers["X-Error"] = "bad_seq";
                return;
            }
            if (ctx.Request.ContentLength is > 2 * 1024 * 1024)
            {
                ctx.Response.StatusCode = 413;
                ctx.Response.Headers["X-Error"] = "body_too_large";
                return;
            }
            var batch = await ReadBodyCapped(ctx, 2 * 1024 * 1024);
            if (batch == null)
            {
                // API-009/SEC-005: chunked oversize fails fast instead of
                // buffering megabytes before the refusal.
                ctx.Response.StatusCode = 413;
                ctx.Response.Headers["X-Error"] = "body_too_large";
                return;
            }
            try
            {
                var laneId = ParseLaneId(ctx, session.LanesMode);
                if (laneId < 0)
                {
                    ctx.Response.StatusCode = 400; // lanes mode requires a valid X-Lane-ID
                    ctx.Response.Headers["X-Error"] = "bad_lane_header";
                    return;
                }
                if (session.LanesMode)
                {
                    var laneResult = await hub.ApplyUpLane(session, (uint)laneId, seq, batch.Payload);
                    switch (laneResult.Outcome)
                    {
                        case LaneOutcome.Ok or LaneOutcome.LaneClosed:
                            ctx.Response.StatusCode = 204;
                            ctx.Response.Headers["X-Up-Ack"] = laneResult.AckSeq.ToString();
                            return;
                        case LaneOutcome.RetryLater:
                            ctx.Response.StatusCode = 503;
                            ctx.Response.Headers["Retry-After"] = "1";
                            return;
                        default:
                            hub.CloseSession(session, $"uplink error: {laneResult.Error}");
                            ctx.Response.StatusCode = 409;
                            ctx.Response.Headers["X-Error"] = ErrorSlug("uplink", laneResult.Error);
                            return;
                    }
                }
                var result = await hub.ApplyUp(session, seq, batch.Payload);
                switch (result.Outcome)
                {
                    case UpOutcome.Acked or UpOutcome.DuplicateAcked:
                        ctx.Response.StatusCode = 204;
                        ctx.Response.Headers["X-Up-Ack"] = result.AckSeq.ToString();
                        return;
                    case UpOutcome.RetryLater:
                        ctx.Response.StatusCode = 503;
                        ctx.Response.Headers["Retry-After"] = "1";
                        return;
                    default:
                        hub.CloseSession(session, $"uplink error: {result.Error}");
                        ctx.Response.StatusCode = 409;
                        ctx.Response.Headers["X-Error"] = ErrorSlug("uplink", result.Error);
                        return;
                }
            }
            finally
            {
                batch.Return();
            }
        });

        app.MapPost(webRoot + "api/v1/down", async (HttpContext ctx) =>
        {
            if (!HostOk(ctx, opt))
            {
                await publicContent.FallbackAsync(ctx);
                return;
            }
            var session = hub.AuthSession(Bearer(ctx));
            if (session == null)
            {
                await publicContent.FallbackAsync(ctx);
                return;
            }
            if (!ctx.Request.Headers.TryGetValue("X-Down-Cursor", out var curStr) ||
                !long.TryParse(curStr, out var cursor) || cursor < 0)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Headers["X-Error"] = "bad_cursor";
                return;
            }
            var laneId = ParseLaneId(ctx, session.LanesMode);
            if (laneId < 0)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Headers["X-Error"] = "bad_lane_header";
                return;
            }
            var result = session.LanesMode
                ? await hub.GetDownLane(session, (uint)laneId, cursor, ctx.RequestAborted)
                : await hub.GetDown(session, cursor, ctx.RequestAborted);
            if (result.ProtocolError)
            {
                hub.CloseSession(session, "downlink protocol error");
                ctx.Response.StatusCode = 409;
                ctx.Response.Headers["X-Error"] = "protocol_error";
                return;
            }
            if (!result.HasBatch || result.Body == null)
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.Headers["X-Down-Cursor"] = cursor.ToString();
                if (result.LaneClosed)
                    ctx.Response.Headers["X-Lane-Closed"] = "1";
                return;
            }
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers["X-Down-Cursor"] = result.Cursor.ToString();
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.ContentType = "application/octet-stream";
            await ctx.Response.Body.WriteAsync(result.Body.Payload);
        });

        app.MapDelete(webRoot + "api/v1/session", async (HttpContext ctx) =>
        {
            if (!HostOk(ctx, opt))
            {
                await publicContent.FallbackAsync(ctx);
                return;
            }
            var session = hub.AuthSession(Bearer(ctx));
            if (session == null)
            {
                await publicContent.FallbackAsync(ctx);
                return;
            }
            hub.CloseSession(session, "client close");
            ctx.Response.StatusCode = 204;
        });

        app.MapGet(webRoot + "api/v1/ws", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest || !HostOk(ctx, opt))
            {
                await publicContent.FallbackAsync(ctx);
                return;
            }
            const string prefix = "tproxy-v1.";
            const string lanePrefix = "tproxy-lane-v1.";
            var proto = ctx.Request.Headers.SecWebSocketProtocol.ToString();
            if (proto.StartsWith(lanePrefix, StringComparison.Ordinal))
            {
                // websocket-lanes: one socket per stream, tproxy-lane-v1.<token>.<sid>
                var rest = proto[lanePrefix.Length..];
                var dot = rest.IndexOf('.');
                if (dot != 43 || !uint.TryParse(rest[(dot + 1)..], out var sid) || sid == 0)
                {
                    await publicContent.FallbackAsync(ctx);
                    return;
                }
                var session = hub.AuthSession(rest[..dot]);
                if (session == null || !CarrierModes.IsWebSocketLanes(session.CarrierMode) ||
                    session.IsTombstoned(sid) || session.Lanes.TryGetValue(sid, out var l) && l.AttachedSocket != null)
                {
                    await publicContent.FallbackAsync(ctx);
                    return;
                }
                var laneWs = await ctx.WebSockets.AcceptWebSocketAsync(proto);
                await hub.RunWebSocketLane(session, sid, laneWs, ctx.RequestAborted);
                return;
            }
            if (!proto.StartsWith(prefix, StringComparison.Ordinal))
            {
                await publicContent.FallbackAsync(ctx);
                return;
            }
            var token = proto[prefix.Length..];
            var session2 = hub.AuthSession(token);
            if (session2 == null)
            {
                await publicContent.FallbackAsync(ctx);
                return;
            }
            var ws = await ctx.WebSockets.AcceptWebSocketAsync(proto);
            await hub.RunWebSocket(session2, ws, ctx.RequestAborted);
        });

        // An explicit {*path} template (not the :nonfile default) so paths with a
        // dot — /about.html, /logo.png — also reach the public handler.
        app.MapFallback("/{*path}", async (HttpContext ctx) => await publicContent.FallbackAsync(ctx));

        return app;
    }

    // ---- request helpers ---------------------------------------------------------

    /// <summary>
    /// Exactly one accounting address: a single X-Forwarded-For value when present
    /// (a list is rejected — the front proxy must not append), else the socket peer.
    /// </summary>
    private static IPAddress? ClientIp(HttpContext ctx)
    {
        var xff = ctx.Request.Headers["X-Forwarded-For"].ToString();
        if (xff.Length == 0)
            return ctx.Connection.RemoteIpAddress;
        return IPAddress.TryParse(xff.Trim(), out var ip) ? ip : null;
    }

    private static bool HostOk(HttpContext ctx, RelayOptions opt) =>
        !opt.RequireHost ||
        string.Equals(ctx.Request.Host.Host, opt.PublicHostname, StringComparison.OrdinalIgnoreCase);

    private static bool ExactBridgeQuery(HttpContext ctx, out string value)
    {
        value = "";
        var q = ctx.Request.Query;
        if (!(q.Count == 1 && q.ContainsKey("bridge") && q["bridge"].Count == 1))
            return false;
        value = q["bridge"].ToString();
        return value.Length == 43;
    }

    private static string? Bearer(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.Length != 7 + 43) // "Bearer " + 43 chars
            return null;
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        var token = auth[7..];
        foreach (var c in token)
            if (!(c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
                return null;
        return token;
    }

    /// <summary>
    /// X-Lane-ID parsing: returns the lane id, or -1 when the header value is
    /// invalid. In lanes mode the header is mandatory; elsewhere it must be absent.
    /// </summary>
    private static int ParseLaneId(HttpContext ctx, bool lanesMode)
    {
        var present = ctx.Request.Headers.TryGetValue("X-Lane-ID", out var v);
        if (!lanesMode)
            return present ? -1 : 0;
        if (!present || !uint.TryParse(v, out var lane) || lane > FrameCodec.MaxStreamId)
            return -1;
        return (int)lane;
    }

    /// <summary>
    /// SEC-005/PERF-003: stream-read a request body with a hard cap into a
    /// pooled buffer that grows by re-renting (never a MemoryStream plus a
    /// full copy); chunked oversize fails as soon as the cap is crossed.
    /// Returns null when the body exceeds the cap. The caller owns and must
    /// Return() the batch.
    /// </summary>
    private static async Task<PooledBatch?> ReadBodyCapped(HttpContext ctx, int cap)
    {
        var buf = new byte[8192];
        var acc = PooledBatch.Rent(Math.Min(cap, 64 * 1024));
        acc.Length = 0; // Rent() pre-sets Length to the rented size; we count bytes ourselves
        try
        {
            int n;
            while ((n = await ctx.Request.Body.ReadAsync(buf, ctx.RequestAborted)) > 0)
            {
                if (acc.Length + n > cap)
                {
                    acc.Return();
                    return null;
                }
                if (acc.Length + n > acc.Buffer.Length)
                {
                    // Grow: rent bigger, copy, return the old buffer.
                    var grown = 0;
                    while (grown < acc.Length + n)
                        grown = checked(grown * 2 + 64 * 1024);
                    var bigger = PooledBatch.Rent(Math.Min(grown, cap));
                    acc.Buffer.AsSpan(0, acc.Length).CopyTo(bigger.Buffer);
                    // Rent() pre-sets Length to the requested capacity; the
                    // new batch must carry the OLD byte count.
                    bigger.Length = acc.Length;
                    acc.Return();
                    acc = bigger;
                }
                buf.AsSpan(0, n).CopyTo(acc.Buffer.AsSpan(acc.Length, n));
                acc.Length += n;
            }
            return acc;
        }
        catch
        {
            acc.Return();
            throw;
        }
    }

    /// <summary>API-008: short machine-readable reason for X-Error headers.</summary>
    private static string ErrorSlug(string kind, string? message)
    {
        const int max = 64;
        var slug = string.IsNullOrEmpty(message) ? "error" : new string(message.Take(max).ToArray());
        return $"{kind}:{slug}";
    }

    private static async Task<bool> BackendReachable(RelayOptions opt)
    {
        try
        {
            using var cts = new CancellationTokenSource(1500);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(opt.BackendHostName, opt.BackendPort, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
