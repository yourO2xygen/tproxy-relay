namespace TproxyRelay;

/// <summary>
/// The ordinary public website. Every request without an authentic relay
/// credential lands here, so no separately probeable transport surface exists.
/// </summary>
public static class PublicSite
{
    private const string IndexHtml = """
<!doctype html>
<html>
<head><meta charset="utf-8"><title>Example project</title></head>
<body>
<!-- PUBLIC_SITE_INDEX -->
<h1>Example project</h1>
<p>This is the ordinary public website served for unauthenticated visitors.</p>
</body>
</html>
""";

    private const string NotFoundHtml = """
<!doctype html>
<html>
<head><meta charset="utf-8"><title>Not found</title></head>
<body>
<!-- PUBLIC_SITE_404 -->
<h1>Page not found</h1>
<p>The page you requested does not exist.</p>
</body>
</html>
""";

    public static async Task Index(HttpContext ctx)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache";
        await ctx.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(IndexHtml));
    }

    public static async Task NotFound(HttpContext ctx)
    {
        ctx.Response.StatusCode = 404;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache";
        await ctx.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(NotFoundHtml));
    }
}
