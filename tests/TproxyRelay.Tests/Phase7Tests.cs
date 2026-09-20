using System.Net;
using System.Net.Http;
using System.Text;
using Xunit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using TproxyRelay;

namespace TproxyRelay.Tests;

public sealed class StaticSiteTests : IDisposable
{
    private readonly string _dir;

    public StaticSiteTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tproxy-site-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_dir, "img"));
        File.WriteAllText(Path.Combine(_dir, "index.html"), "<h1>home</h1>");
        File.WriteAllText(Path.Combine(_dir, "404.html"), "<h1>custom 404</h1>");
        File.WriteAllText(Path.Combine(_dir, "about.html"), "<h1>about page</h1>");
        File.WriteAllText(Path.Combine(_dir, "styles.css"), "body{}");
        File.WriteAllText(Path.Combine(_dir, "img", "logo.png"), "PNGDATA");
    }

    [Fact]
    public async Task Home_Serves_Index()
    {
        var site = new StaticSite(_dir, legacyAliases: false, NullLogger.Instance);
        var (status, body, _) = await InvokeAsync(site.HomeAsync);
        Assert.Equal(200, status);
        Assert.Contains("home", body);
    }

    [Fact]
    public async Task Fallback_Serves_Exact_Paths_With_ContentType()
    {
        var site = new StaticSite(_dir, legacyAliases: false, NullLogger.Instance);
        var (_, body, _) = await InvokeAsync(ctx => site.FallbackAsync(ctx), "/about.html");
        Assert.Contains("about page", body);
        var (_, css, cssHeaders) = await InvokeAsync(ctx => site.FallbackAsync(ctx), "/styles.css");
        Assert.Contains("body", css);
        Assert.Equal("text/css; charset=utf-8", cssHeaders.ContentType.ToString());
        var (_, png, pngCt) = await InvokeAsync(ctx => site.FallbackAsync(ctx), "/img/logo.png");
        Assert.Equal("PNGDATA", png);
        Assert.Equal("image/png", pngCt.ContentType.ToString());
    }

    [Fact]
    public async Task Missing_Path_Serves_Custom_404()
    {
        var site = new StaticSite(_dir, legacyAliases: false, NullLogger.Instance);
        var (status, body, _) = await InvokeAsync(ctx => site.FallbackAsync(ctx), "/nope.html");
        Assert.Equal(404, status);
        Assert.Contains("custom 404", body);
    }

    [Fact]
    public async Task Conditional_Get_Returns_304()
    {
        var site = new StaticSite(_dir, legacyAliases: false, NullLogger.Instance);
        var (_, _, firstHeaders) = await InvokeAsync(ctx => site.FallbackAsync(ctx), "/about.html");
        var etag = firstHeaders.ETag.ToString();
        Assert.False(string.IsNullOrEmpty(etag));
        var (status304, body304, _) = await InvokeAsync(ctx => site.FallbackAsync(ctx), "/about.html",
            req => req.Headers.IfNoneMatch = etag);
        Assert.Equal(304, status304);
        Assert.Equal("", body304);
    }

    [Fact]
    public async Task Byte_Ranges_Are_Served()
    {
        var site = new StaticSite(_dir, legacyAliases: false, NullLogger.Instance);
        var (s1, b1, h1) = await InvokeAsync(ctx => site.FallbackAsync(ctx), "/styles.css",
            req => req.Headers.Range = new("bytes=0-3"));
        Assert.Equal(206, s1);
        Assert.Equal("body", b1);
        Assert.Equal("bytes 0-3/6", h1.ContentRange.ToString());

        var (s2, b2, _) = await InvokeAsync(ctx => site.FallbackAsync(ctx), "/styles.css",
            req => req.Headers.Range = new("bytes=-2"));
        Assert.Equal(206, s2);
        Assert.Equal("{}", b2.TrimEnd('\r', '\n') is var trimmed && trimmed.Length >= 2 ? trimmed[^2..] : trimmed);

        var (s3, _, h3) = await InvokeAsync(ctx => site.FallbackAsync(ctx), "/styles.css",
            req => req.Headers.Range = new("bytes=999-"));
        Assert.Equal(416, s3);
        Assert.Equal("bytes */6", h3.ContentRange.ToString());
    }

    [Fact]
    public async Task Traversal_Is_Rejected()
    {
        var site = new StaticSite(_dir, legacyAliases: false, NullLogger.Instance);
        var (status, _, _) = await InvokeAsync(ctx => site.FallbackAsync(ctx), "/../etc/passwd");
        Assert.Equal(404, status);
    }

    [Fact]
    public async Task Legacy_Aliases_Map_Extensionless()
    {
        var exact = new StaticSite(_dir, legacyAliases: false, NullLogger.Instance);
        var (s1, _, _) = await InvokeAsync(ctx => exact.FallbackAsync(ctx), "/about");
        Assert.Equal(404, s1);

        var legacy = new StaticSite(_dir, legacyAliases: true, NullLogger.Instance);
        var (s2, b2, _) = await InvokeAsync(ctx => legacy.FallbackAsync(ctx), "/about");
        Assert.Equal(200, s2);
        Assert.Contains("about page", b2);
    }

    [Fact]
    public void Missing_Index_Refuses_Startup()
    {
        var empty = Path.Combine(_dir, "img");
        Assert.Throws<InvalidOperationException>(() => new StaticSite(empty, false, NullLogger.Instance));
    }

    private static async Task<(int Status, string Body, IHeaderDictionary Headers)> InvokeAsync(
        Func<HttpContext, Task> handler, string path = "/", Action<HttpRequest>? setup = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        setup?.Invoke(ctx.Request);
        await handler(ctx);
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8);
        return ((int)ctx.Response.StatusCode, await reader.ReadToEndAsync(), ctx.Response.Headers);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* temp cleanup */ }
    }
}

