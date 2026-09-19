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
builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(opt.ListenPort);
    k.ListenAnyIP(opt.AdminPort);
    k.Limits.MaxRequestBodySize = 4 * 1024 * 1024;
    k.Limits.MaxRequestLineSize = 16 * 1024;
});

var app = builder.Build();

var minter = new TokenMinter(TokenMinter.LoadOrCreateKey(opt.TokenKeyPath));
var hub = new RelayHub(opt, minter, app.Logger);
_ = hub.StartReaper(app.Lifetime.ApplicationStopping);

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
    await next();
});

bool HostOk(HttpContext ctx) =>
    !opt.RequireHost ||
    string.Equals(ctx.Request.Host.Host, opt.PublicHostname, StringComparison.OrdinalIgnoreCase);

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

app.MapGet("/", async (HttpContext ctx) =>
{
    if (!HostOk(ctx))
    {
        await PublicSite.NotFound(ctx);
        return;
    }
    var q = ctx.Request.Query;
    var exact = q.Count == 1 && q.ContainsKey("bridge") && q["bridge"].Count == 1;
    var value = exact ? q["bridge"].ToString() : "";
    if (exact && value.Length == 43 && CapabilityDeriver.Matches(value, opt.CapabilityBytes))
    {
        var bootstrap = hub.MintBootstrap();
        await BridgePage.Write(ctx, bootstrap, opt.CarrierMode, opt.PublicHostname);
        return;
    }
    await PublicSite.Index(ctx); // missing/wrong/augmented bridge query: same home page
});

// -- carrier API -------------------------------------------------------------

app.MapPost("/api/v1/session", async (HttpContext ctx) =>
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
    await ctx.Request.Body.CopyToAsync(ms);
    var body = ms.ToArray();
    if (!FrameCodec.IsValidHello(body))
    {
        app.Logger.LogWarning("event=session_400 reason=invalid_hello len={Len} hex={Hex}", body.Length, Convert.ToHexString(body));
        ctx.Response.StatusCode = 400;
        return;
    }
    switch (hub.TryRedeemBootstrap(token, body, out var session))
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
            app.Logger.LogWarning("event=session_400 reason=redeem_invalid bootstraps={Count}", hub.BootstrapCount);
            ctx.Response.StatusCode = 400;
            return;
    }
});

app.MapPost("/api/v1/up", async (HttpContext ctx) =>
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

app.MapPost("/api/v1/down", async (HttpContext ctx) =>
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
    var result = await hub.GetDown(session, cursor, ctx.RequestAborted);
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
        return;
    }
    ctx.Response.StatusCode = 200;
    ctx.Response.Headers["X-Down-Cursor"] = result.Cursor.ToString();
    ctx.Response.Headers.CacheControl = "no-store";
    ctx.Response.ContentType = "application/octet-stream";
    await ctx.Response.Body.WriteAsync(result.Body);
});

app.MapDelete("/api/v1/session", async (HttpContext ctx) =>
{
    var session = hub.AuthSession(Bearer(ctx));
    if (session == null)
    {
        await PublicSite.NotFound(ctx);
        return;
    }
    hub.CloseSession(session, "client close");
    ctx.Response.StatusCode = 204;
});

app.MapGet("/api/v1/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest || !HostOk(ctx))
    {
        await PublicSite.NotFound(ctx);
        return;
    }
    const string prefix = "tproxy-v1.";
    var proto = ctx.Request.Headers.SecWebSocketProtocol.ToString();
    if (!proto.StartsWith(prefix, StringComparison.Ordinal))
    {
        await PublicSite.NotFound(ctx);
        return;
    }
    var token = proto[prefix.Length..];
    var session = hub.AuthSession(token);
    if (session == null)
    {
        await PublicSite.NotFound(ctx);
        return;
    }
    var ws = await ctx.WebSockets.AcceptWebSocketAsync(proto);
    await hub.RunWebSocket(session, ws, ctx.RequestAborted);
});

app.MapFallback(async (HttpContext ctx) => await PublicSite.NotFound(ctx));

app.Run();

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
