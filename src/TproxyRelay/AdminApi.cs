using System.Security.Cryptography;
using System.Text;

namespace TproxyRelay;

/// <summary>
/// Operations exposed over the loopback admin listener under /admin/*.
/// Enabled only when management.api.enabled is set together with an admin
/// token (fail-closed): a missing token refuses startup rather than silently
/// leaving the API off.
/// </summary>
public static class AdminApi
{
    public static void Map(WebApplication app, RelayOptions opt, RelayHub hub,
        KeyStore store, ProfileRegistry registry)
    {
        var cfg = opt.ManagementApi;
        if (!cfg.Enabled)
            return;
        if (string.IsNullOrWhiteSpace(cfg.AdminToken))
            throw new InvalidOperationException(
                "management.api.enabled requires management.api.admin_token (refusing to start)");

        const string prefix = "/admin";
        var prefixBase = opt.BasePath.Length == 0 ? prefix : "/" + opt.BasePath + prefix;

        bool Auth(HttpContext ctx)
        {
            // The admin surface answers the loopback listener only.
            if (ctx.Connection.LocalPort != opt.AdminPort)
                return false;
            var auth = ctx.Request.Headers.Authorization.ToString();
            if (auth.Length != 7 + cfg.AdminToken.Length)
                return false;
            if (!auth.StartsWith("Bearer ", StringComparison.Ordinal))
                return false;
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(auth[7..]), Encoding.UTF8.GetBytes(cfg.AdminToken));
        }

        app.Map(prefixBase + "/stats", async (HttpContext ctx) =>
        {
            if (!Auth(ctx)) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
            {
                sessions_active = CountersRender("tproxy_sessions_active"),
                streams_active = CountersRender("tproxy_streams_active"),
                bootstraps_outstanding = hub.BootstrapCount,
                up_bytes_total = CountersRender("tproxy_up_bytes_total"),
                down_bytes_total = CountersRender("tproxy_down_bytes_total"),
                limit_hits_total = CountersRender("tproxy_limit_hits_total"),
                pending_bytes = CountersRender("tproxy_pending_bytes"),
                keys = registry.All.Select(p => new { p.KeyId, p.Name, p.Backend }),
            }, JsonOpts));
        });

        app.Map(prefixBase + "/keys", async (HttpContext ctx) =>
        {
            if (!Auth(ctx)) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
            {
                keys = store.ListKeys().Select(k => new
                {
                    k.Id, k.Name, k.BackendPort, k.CreatedUtc, k.RevokedUtc, k.Paused, k.Active,
                    // The secret is the operator's own credential: only over the
                    // loopback admin listener, and only with ?reveal=1 (API-005:
                    // any other value must keep it hidden).
                    secret_hex = ctx.Request.Query["reveal"] == "1" ? k.SecretHex : null,
                }),
            }, JsonOpts));
        });

        app.MapPost(prefixBase + "/keys", async (HttpContext ctx) =>
        {
            if (!Auth(ctx)) { ctx.Response.StatusCode = 404; return; }
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
            var secret = doc.RootElement.TryGetProperty("secret_hex", out var s) ? s.GetString() : null;
            try
            {
                var key = store.Create(name ?? throw new ArgumentException("name is required"),
                    string.IsNullOrWhiteSpace(secret) ? null : secret);
                RefreshAndExport(opt, store, registry);
                ctx.Response.StatusCode = 201;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(KeyView(key, opt, reveal: true), JsonOpts));
            }
            catch (ArgumentException e)
            {
                await Error(ctx, 400, e.Message);
            }
            catch (InvalidOperationException e)
            {
                await Error(ctx, 409, e.Message);
            }
        });

        app.MapDelete(prefixBase + "/keys/{id}", async (HttpContext ctx, string id) =>
        {
            if (!Auth(ctx)) { ctx.Response.StatusCode = 404; return; }
            var key = store.Get(id);
            if (key == null || !store.Revoke(id))
            {
                await Error(ctx, 404, "no such active key");
                return;
            }
            RefreshAndExport(opt, store, registry);
            var closed = hub.CloseAllSessionsForKey(id, "key revoked");
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { revoked = key.Id, sessions_closed = closed }, JsonOpts));
        });

        app.MapPost(prefixBase + "/keys/{id}/pause", async (HttpContext ctx, string id) =>
        {
            if (!Auth(ctx)) { ctx.Response.StatusCode = 404; return; }
            if (!store.SetPaused(id, true)) { await Error(ctx, 404, "no such active key"); return; }
            RefreshAndExport(opt, store, registry);
            var closed = hub.CloseAllSessionsForKey(id, "key paused");
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { paused = id, sessions_closed = closed }, JsonOpts));
        });

        app.MapPost(prefixBase + "/keys/{id}/resume", async (HttpContext ctx, string id) =>
        {
            if (!Auth(ctx)) { ctx.Response.StatusCode = 404; return; }
            if (!store.SetPaused(id, false)) { await Error(ctx, 404, "no such active key"); return; }
            RefreshAndExport(opt, store, registry);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"resumed\":true}");
        });

        app.Map(prefixBase + "/sessions", async (HttpContext ctx) =>
        {
            if (!Auth(ctx)) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
            {
                sessions = hub.SessionsSnapshot().Select(s => new
                {
                    s.KeyId,
                    key_name = registry.All.FirstOrDefault(p => p.KeyId == s.KeyId)?.Name,
                    created = s.CreatedUtc,
                    last_activity = s.LastActivity,
                    client_ip = s.ClientIp?.ToString(),
                    streams = s.Streams,
                }),
            }, JsonOpts));
        });

        app.Map(prefixBase + "/traffic", async (HttpContext ctx) =>
        {
            if (!Auth(ctx)) { ctx.Response.StatusCode = 404; return; }
            var days = int.TryParse(ctx.Request.Query["days"], out var d) && d is >= 1 and <= 90 ? d : 7;
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
            {
                rows = store.Traffic(days),
            }, JsonOpts));
        });
    }

    /// <summary>Reloads managed profiles and re-exports the supervisor registry.</summary>
    public static void RefreshAndExport(RelayOptions opt, KeyStore store, ProfileRegistry registry)
    {
        registry.ReplaceManaged(store.ListKeys(includeRevoked: false).Where(k => k.Active).Select(k =>
            new RelayProfile(k.Id, k.Name, Convert.FromHexString(k.SecretHex),
                opt.BackendHostName, k.BackendPort, opt.CarrierMode)));
        store.ExportRegistry(Convert.ToHexString(opt.Secret).ToLowerInvariant());
    }

    private static object KeyView(KeyRecord k, RelayOptions opt, bool reveal) => new
    {
        k.Id,
        k.Name,
        k.BackendPort,
        k.CreatedUtc,
        k.RevokedUtc,
        k.Paused,
        k.Active,
        secret_hex = reveal ? k.SecretHex : null,
        // Client-facing credits: the capability depends on hostname+base path.
        link = ProxyLinks.Tme(opt.PublicHostname, opt.BasePath, Convert.FromHexString(k.SecretHex)),
    };

    private static long CountersRender(string name)
    {
        var text = Counters.Render();
        foreach (var line in text.Split('\n'))
            if (line.StartsWith(name + ' '))
                return long.Parse(line[(name.Length + 1)..].Trim());
        return 0;
    }

    private static async Task Error(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { error = message }, JsonOpts));
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
    };
}