/// <summary>A minimal loopback HTTP app standing in for the site application.</summary>
public sealed class FakeUpstream : IDisposable
{
    private readonly HttpListener _listener = new();

    public FakeUpstream()
    {
        var port = 40000 + Random.Shared.Next(20000);
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        Port = port;
        _ = AcceptLoop();
    }

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}/";

    private async Task AcceptLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Handle(ctx);
        }
    }

    private static async Task Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.PathAndQuery ?? "/";
        using var reader = new StreamReader(ctx.Request.InputStream);
        var body = await reader.ReadToEndAsync();
        var xff = ctx.Request.Headers["X-Forwarded-For"];
        var resp = $"upstream path={path} body={body} xff={xff} method={ctx.Request.HttpMethod}";
        var bytes = Encoding.UTF8.GetBytes(resp);
        ctx.Response.StatusCode = path.Contains("missing") ? 404 : 200;
        ctx.Response.ContentType = "text/plain";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    public void Dispose() => _listener.Stop();
}

public sealed class UpstreamSiteTests : IDisposable
{
    private readonly FakeUpstream _app = new();

    [Fact]
    public async Task Forwards_Path_Body_And_Single_Xff()
    {
        var site = new UpstreamSite(_app.Url);
        var (status, body, _) = await InvokeAsync(ctx =>
        {
            ctx.Items["ClientIp"] = IPAddress.Parse("203.0.113.9");
            ctx.Request.Method = "POST";
            ctx.Request.Path = "/api/site-data";
            ctx.Request.QueryString = new QueryString("?q=1");
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("payload"));
            ctx.Request.ContentLength = 7;
            ctx.Request.ContentType = "text/plain";
            return site.FallbackAsync(ctx);
        });
        Assert.Equal(200, status);
        Assert.Contains("path=/api/site-data?q=1", body);
        Assert.Contains("body=payload", body);
        Assert.Contains("xff=203.0.113.9", body);
        Assert.Contains("method=POST", body);
    }

    [Fact]
    public async Task NotFound_Forwards_Original_Path_To_The_App()
    {
        var site = new UpstreamSite(_app.Url);
        var (status, body, _) = await InvokeAsync(ctx =>
        {
            ctx.Request.Method = "GET";
            ctx.Request.Path = "/missing-page";
            return site.NotFoundAsync(ctx);
        });
        Assert.Equal(404, status);
        Assert.Contains("path=/missing-page", body);
    }

    public void Dispose() => _app.Dispose();

    private static async Task<(int Status, string Body, IHeaderDictionary Headers)> InvokeAsync(
        Func<HttpContext, Task> handler)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        await handler(ctx);
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8);
        return ((int)ctx.Response.StatusCode, await reader.ReadToEndAsync(), ctx.Response.Headers);
    }
}
