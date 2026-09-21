using System.Net;
using Xunit;

namespace TproxyRelay.Tests.Integration;

/// <summary>TEST-002: the HTTP boundary in front of the carrier — XFF
/// hygiene, cookie rejection, bearer shape, body caps, lane headers, host
/// checks and the admin health surface.</summary>
public sealed class HttpBoundaryTests : IAsyncLifetime
{
    private RelayHost _host = null!;

    public async Task InitializeAsync() => _host = await RelayHost.StartAsync();
    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Admin_Health_Surface()
    {
        var health = await _host.Admin.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("ok\n", await health.Content.ReadAsStringAsync());
        var metrics = await _host.Admin.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
        Assert.Contains("text/plain", metrics.Content.Headers.ContentType!.ToString());
        Assert.Contains("tproxy_pending_bytes", await metrics.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await _host.Admin.GetAsync("/nope")).StatusCode);
    }

    [Fact]
    public async Task Cookie_On_Carrier_Api_Is_Rejected()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/session");
        req.Headers.TryAddWithoutValidation("Cookie", "session=leak");
        var resp = await _host.Public.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("cookie_rejected", resp.Headers.GetValues("X-Error").Single());
    }

    [Fact]
    public async Task Xff_Must_Be_Exactly_One_Address()
    {
        using var list = new HttpRequestMessage(HttpMethod.Post, "/api/v1/session");
        list.Headers.TryAddWithoutValidation("X-Forwarded-For", "1.2.3.4, 5.6.7.8");
        var resp = await _host.Public.SendAsync(list);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("bad_xff", resp.Headers.GetValues("X-Error").Single());

        using var garbage = new HttpRequestMessage(HttpMethod.Post, "/api/v1/session");
        garbage.Headers.TryAddWithoutValidation("X-Forwarded-For", "not-an-ip");
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.Public.SendAsync(garbage)).StatusCode);

        // A single valid address passes the hygiene gate (anything after it is
        // the public fallback, not a 400).
        using var single = new HttpRequestMessage(HttpMethod.Post, "/api/v1/session");
        single.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.7");
        Assert.Equal(HttpStatusCode.NotFound, (await _host.Public.SendAsync(single)).StatusCode);
    }

    [Fact]
    public async Task Session_Create_Caps_The_Body_Chunked_Included()
    {
        var bootstrap = _host.MintBootstrap();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/session");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bootstrap);
        // StreamContent without a length => Transfer-Encoding: chunked.
        req.Content = new StreamContent(new MemoryStream(new byte[200]));
        req.Content.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");
        var resp = await _host.Public.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("body_too_large", resp.Headers.GetValues("X-Error").Single());
    }

    [Fact]
    public async Task Uplink_413_On_Chunked_Oversize_Without_Full_Buffer()
    {
        var (session, _) = await CreateSessionAsync();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/up");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + session);
        req.Headers.TryAddWithoutValidation("X-Up-Seq", "1");
        req.Content = new StreamContent(new MemoryStream(new byte[2 * 1024 * 1024 + 4096]));
        req.Content.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");
        var resp = await _host.Public.SendAsync(req);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
        Assert.Equal("body_too_large", resp.Headers.GetValues("X-Error").Single());
    }

    [Fact]
    public async Task Uplink_Small_Body_Is_Acked()
    {
        // The happy path through the pooled body reader (regression: the
        // rented buffer's Length once started at capacity, not 0).
        var (session, _) = await CreateSessionAsync();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/up");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + session);
        req.Headers.TryAddWithoutValidation("X-Up-Seq", "1");
        req.Content = new ByteArrayContent(FrameCodec.Encode(FrameType.Open, 1));
        var resp = await _host.Public.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal("1", resp.Headers.GetValues("X-Up-Ack").Single());
    }

    [Fact]
    public async Task Uplink_Requires_A_Valid_Seq_Header()
    {
        var (session, _) = await CreateSessionAsync();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/up");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + session);
        req.Headers.TryAddWithoutValidation("X-Up-Seq", "zero");
        req.Content = new ByteArrayContent([]);
        var resp = await _host.Public.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("bad_seq", resp.Headers.GetValues("X-Error").Single());
    }

    [Fact]
    public async Task Foreign_Host_Follows_The_Public_Fallback()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/session");
        req.Headers.Host = "wrong.example.com";
        req.Content = new ByteArrayContent([]);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.Public.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Bearer_With_Invalid_Alphabet_Is_Not_An_Error()
    {
        // A token outside the base64url alphabet is indistinguishable from
        // random credentials: the public fallback answers, never a 4xx oracle.
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/session");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + new string('!', 43));
        req.Content = new ByteArrayContent([]);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.Public.SendAsync(req)).StatusCode);
    }

    private async Task<(string Token, HttpResponseMessage Resp)> CreateSessionAsync()
    {
        var bootstrap = _host.MintBootstrap();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/session");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bootstrap);
        req.Content = new ByteArrayContent(FrameCodec.Encode(FrameType.Hello, 0, [1]));
        var resp = await _host.Public.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (resp.Headers.GetValues("X-Session-Token").Single(), resp);
    }
}

/// <summary>Lanes-mode boundary specifics: X-Lane-ID discipline.</summary>
public sealed class LanesBoundaryTests : IAsyncLifetime
{
    private RelayHost _host = null!;

    public async Task InitializeAsync() =>
        _host = await RelayHost.StartAsync(carrierMode: "https-lanes");
    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Uplink_In_Lanes_Mode_Requires_X_Lane_ID()
    {
        var bootstrap = _host.MintBootstrap();
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/session");
        create.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bootstrap);
        create.Content = new ByteArrayContent(FrameCodec.Encode(FrameType.Hello, 0, [1]));
        using var cr = await _host.Public.SendAsync(create);
        Assert.Equal(HttpStatusCode.OK, cr.StatusCode);
        var token = cr.Headers.GetValues("X-Session-Token").Single();
        Assert.Equal("https-lanes", cr.Headers.GetValues("X-Carrier-Mode").Single());

        using var up = new HttpRequestMessage(HttpMethod.Post, "/api/v1/up");
        up.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        up.Headers.TryAddWithoutValidation("X-Up-Seq", "1");
        up.Content = new ByteArrayContent(FrameCodec.Encode(FrameType.Open, 5));
        var resp = await _host.Public.SendAsync(up);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("bad_lane_header", resp.Headers.GetValues("X-Error").Single());
    }

    [Fact]
    public async Task Non_Lanes_Mode_Rejects_A_Stray_Lane_Header()
    {
        await using var plain = await RelayHost.StartAsync(carrierMode: "https");
        var bootstrap = plain.MintBootstrap();
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/session");
        create.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bootstrap);
        create.Content = new ByteArrayContent(FrameCodec.Encode(FrameType.Hello, 0, [1]));
        using var cr = await plain.Public.SendAsync(create);
        var token = cr.Headers.GetValues("X-Session-Token").Single();

        using var up = new HttpRequestMessage(HttpMethod.Post, "/api/v1/up");
        up.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        up.Headers.TryAddWithoutValidation("X-Up-Seq", "1");
        up.Headers.TryAddWithoutValidation("X-Lane-ID", "5");
        up.Content = new ByteArrayContent(FrameCodec.Encode(FrameType.Open, 5));
        Assert.Equal(HttpStatusCode.BadRequest, (await plain.Public.SendAsync(up)).StatusCode);
    }
}
