using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace TproxyRelay.Tests.Integration;

/// <summary>TEST-001: the Admin API surface — bearer auth, reveal semantics,
/// key CRUD, revocation side effects, traffic.</summary>
public sealed class AdminApiTests : IAsyncLifetime
{
    private RelayHost _host = null!;
    private HttpClient C => _host.Admin;
    private static readonly string Auth = "Bearer testtoken123";

    public async Task InitializeAsync() => _host = await RelayHost.StartAsync();
    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Auth_Is_Fail_Closed()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await C.GetAsync("/admin/stats")).StatusCode);
        using var wrong = new HttpRequestMessage(HttpMethod.Get, "/admin/stats");
        wrong.Headers.TryAddWithoutValidation("Authorization", "Bearer wrong-token-wrong-token-wrong");
        Assert.Equal(HttpStatusCode.NotFound, (await C.SendAsync(wrong)).StatusCode);
        using var ok = new HttpRequestMessage(HttpMethod.Get, "/admin/stats");
        ok.Headers.TryAddWithoutValidation("Authorization", Auth);
        Assert.Equal(HttpStatusCode.OK, (await C.SendAsync(ok)).StatusCode);
    }

    [Fact]
    public async Task Admin_Endpoints_Are_Not_Probed_On_The_Public_Port()
    {
        // No oracle for random internet scanners: the public listener must not
        // answer /admin at all.
        using var req = new HttpRequestMessage(HttpMethod.Get, "/admin/stats");
        req.Headers.TryAddWithoutValidation("Authorization", Auth);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.Public.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Create_List_Reveal_Lifecycle()
    {
        using var create = new HttpRequestMessage(HttpMethod.Post, "/admin/keys");
        create.Headers.TryAddWithoutValidation("Authorization", Auth);
        create.Content = JsonContent.Create(new { name = "alpha" });
        using var cr = await C.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, cr.StatusCode);
        var created = await cr.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("Id").GetString()!;
        Assert.Equal(2399, created.GetProperty("BackendPort").GetInt32());
        Assert.Matches("^[0-9a-f]{32}$", created.GetProperty("secret_hex").GetString()!);
        Assert.Contains("t.me", created.GetProperty("link").GetString());

        // Hidden by default, with ?reveal=0, revealed only with ?reveal=1.
        var hidden = await GetAsync("/admin/keys");
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("keys")[0].GetProperty("secret_hex").ValueKind);
        var zero = await GetAsync("/admin/keys?reveal=0");
        Assert.Equal(JsonValueKind.Null, zero.GetProperty("keys")[0].GetProperty("secret_hex").ValueKind);
        var shown = await GetAsync("/admin/keys?reveal=1");
        Assert.Matches("^[0-9a-f]{32}$", shown.GetProperty("keys")[0].GetProperty("secret_hex").GetString()!);

        // Pause closes nothing but freezes the key; resume flips it back.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync($"/admin/keys/{id}/pause")).StatusCode);
        Assert.False(_host.Store.Get(id)!.Active);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync($"/admin/keys/{id}/resume")).StatusCode);
        Assert.True(_host.Store.Get(id)!.Active);

        // Revoke: sessions_closed reported, second revoke is a 404.
        Assert.Equal(HttpStatusCode.OK, (await DeleteAsync($"/admin/keys/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DeleteAsync($"/admin/keys/{id}")).StatusCode);
        var after = await GetAsync("/admin/keys");
        Assert.Equal(JsonValueKind.String, after.GetProperty("keys")[0].GetProperty("RevokedUtc").ValueKind);
    }

    [Fact]
    public async Task Create_Validates_Input_With_400()
    {
        // REL-015: malformed JSON.
        using var bad = new HttpRequestMessage(HttpMethod.Post, "/admin/keys");
        bad.Headers.TryAddWithoutValidation("Authorization", Auth);
        bad.Content = new StringContent("{ not json", null, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await C.SendAsync(bad)).StatusCode);
        // API-007: wrong-typed field is a client error, not a 500.
        using var typed = new HttpRequestMessage(HttpMethod.Post, "/admin/keys");
        typed.Headers.TryAddWithoutValidation("Authorization", Auth);
        typed.Content = JsonContent.Create(new { name = 123 });
        Assert.Equal(HttpStatusCode.BadRequest, (await C.SendAsync(typed)).StatusCode);
        // Duplicate name stays a 409.
        using var dup1 = new HttpRequestMessage(HttpMethod.Post, "/admin/keys");
        dup1.Headers.TryAddWithoutValidation("Authorization", Auth);
        dup1.Content = JsonContent.Create(new { name = "dup" });
        await C.SendAsync(dup1);
        using var dup2 = new HttpRequestMessage(HttpMethod.Post, "/admin/keys");
        dup2.Headers.TryAddWithoutValidation("Authorization", Auth);
        dup2.Content = JsonContent.Create(new { name = "dup" });
        Assert.Equal(HttpStatusCode.Conflict, (await C.SendAsync(dup2)).StatusCode);
    }

    [Fact]
    public async Task Revocation_Rewrites_The_Supervisor_Registry()
    {
        // Create through the API: only the managed ritual re-exports registry.txt.
        using var create = new HttpRequestMessage(HttpMethod.Post, "/admin/keys");
        create.Headers.TryAddWithoutValidation("Authorization", Auth);
        create.Content = JsonContent.Create(new { name = "regtest" });
        using var cr = await C.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, cr.StatusCode);
        var id = (await cr.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("Id").GetString()!;

        var registryPath = Path.Combine(_host.DataDir, "registry.txt");
        Assert.Contains("2399:", await File.ReadAllTextAsync(registryPath));
        using var del = new HttpRequestMessage(HttpMethod.Delete, $"/admin/keys/{id}");
        del.Headers.TryAddWithoutValidation("Authorization", Auth);
        Assert.Equal(HttpStatusCode.OK, (await C.SendAsync(del)).StatusCode);
        Assert.DoesNotContain("2399:", await File.ReadAllTextAsync(registryPath));
    }

    [Fact]
    public async Task Sessions_And_Traffic_Reports()
    {
        _host.Store.AggregateTraffic("builtin", 100, 200);
        var traffic = await GetAsync("/admin/traffic?days=1");
        Assert.Equal(100, traffic.GetProperty("rows")[0].GetProperty("UpBytes").GetInt32());
        var sessions = await GetAsync("/admin/sessions");
        Assert.Equal(JsonValueKind.Array, sessions.GetProperty("sessions").ValueKind);
    }

    private async Task<JsonElement> GetAsync(string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.TryAddWithoutValidation("Authorization", Auth);
        var resp = await C.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<HttpResponseMessage> PostAsync(string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path);
        req.Headers.TryAddWithoutValidation("Authorization", Auth);
        return await C.SendAsync(req);
    }

    private async Task<HttpResponseMessage> DeleteAsync(string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, path);
        req.Headers.TryAddWithoutValidation("Authorization", Auth);
        return await C.SendAsync(req);
    }
}
