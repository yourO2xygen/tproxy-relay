using System.IO.Hashing;
using System.Net;
using System.Net.Http;
using System.Text;

namespace TproxyRelay;

/// <summary>
/// The ordinary public website every unauthenticated request lands on.
/// Three modes (PUBLIC_SITE.md): the built-in placeholder, a static site
/// loaded once from public_dir at startup (conditional + range handling),
/// or a reverse proxy to one loopback web application (public_upstream).
/// The site is read once at start-up in static mode; restart to refresh.
/// </summary>
public abstract class PublicContent
{
    public abstract Task HomeAsync(HttpContext ctx);
    public abstract Task NotFoundAsync(HttpContext ctx);

    /// <summary>Path-aware public response for an arbitrary unauthenticated URL.</summary>
    public abstract Task FallbackAsync(HttpContext ctx);

    public static PublicContent Create(RelayOptions opt, ILogger log)
    {
        if (!string.IsNullOrEmpty(opt.PublicDir) && !string.IsNullOrEmpty(opt.PublicUpstream))
            throw new InvalidOperationException("public_dir and public_upstream are mutually exclusive");
        if (!string.IsNullOrEmpty(opt.PublicDir))
            return new StaticSite(opt.PublicDir, opt.StaticRoutesLegacy, log);
        if (!string.IsNullOrEmpty(opt.PublicUpstream))
            return new UpstreamSite(opt.PublicUpstream);
        return new BuiltInSite();
    }
}

public sealed class BuiltInSite : PublicContent
{
    public override Task HomeAsync(HttpContext ctx) => PublicSite.Index(ctx);
    public override Task NotFoundAsync(HttpContext ctx) => PublicSite.NotFound(ctx);
    public override Task FallbackAsync(HttpContext ctx) => PublicSite.NotFound(ctx);
}

/// <summary>Reverse proxy to one private loopback web application.</summary>
public sealed class UpstreamSite : PublicContent
{
    private readonly HttpClient _http;

    public UpstreamSite(string baseUri)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri(baseUri), Timeout = Timeout.InfiniteTimeSpan };
    }

    public override async Task HomeAsync(HttpContext ctx) => await Forward(ctx);

    /// <summary>The application owns its own 404s: forward the original path.</summary>
    public override Task NotFoundAsync(HttpContext ctx) => Forward(ctx);

    public override Task FallbackAsync(HttpContext ctx) => Forward(ctx);

    private static readonly string[] HopByHop =
    [
        "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "Proxy-Authenticate",
        "Proxy-Authorization", "Te", "Trailer", "Trailers"
    ];

    private async Task Forward(HttpContext ctx)
    {
        var upstream = new HttpRequestMessage(new HttpMethod(ctx.Request.Method),
            ctx.Request.Path + ctx.Request.QueryString);
        // The site application receives the original Host; it owns cookies and
        // framework headers. Capacity accounting travels as a single address.
        upstream.Headers.Host = ctx.Request.Host.Value;
        if (ctx.Items["ClientIp"] is IPAddress ip)
            upstream.Headers.Add("X-Forwarded-For", ip.ToString());
        foreach (var h in ctx.Request.Headers)
        {
            if (HopByHop.Contains(h.Key, StringComparer.OrdinalIgnoreCase) ||
                h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                h.Key.Equals("X-Forwarded-For", StringComparison.OrdinalIgnoreCase))
                continue;
            try { upstream.Content?.Headers.Add(h.Key, h.Value.ToArray()); }
            catch
            {
                try { upstream.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray()); }
                catch { /* drop unparseable */ }
            }
        }
        if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            upstream.Content = new StreamContent(ctx.Request.Body);
            if (ctx.Request.ContentType is { Length: > 0 } ct)
                upstream.Content.Headers.TryAddWithoutValidation("Content-Type", ct);
        }
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
        }
        catch (HttpRequestException)
        {
            ctx.Response.StatusCode = 502;
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        ctx.Response.StatusCode = (int)response.StatusCode;
        foreach (var h in response.Headers)
        {
            if (HopByHop.Contains(h.Key, StringComparer.OrdinalIgnoreCase))
                continue;
            ctx.Response.Headers[h.Key] = h.Value.ToArray();
        }
        foreach (var h in response.Content.Headers)
        {
            if (HopByHop.Contains(h.Key, StringComparer.OrdinalIgnoreCase))
                continue;
            ctx.Response.Headers[h.Key] = h.Value.ToArray();
        }
        await response.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }
}

/// <summary>
/// Static site served from memory. Read once at start-up: restart the relay
/// after changing files. Exact links (/about.html) plus optional legacy
/// extensionless aliases; GET/HEAD with standard conditional and single-range
/// handling.
/// </summary>
public sealed class StaticSite : PublicContent
{
    private sealed record Entry(byte[] Bytes, string ContentType, string ETag, DateTimeOffset LastModified);

    private readonly Dictionary<string, Entry> _files = new(StringComparer.Ordinal);
    private readonly bool _legacyAliases;
    private readonly ILogger _log;
    private Entry? _home;
    private Entry? _notFoundPage;

