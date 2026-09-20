using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TproxyRelay;

/// <summary>
/// A thin Telegram client over the management Admin API primitives. Opt-in and
/// fail-closed: enabled without a bot token or an admin chat list refuses
/// startup. Commands from any other chat are ignored entirely.
/// </summary>
public sealed class TelegramBot : BackgroundService
{
    private readonly ManagementBotConfig _cfg;
    private readonly RelayOptions _opt;
    private readonly RelayHub _hub;
    private readonly KeyStore _store;
    private readonly ProfileRegistry _registry;
    private readonly ILogger _log;
    private readonly HttpClient _http;
    private long _offset;

    public TelegramBot(ManagementBotConfig cfg, RelayOptions opt, RelayHub hub,
        KeyStore store, ProfileRegistry registry, ILogger log)
    {
        _cfg = cfg;
        _opt = opt;
        _hub = hub;
        _store = store;
        _registry = registry;
        _log = log;
        if (cfg.Enabled && (string.IsNullOrWhiteSpace(cfg.Token) || cfg.AdminChats.Length == 0))
            throw new InvalidOperationException(
                "management.bot.enabled requires management.bot.token and management.bot.admin_chat_ids (refusing to start)");
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(65) };
    }

    public static void ValidateAndRegister(WebApplication app, RelayOptions opt, RelayHub hub,
        KeyStore store, ProfileRegistry registry)
    {
        var cfg = opt.ManagementBot;
        if (!cfg.Enabled)
            return;
        var bot = new TelegramBot(cfg, opt, hub, store, registry, app.Logger);
        app.Services.GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStopping.Register(() =>
            {
                bot.Dispose();
                try { bot.PollTask?.Wait(TimeSpan.FromSeconds(5)); } catch { /* ignore */ }
            });
        // Observed task: an unhandled poll failure surfaces in the host log
        // instead of silently disappearing.
        bot.PollTask = Task.Run(async () =>
        {
            try { await bot.ExecuteAsync(CancellationToken.None); }
            catch (Exception e)
            {
                app.Logger.LogError("event=tg_bot_crashed err={Error}", e.Message);
            }
        });
    }

    internal Task? PollTask;

    protected override async Task ExecuteAsync(CancellationToken ct) => await Poll(ct);

    // ---- transport ------------------------------------------------------------

    internal async Task<JsonElement?> Api(string method, object? payload = null, CancellationToken ct = default)
    {
        try
        {
            using var content = payload == null
                ? null
                : JsonContent.Create(payload);
            var resp = await _http.PostAsync(
                $"https://api.telegram.org/bot{_cfg.Token}/{method}", content, ct);
            var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!doc.RootElement.GetProperty("ok").GetBoolean())
            {
                _log.LogWarning("event=tg_api_failed method={Method} desc={Desc}",
                    method, doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() : "?");
                return null;
            }
            return doc.RootElement.GetProperty("result");
        }
        catch (OperationCanceledException)
        {
            // Timeouts here are routine (long poll): never let them kill the loop.
            _log.LogWarning("event=tg_api_timeout method={Method}", method);
            return null;
        }
        catch (Exception e)
        {
            _log.LogWarning("event=tg_api_error method={Method} err={Error}", method, e.Message);
            return null;
        }
    }

    internal async Task Poll(CancellationToken ct)
    {
        _log.LogInformation("event=tg_bot_started admins={Count}", _cfg.AdminChats.Length);
        while (!ct.IsCancellationRequested)
        {
            JsonElement? updates;
            try
            {
                updates = await Api("getUpdates",
                    new { offset = _offset + 1, timeout = 25, allowed_updates = new[] { "message" } }, ct);
            }
            catch (Exception e)
            {
                // The poll loop must survive anything, including timeouts.
                _log.LogWarning("event=tg_poll_error err={Error}", e.Message);
                updates = null;
            }
            if (updates == null)
            {
                try { await Task.Delay(5000, ct); }
                catch (OperationCanceledException) { break; }
                continue;
            }
            foreach (var u in updates.Value.EnumerateArray())
            {
                _offset = u.GetProperty("update_id").GetInt64();
                if (!u.TryGetProperty("message", out var msg))
                    continue;
                var chat = msg.GetProperty("chat").GetProperty("id").GetInt64();
                if (!_cfg.AdminChats.Contains(chat.ToString()))
                {
                    // Visible in the logs so an operator can bootstrap their own
                    // chat id into management.bot.admin_chat_ids.
                    _log.LogInformation("event=tg_stranger chat={ChatId}", chat);
                    continue;
                }
                if (!msg.TryGetProperty("text", out var textEl))
                    continue;
                var text = textEl.GetString() ?? "";
                _log.LogInformation("event=tg_command chat={ChatId} text={Text}", chat, text);
                string reply;
                try
                {
                    reply = await HandleCommandAsync(text);
                }
                catch (Exception e)
                {
                    _log.LogError("event=tg_command_failed text={Text} err={Error}", text, e.Message);
                    reply = $"ошибка: {e.Message}";
                }
                if (reply.Length > 0)
                    await Send(chat, reply, ct);
            }
        }
        _log.LogInformation("event=tg_bot_stopped");
    }

    internal async Task Send(long chat, string text, CancellationToken ct = default)
    {
        // Telegram hard-caps messages at 4096 characters.
        for (var off = 0; off < text.Length; off += 4000)
            await Api("sendMessage",
                new { chat_id = chat, text = text[off..Math.Min(text.Length, off + 4000)] }, ct);
    }

    // ---- commands ---------------------------------------------------------------

    internal async Task<string> HandleCommandAsync(string line)
    {
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return "";
        var cmd = parts[0].Split('@')[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1] : "";
        try
        {
            return cmd switch
            {
                "/help" or "/start" => Help,
                "/stats" => Stats(),
                "/keys" => Keys(),
                "/key" => await KeyCreateAsync(arg),
                "/revoke" => KeyChange(arg, "revoke"),
                "/pause" => KeyChange(arg, "pause"),
                "/resume" => KeyChange(arg, "resume"),
                "/traffic" => Traffic(arg),
                _ => "",
            };
        }
        catch (Exception e)
        {
            return $"ошибка: {e.Message}";
        }
    }

    private const string Help = """
        Команды управления tproxy-relay:
        /stats — сводка по трафику и сессиям
        /keys — список ключей (без секретов)
        /key <имя> — выдать ключ (вернёт кредиты и ссылку)
        /revoke <имя> — отозвать ключ
        /pause <имя> | /resume <имя> — временно выключить/включить
        /traffic [дней] — трафик по ключам за N дней
        """;

    private string Stats()
    {
        var gauge = (string name) =>
        {
            foreach (var l in Counters.Render().Split('\n'))
                if (l.StartsWith(name + ' '))
                    return l[(name.Length + 1)..].Trim();
            return "0";
        };
        var sessions = _hub.SessionsSnapshot();
        var sb = new StringBuilder();
        sb.AppendLine("Состояние релея:");
        sb.AppendLine($"Сессий: {gauge("tproxy_sessions_active")} | Стримов: {gauge("tproxy_streams_active")}");
        sb.AppendLine($"Трафик всего: ↑{Fmt(long.Parse(gauge("tproxy_up_bytes_total")))} ↓{Fmt(long.Parse(gauge("tproxy_down_bytes_total")))}");
        sb.AppendLine($"Лимитов задето: {gauge("tproxy_limit_hits_total")} | Активных ключей: {_registry.All.Count}");
        foreach (var s in sessions.Take(10))
            sb.AppendLine($"  • {s.KeyId}: стримов {s.Streams}, активность {s.LastActivity:HH:mm:ss}");
        return sb.ToString();
    }

    private string Keys()
    {
        var keys = _store.ListKeys();
        if (keys.Count == 0)
            return "Ключей нет. /key <имя> — выдать.";
        var sb = new StringBuilder("Ключи:\n");
        foreach (var k in keys)
        {
            var state = k.RevokedUtc != null ? "ОТОЗВАН" : k.Paused ? "пауза" : "активен";
            sb.AppendLine($"  • {k.Name} (порт {k.BackendPort}, {state}, id {k.Id[..8]})");
        }
        return sb.ToString();
    }

    private async Task<string> KeyCreateAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Использование: /key <имя>";
        var key = _store.Create(name);
        AdminApi.RefreshAndExport(_opt, _store, _registry);
        var secret = Convert.FromHexString(key.SecretHex);
        return $"""
            Ключ «{name}» выдан.
            Сервер:  {_opt.PublicHostname}{(optBase() is { Length: > 0 } b ? "/" + b : "")}
            Секрет:  {ProxyLinks.SecretForLink(secret, optBase())}
            Ссылка:  {ProxyLinks.Tme(_opt.PublicHostname, optBase(), secret)}
            """;
    }

    private string optBase() => _opt.BasePath;

    private string KeyChange(string name, string action)
    {
        if (string.IsNullOrWhiteSpace(name))
            return $"Использование: /{action} <имя>";
        var key = _store.GetByName(name);
        if (key == null || key.RevokedUtc != null)
            return $"Ключ «{name}» не найден.";
        var closed = 0;
        switch (action)
        {
            case "revoke":
                _store.Revoke(key.Id);
                closed = _hub.CloseAllSessionsForKey(key.Id, "key revoked");
                break;
            case "pause":
                _store.SetPaused(key.Id, true);
                closed = _hub.CloseAllSessionsForKey(key.Id, "key paused");
                break;
            case "resume":
                _store.SetPaused(key.Id, false);
                break;
        }
        AdminApi.RefreshAndExport(_opt, _store, _registry);
        return action switch
        {
            "revoke" => $"Ключ «{name}» отозван, сессий закрыто: {closed}.",
            "pause" => $"Ключ «{name}» на паузе, сессий закрыто: {closed}.",
            _ => $"Ключ «{name}» снова активен.",
        };
    }

    private string Traffic(string daysArg)
    {
        var days = int.TryParse(daysArg, out var d) && d is >= 1 and <= 90 ? d : 7;
        var rows = _store.Traffic(days);
        if (rows.Count == 0)
            return $"Трафика за {days} дн. нет.";
        var names = _store.ListKeys().ToDictionary(k => k.Id, k => k.Name);
        var sb = new StringBuilder($"Трафик за {days} дн.:\n");
        foreach (var g in rows.GroupBy(r => r.KeyId))
        {
            var up = g.Sum(r => r.UpBytes);
            var down = g.Sum(r => r.DownBytes);
            var label = names.GetValueOrDefault(g.Key, g.Key);
            sb.AppendLine($"  • {label}: ↑{Fmt(up)} ↓{Fmt(down)}");
        }
        return sb.ToString();
    }

    internal static string Fmt(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):F1} GiB"),
        >= 1L << 20 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):F1} MiB"),
        >= 1L << 10 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 10):F1} KiB"),
        _ => $"{bytes} B"
    };
}
