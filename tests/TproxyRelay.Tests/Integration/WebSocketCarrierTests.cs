using System.Net;
using System.Net.WebSockets;
using System.Text;
using Xunit;

namespace TproxyRelay.Tests.Integration;

/// <summary>TEST-003: both websocket carriers over real sockets — happy-path
/// echo, text rejection, oversized messages, subprotocol discipline.</summary>
public sealed class WebSocketCarrierTests : IAsyncLifetime
{
    private RelayHost _host = null!;
    private string _session = null!;

    public async Task InitializeAsync()
    {
        _host = await RelayHost.StartAsync(carrierMode: "websocket");
        _session = await CreateSessionAsync();
    }
    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<string> CreateSessionAsync()
    {
        var bootstrap = _host.MintBootstrap();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/session");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bootstrap);
        req.Content = new ByteArrayContent(FrameCodec.Encode(FrameType.Hello, 0, [1]));
        using var resp = await _host.Public.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return resp.Headers.GetValues("X-Session-Token").Single();
    }

    private async Task<ClientWebSocket> ConnectAsync(string subprotocol)
    {
        var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol(subprotocol);
        await ws.ConnectAsync(new Uri(_host.PublicBase.Replace("http", "ws") + "/api/v1/ws"), CancellationToken.None);
        return ws;
    }

    [Fact]
    public async Task Happy_Path_Echo_Through_The_Real_Backend()
    {
        using var ws = await ConnectAsync("tproxy-v1." + _session);
        var payload = "hello over ws"u8.ToArray();
        var up = new byte[FrameCodec.Encode(FrameType.Open, 1).Length +
                          FrameCodec.Encode(FrameType.Data, 1, payload).Length +
                          FrameCodec.EncodeWindow(1, 1 << 20).Length];
        var off = 0;
        foreach (var f in new[]
                 {
                     FrameCodec.Encode(FrameType.Open, 1),
                     FrameCodec.Encode(FrameType.Data, 1, payload),
                     FrameCodec.EncodeWindow(1, 1 << 20),
                 })
        {
            f.CopyTo(up.AsSpan(off));
            off += f.Length;
        }
        await ws.SendAsync(up, WebSocketMessageType.Binary, true, CancellationToken.None);

        // Expect WELCOME-lane echo: the relay answers with the backend's bytes
        // (a DATA frame on stream 1 carrying the same payload).
        var buf = new byte[64 * 1024];
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        while (true)
        {
            var msg = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buf, deadline.Token);
                msg.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);
            var frames = FrameCodec.ParseAll(msg.ToArray());
            foreach (var f in frames)
                if (f.Type == FrameType.Data && f.StreamId == 1 &&
                    f.Payload.ToArray().SequenceEqual(payload))
                    return; // echoed
        }
    }

    [Fact]
    public async Task Text_Messages_Are_Rejected()
    {
        using var ws = await ConnectAsync("tproxy-v1." + _session);
        await ws.SendAsync("text is not allowed"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
        var buf = new byte[1024];
        var r = await ws.ReceiveAsync(buf, CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Close, r.MessageType);
    }

    [Fact]
    public async Task Oversized_Message_Closes_The_Socket()
    {
        using var ws = await ConnectAsync("tproxy-v1." + _session);
        var big = new byte[3 * 1024 * 1024]; // above DownBatchTargetBytes (2 MiB)
        await ws.SendAsync(big, WebSocketMessageType.Binary, true, CancellationToken.None);
        var buf = new byte[1024];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, timeout.Token);
            if (r.MessageType == WebSocketMessageType.Close)
                return; // server hung up on the oversize
        }
    }

    [Fact]
    public async Task Unknown_Subprotocol_Falls_Back_To_The_Public_Site()
    {
        var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("something-else");
        await Assert.ThrowsAnyAsync<WebSocketException>(() =>
            ws.ConnectAsync(new Uri(_host.PublicBase.Replace("http", "ws") + "/api/v1/ws"), CancellationToken.None));
    }

    [Fact]
    public async Task Websocket_Lanes_Carrier_Serves_One_Socket_Per_Stream()
    {
        await using var lanes = await RelayHost.StartAsync(carrierMode: "websocket-lanes");
        var bootstrap = lanes.MintBootstrap();
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/session");
        create.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bootstrap);
        create.Content = new ByteArrayContent(FrameCodec.Encode(FrameType.Hello, 0, [1]));
        using var cr = await lanes.Public.SendAsync(create);
        Assert.Equal("websocket-lanes", cr.Headers.GetValues("X-Carrier-Mode").Single());
        var token = cr.Headers.GetValues("X-Session-Token").Single();

        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol($"tproxy-lane-v1.{token}.5");
        await ws.ConnectAsync(new Uri(lanes.PublicBase.Replace("http", "ws") + "/api/v1/ws"), CancellationToken.None);
        Assert.Equal(WebSocketState.Open, ws.State);

        // The lane socket's first message must be exactly one OPEN frame.
        await ws.SendAsync(FrameCodec.Encode(FrameType.Open, 5), WebSocketMessageType.Binary, true, CancellationToken.None);

        var payload = "lane echo"u8.ToArray();
        var body = FrameCodec.Encode(FrameType.Data, 5, payload)
            .Concat(FrameCodec.EncodeWindow(5, 1 << 20)).ToArray();
        await ws.SendAsync(body, WebSocketMessageType.Binary, true, CancellationToken.None);

        var buf = new byte[64 * 1024];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var msg = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buf, timeout.Token);
                if (r.MessageType == WebSocketMessageType.Close)
                    Assert.Fail("lane socket closed before the echo arrived");
                msg.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);
            if (msg.Length == 0)
                continue;
            foreach (var f in FrameCodec.ParseAll(msg.ToArray()))
                if (f.Type == FrameType.Data && f.StreamId == 5 &&
                    f.Payload.ToArray().SequenceEqual(payload))
                    return;
        }
    }
}
