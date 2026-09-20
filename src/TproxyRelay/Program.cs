using System.Net;
using System.Net.Sockets;
using TproxyRelay;

var opt = RelayOptions.Load();

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
    else k.ListenAnyIP(opt.AdminPort);
    k.Limits.MaxRequestBodySize = 4 * 1024 * 1024;
    k.Limits.MaxRequestLineSize = 16 * 1024;
    k.Limits.MaxConcurrentConnections = 256;
    k.Limits.MaxConcurrentUpgradedConnections = 64;
});
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

var app = builder.Build();

var minter = new TokenMinter(TokenMinter.LoadOrCreateKey(opt.TokenKeyPath));
var hub = new RelayHub(opt, minter, app.Logger);
_ = hub.StartReaper(app.Lifetime.ApplicationStopping);
// Graceful shutdown: close all sessions so carriers observe a clean end
// (Close frames / cancelled polls) instead of TCP resets; long polls (25s)
// fit into the explicit 30s drain window.
app.Lifetime.ApplicationStopping.Register(() => hub.CloseAllSessions("shutdown"));

app.Logger.LogInformation(
    "event=started hostname={Host} backend={Backend} carrier={Mode} listen={Port} admin={AdminPort}",
    opt.PublicHostname, opt.BackendHost, opt.CarrierMode, opt.ListenPort, opt.AdminPort);

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

// Admin listener lives on its own loopback port and never sees public traffic.
app.Use(async (ctx, next) =>
{
    if (ctx.Connection.LocalPort == opt.AdminPort)
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
    if (ctx.Request.Path.StartsWithSegments("/api/v1"))
    {
        if (ctx.Request.Headers.ContainsKey("Cookie"))
        {
            ctx.Response.StatusCode = 400;
            return;
        }
    }
    var ip = ClientIp(ctx);
    if (ip is null)
    {
        // X-Forwarded-For carried a list or an unparsable value.
        ctx.Response.StatusCode = 400;
        return;
    }
    ctx.Items["ClientIp"] = ip;
    await next();
});

/// <summary>
/// Exactly one accounting address: a single X-Forwarded-For value when present
/// (a list is rejected — the front proxy must not append), else the socket peer.
/// </summary>
static IPAddress? ClientIp(HttpContext ctx)
{
    var xff = ctx.Request.Headers["X-Forwarded-For"].ToString();
    if (xff.Length == 0)
        return ctx.Connection.RemoteIpAddress;
    return IPAddress.TryParse(xff.Trim(), out var ip) ? ip : null;
}

bool HostOk(HttpContext ctx) =>
    !opt.RequireHost ||
    string.Equals(ctx.Request.Host.Host, opt.PublicHostname, StringComparison.OrdinalIgnoreCase);

// Base-path routing (BASE_PATH.md): at the root the web path is "/", with a
// prefix every transport endpoint moves under "/<base>/". Only the
// trailing-slash form is served; "/<base>" without the slash is not
// special-cased and gets the ordinary public 404.
var webRoot = BasePaths.WebPath(opt.BasePath);
bool ExactBridgeQuery(HttpContext ctx, out string value)
{
    value = "";
    var q = ctx.Request.Query;
    if (!(q.Count == 1 && q.ContainsKey("bridge") && q["bridge"].Count == 1))
        return false;
    value = q["bridge"].ToString();
    return value.Length == 43;
}

static string? Bearer(HttpContext ctx)
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

// -- public root: bridge selection or ordinary site -------------------------

app.MapGet(webRoot, async (HttpContext ctx) =>
{
    if (!HostOk(ctx))
    {
        await PublicSite.NotFound(ctx);
        return;
    }
    if (ExactBridgeQuery(ctx, out var value) &&
        CapabilityDeriver.Matches(value, opt.CapabilityBytes))
    {
        var bootstrap = hub.MintBootstrap(ctx.Items["ClientIp"] as IPAddress);
        if (bootstrap == null)
        {
            ctx.Response.StatusCode = 503; // bootstrap cap or creation-rate bucket
            ctx.Response.Headers["Retry-After"] = "2";
            return;
        }
        await BridgePage.Write(ctx, bootstrap, opt.CarrierMode, opt.PublicHostname, opt.BasePath);
        return;
    }
    if (opt.BasePath.Length > 0)
    {
        // With a base path the root is not a transport path. An authentic
        // capability offered here fails locally with an uncacheable 404;
        // everything else is the public home page.
        if (ExactBridgeQuery(ctx, out value))
            await PublicSite.NotFound(ctx);
        else
            await PublicSite.Index(ctx);
        return;
    }
    await PublicSite.Index(ctx); // missing/wrong/augmented bridge query: same home page
});

// -- carrier API -------------------------------------------------------------

app.MapPost(webRoot + "api/v1/session", async (HttpContext ctx) =>
{
    if (!HostOk(ctx))
    {
        await PublicSite.NotFound(ctx);
        return;
    }
    var token = Bearer(ctx);
    if (token == null || !minter.TryValidate(token, TokenMinter.KindBootstrap))
    {
        await PublicSite.NotFound(ctx); // random credentials follow the public site
        return;
    }
    if (ctx.Request.ContentLength is > 64)
    {
        ctx.Response.StatusCode = 400; // create body is a single HELLO frame
        return;
    }
    using var ms = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
    if (ms.Length > 64)
    {
        ctx.Response.StatusCode = 400; // also catches oversized chunked bodies
        return;
    }
    var body = ms.ToArray();
    if (!FrameCodec.IsValidHello(body))
    {
        app.Logger.LogWarning("event=session_400 reason=invalid_hello len={Len}", body.Length);
        ctx.Response.StatusCode = 400;
        return;
    }
    switch (hub.TryRedeemBootstrap(token, body, ctx.Items["ClientIp"] as IPAddress, out var session))
    {
        case RedeemResult.Ok when session != null:
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers["X-Session-Token"] = session.Token;
            ctx.Response.Headers["X-Down-Cursor"] = "0";
            ctx.Response.Headers["X-Carrier-Mode"] = opt.CarrierMode;
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
            return;
    }
});