    public StaticSite(string dir, bool legacyAliases, ILogger log)
    {
        _legacyAliases = legacyAliases;
        _log = log;
        var root = Path.GetFullPath(dir);
        if (!Directory.Exists(root))
            throw new InvalidOperationException($"public_dir {root} does not exist");
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            var url = "/" + rel;
            var bytes = File.ReadAllBytes(file);
            var etag = $"\"{XxHash3.HashToUInt64(bytes):x16}-{bytes.Length:x}\"";
            var entry = new Entry(bytes, ContentTypeFor(rel), etag, File.GetLastWriteTimeUtc(file));
            _files[url] = entry;
            if (rel == "index.html")
                _home = entry;
            if (rel == "404.html")
                _notFoundPage = entry;
        }
        if (_home == null)
            throw new InvalidOperationException($"public_dir {root} has no index.html");
        _log.LogInformation("event=public_site_loaded files={Count}", _files.Count);
    }

    private static string ContentTypeFor(string rel)
    {
        var ext = Path.GetExtension(rel).ToLowerInvariant();
        return ext switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".json" => "application/json",
            ".txt" => "text/plain; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".ico" => "image/x-icon",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".xml" => "application/xml",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream"
        };
    }

    private static void AddValidators(HttpContext ctx, Entry e)
    {
        ctx.Response.Headers.ETag = e.ETag;
        ctx.Response.Headers.LastModified = e.LastModified.ToString("R");
    }

    private async Task ServeEntry(HttpContext ctx, Entry e)
    {
        // Conditional request handling: If-None-Match wins over If-Modified-Since.
        var inm = ctx.Request.Headers.IfNoneMatch.ToString();
        var notModified =
            (inm.Length > 0 && inm.Split(',').Any(t => t.Trim() is "*" or "W/*") ) ||
            (inm.Length > 0 && inm.Split(',').Any(t =>
            {
                var tag = t.Trim();
                if (tag.StartsWith("W/")) tag = tag[2..];
                return tag == e.ETag;
            })) ||
            (inm.Length == 0 &&
             DateTime.TryParse(ctx.Request.Headers.IfModifiedSince, out var ims) &&
             e.LastModified <= ims);
        if (notModified && ctx.Request.Method is "GET" or "HEAD")
        {
            ctx.Response.StatusCode = 304;
            AddValidators(ctx, e);
            return;
        }

        if (HttpMethods.IsHead(ctx.Request.Method))
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = e.ContentType;
            ctx.Response.ContentLength = e.Bytes.Length;
            AddValidators(ctx, e);
            return;
        }
        if (!HttpMethods.IsGet(ctx.Request.Method))
        {
            ctx.Response.StatusCode = 405;
            ctx.Response.Headers.Allow = "GET, HEAD";
            return;
        }

        // Single byte range: "bytes=A-B", "bytes=A-", "bytes=-N".
        var range = ctx.Request.Headers.Range.ToString();
        if (range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var spec = range["bytes=".Length..].Trim();
            var dash = spec.IndexOf('-');
            long? start = null, end = null;
            if (dash > 0 && long.TryParse(spec[..dash], out var s))
                start = s;
            if (dash >= 0 && dash + 1 < spec.Length && long.TryParse(spec[(dash + 1)..], out var en))
                end = en;
            long from, to;
            if (start == null && end != null)
            {
                // suffix range: last N bytes
                var n = Math.Min(end.Value, e.Bytes.Length);
                from = e.Bytes.Length - n;
                to = e.Bytes.Length - 1;
            }
            else if (start != null)
            {
                from = start.Value;
                to = end != null ? Math.Min(end.Value, e.Bytes.Length - 1) : e.Bytes.Length - 1;
            }
            else
            {
                ctx.Response.StatusCode = 416;
                ctx.Response.Headers.ContentRange = $"bytes */{e.Bytes.Length}";
                return;
            }
            if (from < 0 || from > to || from >= e.Bytes.Length)
            {
                ctx.Response.StatusCode = 416;
                ctx.Response.Headers.ContentRange = $"bytes */{e.Bytes.Length}";
                return;
            }
            ctx.Response.StatusCode = 206;
            ctx.Response.ContentType = e.ContentType;
            ctx.Response.Headers.AcceptRanges = "bytes";
            ctx.Response.Headers.ContentRange = $"bytes {from}-{to}/{e.Bytes.Length}";
            AddValidators(ctx, e);
            ctx.Response.ContentLength = to - from + 1;
            await ctx.Response.Body.WriteAsync(e.Bytes.AsMemory((int)from, (int)(to - from + 1)), ctx.RequestAborted);
            return;
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = e.ContentType;
        ctx.Response.Headers.AcceptRanges = "bytes";
        AddValidators(ctx, e);
        ctx.Response.ContentLength = e.Bytes.Length;
        await ctx.Response.Body.WriteAsync(e.Bytes, ctx.RequestAborted);
    }

    public override async Task HomeAsync(HttpContext ctx) => await ServeEntry(ctx, _home!);

    public override async Task NotFoundAsync(HttpContext ctx)
    {
        if (_notFoundPage != null)
        {
            // The custom page answers with a real 404 status.
            ctx.Response.StatusCode = 404;
            ctx.Response.ContentType = _notFoundPage.ContentType;
            ctx.Response.ContentLength = _notFoundPage.Bytes.Length;
            await ctx.Response.Body.WriteAsync(_notFoundPage.Bytes, ctx.RequestAborted);
            return;
        }
        await PublicSite.NotFound(ctx);
    }

    public override async Task FallbackAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        // Path traversal cannot reach the dictionary lookup, but reject the
        // shapes explicitly so nothing depends on the filesystem below.
        if (path.Contains("..") || path.Contains('\0'))
        {
            await NotFoundAsync(ctx);
            return;
        }
        if (_files.TryGetValue(path, out var e) ||
            _legacyAliases && path != "/" && _files.TryGetValue(EnsureHtml(path), out e))
        {
            await ServeEntry(ctx, e);
            return;
        }
        await NotFoundAsync(ctx);
    }

    private static string EnsureHtml(string path) => path.EndsWith('/') ? path + "index.html" : path + ".html";
}
