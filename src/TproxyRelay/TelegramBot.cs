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

    /// <summary>retry_after from the last failed call (Bot API 429), seconds.</summary>
    internal int LastRetryAfter;

    internal async Task<JsonElement?> Api(string method, object? payload = null, CancellationToken ct = default)
    {
        try
        {
            using var content = payload == null
                ? null
                : JsonContent.Create(payload);
            var resp = await _http.PostAsync(
                $"https://api.telegram.org/bot{_cfg.Token}/{method}", content, ct);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.GetProperty("ok").GetBoolean())
            {
                LastRetryAfter = ParseRetryAfter(doc.RootElement);
                _log.LogWarning("event=tg_api_failed method={Method} desc={Desc} retry_after={Retry}",
                    method,
                    doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() : "?",
                    LastRetryAfter);
                return null;
            }
            // Clone: the document is disposed, the element must outlive it.
            return doc.RootElement.GetProperty("result").Clone();
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

    internal static int ParseRetryAfter(JsonElement root) =>
        root.TryGetProperty("parameters", out var p) &&
        p.TryGetProperty("retry_after", out var ra) &&
        ra.ValueKind == JsonValueKind.Number
            ? ra.GetInt32()
            : 0;

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
                // REL-003: a single malformed update (or a failing send) must
                // never take the whole command channel down.
                try
                {
                    if (!u.TryGetProperty("message", out var msg))
                        continue;
                    if (!msg.TryGetProperty("chat", out var chatEl) ||
                        !chatEl.TryGetProperty("id", out var chatIdEl))
                        continue;
                    var chat = chatIdEl.GetInt64();
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
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    _log.LogError("event=tg_update_failed err={Error}", e.Message);
                }
            }
        }
        _log.LogInformation("event=tg_bot_stopped");
    }

    internal async Task Send(long chat, string text, CancellationToken ct = default)
    {
        // Telegram hard-caps messages at 4096 characters.
        for (var off = 0; off < text.Length; off += 4000)
        {
            // REL-008: on 429 honor the server-provided retry_after (once)
            // instead of dropping the chunk on the floor.
            for (var attempt = 0; ; attempt++)
            {
                LastRetryAfter = 0;
                if (await Api("sendMessage",
                        new { chat_id = chat, text = text[off..Math.Min(text.Length, off + 4000)] }, ct) != null)
                    break;
                if (attempt >= 1 || LastRetryAfter <= 0)
                    break;
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(LastRetryAfter, 30)), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
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
        var s = Counters.Snapshot();
        var sessions = _hub.SessionsSnapshot();
        var sb = new StringBuilder();
        sb.AppendLine("Состояние релея:");
        sb.AppendLine($"Сессий: {s.SessionsActive} | Стримов: {s.StreamsActive}");
        sb.AppendLine($"Трафик всего: ↑{Fmt(s.UpBytes)} ↓{Fmt(s.DownBytes)}");
        sb.AppendLine($"Лимитов задето: {s.LimitHits} | Активных ключей: {_registry.All.Count}");
        foreach (var sess in sessions.Take(10))
            sb.AppendLine($"  • {sess.KeyId}: стримов {sess.Streams}, активность {sess.LastActivity:HH:mm:ss}");
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
        // The revoke/pause/resume ritual is owned by KeyManager (ARCH-004).
        return action switch
        {
            "revoke" => KeyManager.Revoke(_opt, _store, _registry, _hub, key.Id) is var closedR && closedR >= 0
                ? $"Ключ «{name}» отозван, сессий закрыто: {closedR}."
                : $"Ключ «{name}» не найден.",
            "pause" => KeyManager.Pause(_opt, _store, _registry, _hub, key.Id) is var closedP && closedP >= 0
                ? $"Ключ «{name}» на паузе, сессий закрыто: {closedP}."
                : $"Ключ «{name}» не найден.",
            _ => KeyManager.Resume(_opt, _store, _registry, key.Id)
                ? $"Ключ «{name}» снова активен."
                : $"Ключ «{name}» не найден.",
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