app.MapPost(webRoot + "api/v1/up", async (HttpContext ctx) =>
{
    if (!HostOk(ctx))
    {
        await PublicSite.NotFound(ctx);
        return;
    }
    var session = hub.AuthSession(Bearer(ctx));
    if (session == null)
    {
        await PublicSite.NotFound(ctx);
        return;
    }
    if (!ctx.Request.Headers.TryGetValue("X-Up-Seq", out var seqStr) ||
        !int.TryParse(seqStr, out var seq) || seq < 1)
    {
        ctx.Response.StatusCode = 400;
        return;
    }
    if (ctx.Request.ContentLength is > 2 * 1024 * 1024)
    {
        ctx.Response.StatusCode = 413;
        return;
    }
    using var ms = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(ms);
    var body = ms.ToArray();
    var laneId = ParseLaneId(ctx, session.LanesMode);
    if (laneId < 0)
    {
        ctx.Response.StatusCode = 400; // lanes mode requires a valid X-Lane-ID
        return;
    }
    if (session.LanesMode)
    {
        var laneResult = await hub.ApplyUpLane(session, (uint)laneId, seq, body);
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
                return;
        }
    }
    var result = await hub.ApplyUp(session, seq, body);
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
            return;
    }
});

app.MapPost(webRoot + "api/v1/down", async (HttpContext ctx) =>
{
    if (!HostOk(ctx))
    {
        await PublicSite.NotFound(ctx);
        return;
    }
    var session = hub.AuthSession(Bearer(ctx));
    if (session == null)
    {
        await PublicSite.NotFound(ctx);
        return;
    }
    if (!ctx.Request.Headers.TryGetValue("X-Down-Cursor", out var curStr) ||
        !long.TryParse(curStr, out var cursor) || cursor < 0)
    {
        ctx.Response.StatusCode = 400;
        return;
    }
    var laneId = ParseLaneId(ctx, session.LanesMode);
    if (laneId < 0)
    {
        ctx.Response.StatusCode = 400;
        return;
    }
    var result = session.LanesMode
        ? await hub.GetDownLane(session, (uint)laneId, cursor, ctx.RequestAborted)
        : await hub.GetDown(session, cursor, ctx.RequestAborted);
    if (result.ProtocolError)
    {
        hub.CloseSession(session, "downlink protocol error");
        ctx.Response.StatusCode = 409;
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
    await ctx.Response.Body.WriteAsync(result.Body);
});

app.MapDelete(webRoot + "api/v1/session", async (HttpContext ctx) =>
{
    if (!HostOk(ctx))
    {
        await PublicSite.NotFound(ctx);
        return;
    }
    var session = hub.AuthSession(Bearer(ctx));
    if (session == null)
    {
        await PublicSite.NotFound(ctx);
        return;
    }
    hub.CloseSession(session, "client close");
    ctx.Response.StatusCode = 204;
});

app.MapGet(webRoot + "api/v1/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest || !HostOk(ctx))
    {
        await PublicSite.NotFound(ctx);
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
            await PublicSite.NotFound(ctx);
            return;
        }
        var session = hub.AuthSession(rest[..dot]);
        if (session == null || session.CarrierMode != "websocket-lanes" ||
            session.IsTombstoned(sid) || session.Lanes.TryGetValue(sid, out var l) && l.AttachedSocket != null)
        {
            await PublicSite.NotFound(ctx);
            return;
        }
        var laneWs = await ctx.WebSockets.AcceptWebSocketAsync(proto);
        await hub.RunWebSocketLane(session, sid, laneWs, ctx.RequestAborted);
        return;
    }
    if (!proto.StartsWith(prefix, StringComparison.Ordinal))
    {
        await PublicSite.NotFound(ctx);
        return;
    }
    var token = proto[prefix.Length..];
    var session2 = hub.AuthSession(token);
    if (session2 == null)
    {
        await PublicSite.NotFound(ctx);
        return;
    }
    var ws = await ctx.WebSockets.AcceptWebSocketAsync(proto);
    await hub.RunWebSocket(session2, ws, ctx.RequestAborted);
});

app.MapFallback(async (HttpContext ctx) => await PublicSite.NotFound(ctx));

app.Run();

/// <summary>
/// X-Lane-ID parsing: returns the lane id, or -1 when the header value is
/// invalid. In lanes mode the header is mandatory; elsewhere it must be absent.
/// </summary>
static int ParseLaneId(HttpContext ctx, bool lanesMode)
{
    var present = ctx.Request.Headers.TryGetValue("X-Lane-ID", out var v);
    if (!lanesMode)
        return present ? -1 : 0;
    if (!present || !uint.TryParse(v, out var lane) || lane > FrameCodec.MaxStreamId)
        return -1;
    return (int)lane;
}

static async Task<bool> BackendReachable(RelayOptions opt)
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
