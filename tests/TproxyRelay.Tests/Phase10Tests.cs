using System.Text.Json;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using TproxyRelay;

namespace TproxyRelay.Tests;

public sealed class TelegramBotTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "tproxy-bot-" + Guid.NewGuid().ToString("N")[..8] + ".db");
    private readonly RelayOptions _opt = new("127.0.0.1:2398")
    {
        Secret = Convert.FromHexString("000102030405060708090a0b0c0d0e0f"),
        PublicHostname = "proxy.example.com",
    };
    private RelayHub Hub() => new(_opt, new TokenMinter(new byte[32]), NullLogger.Instance);
    private KeyStore Store() => new(_db);

    [Fact]
    public void Enabled_Without_Token_Refuses_Construction()
    {
        Assert.Throws<InvalidOperationException>(() => new TelegramBot(
            new ManagementBotConfig(true, "", "123"), _opt, Hub(), Store(), null!, NullLogger.Instance));
        Assert.Throws<InvalidOperationException>(() => new TelegramBot(
            new ManagementBotConfig(true, "token", ""), _opt, Hub(), Store(), null!, NullLogger.Instance));
    }

    [Fact]
    public async Task Key_Create_Revoke_Through_Commands()
    {
        var store = Store();
        var registry = new ProfileRegistry(_opt, new RelayProfile(
            "builtin", "builtin", _opt.Secret, "127.0.0.1", 2398, "https"));
        var bot = new TelegramBot(
            new ManagementBotConfig(true, "t", "1"), _opt, Hub(), store, registry, NullLogger.Instance);

        var created = await bot.HandleCommandAsync("/key ivan");
        Assert.Contains("Ключ «ivan» выдан", created);
        Assert.Contains("proxy.example.com", created);
        Assert.Contains("https://t.me/webproxy?server=proxy.example.com", created);
        Assert.NotNull(store.GetByName("ivan"));

        // Registry picked the new key up.
        Assert.Contains(registry.All, p => p.Name == "ivan");

        var listed = await bot.HandleCommandAsync("/keys");
        Assert.Contains("ivan", listed);
        Assert.DoesNotContain(store.GetByName("ivan")!.SecretHex, listed); // no secrets in listings

        var revoked = await bot.HandleCommandAsync("/revoke ivan");
        Assert.Contains("отозван", revoked);
        Assert.False(store.GetByName("ivan")!.Active);
        Assert.DoesNotContain(registry.All, p => p.Name == "ivan");

        Assert.Contains("не найден", await bot.HandleCommandAsync("/revoke ghost"));
        Assert.Contains("Использование", await bot.HandleCommandAsync("/key"));
    }

    [Fact]
    public void Bytes_Are_Human_Readable()
    {
        Assert.Equal("999 B", TelegramBot.Fmt(999));
        Assert.Equal("1.5 KiB", TelegramBot.Fmt(1536));
        Assert.Equal("2.0 MiB", TelegramBot.Fmt((long)(2.0 * (1 << 20))));
    }

    [Fact]
    public void ParseRetryAfter_Reads_Flood_Control_Window()
    {
        using var doc = JsonDocument.Parse("""{"ok":false,"description":"Too Many Requests","parameters":{"retry_after":7}}""");
        Assert.Equal(7, TelegramBot.ParseRetryAfter(doc.RootElement));

        using var none = JsonDocument.Parse("""{"ok":false,"description":"Unauthorized"}""");
        Assert.Equal(0, TelegramBot.ParseRetryAfter(none.RootElement));
    }

    public void Dispose()
    {
        // TEST-010: clean every SQLite sidecar, not just the main db file.
        foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" })
            try { File.Delete(_db + suffix); } catch { /* best effort */ }
    }
}
