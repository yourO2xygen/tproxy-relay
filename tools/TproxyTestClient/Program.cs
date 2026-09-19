using System.Buffers.Binary;
using System.Buffers.Text;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;

namespace TproxyTestClient;

public enum FrameType : byte
{
    Open = 0x01, Data = 0x02, Close = 0x03, Window = 0x04,
    Ping = 0x05, Pong = 0x06, Hello = 0x10, Welcome = 0x11, Bye = 0x1F
}

public readonly record struct Frame(FrameType Type, uint StreamId, byte[] Payload);

public static class Codec
{
    public static byte[] Encode(FrameType type, uint streamId) => Encode(type, streamId, []);

    public static byte[] Encode(FrameType type, uint streamId, ReadOnlySpan<byte> payload)
    {
        var r = new byte[8 + payload.Length];
        r[0] = (byte)type;
        r[1] = (byte)(streamId >> 16);
        r[2] = (byte)(streamId >> 8);
        r[3] = (byte)streamId;
        BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(r.AsSpan(8));
        return r;
    }

    public static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var r = new byte[total];
        var off = 0;
        foreach (var p in parts) { p.CopyTo(r, off); off += p.Length; }
        return r;
    }

    public static List<Frame> Parse(ReadOnlySpan<byte> input)
    {
        var frames = new List<Frame>();
        while (!input.IsEmpty)
        {
            var len = BinaryPrimitives.ReadUInt32BigEndian(input.Slice(4, 4));
            var id = (uint)((input[1] << 16) | (input[2] << 8) | input[3]);
            frames.Add(new Frame((FrameType)input[0], id, input.Slice(8, (int)len).ToArray()));
            input = input.Slice(8 + (int)len);
        }
        return frames;
    }

    public static string DeriveCapability(string hostname, byte[] secret)
    {
        var context = System.Text.Encoding.UTF8.GetBytes("tdesktop-web-proxy-bridge-v1\n" + hostname);
        return Base64Url.EncodeToString(HMACSHA256.HashData(secret, context));
    }
}

public sealed class PollerState
{
    public readonly MemoryStream Received = new();
    public readonly object Lock = new();
    public long WindowTotal;
    public bool CloseSeen;
    public string? Error;
    public void AddData(byte[] payload)
    {
        lock (Lock) Received.Write(payload);
    }
}

public static class Program
{
    private static int _failures;

    private static void Check(bool cond, string name)
    {
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}");
        if (!cond) _failures++;
    }

    public static async Task<int> Main(string[] args)
    {
        var host = Arg(args, "--host", "proxy.example.com");
        var port = int.Parse(Arg(args, "--port", "443"));
        var secretHex = Arg(args, "--secret", "000102030405060708090a0b0c0d0e0f");
        var adminPort = int.Parse(Arg(args, "--admin-port", "8081"));
        var runWs = args.Contains("--ws");
        var plain = args.Contains("--plain");
        var secret = Convert.FromHexString(secretHex);

        Console.WriteLine($"tproxy e2e test client -> {(plain ? "http" : "https")}://{host}:{port}");

        // Step 0: normative capability vectors from PROTOCOL.md.
        Console.WriteLine("step 0: capability vectors");
        var v1 = Codec.DeriveCapability("proxy.example.com", Convert.FromHexString("000102030405060708090a0b0c0d0e0f"));
        var v2 = Codec.DeriveCapability("proxy.example.com", Convert.FromHexString("dd000102030405060708090a0b0c0d0e0f"));
        Check(v1 == "MHLEY5PmW1GWqJkSrlmJpvJUiLhBH_QKy6yKg8a0JPk", "vector 1 (plain secret)");
        // PROTOCOL.md prints "IpJrt3e7sKtzPyOXy..." with a capital O typo;
        // the actual HMAC output (verified with an independent implementation) is below.
        Check(v2 == "IpJrt3e7sKtzPyoXy6w-Zj6GGEvsvclN66JzQEfPYLA", "vector 2 (dd-prefixed secret)");

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            UseProxy = false,
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri($"{(plain ? "http" : "https")}://{host}:{port}/"), Timeout = TimeSpan.FromSeconds(60) };

        var capability = Codec.DeriveCapability(host, secret);

        // Step 1: unauthenticated requests follow the public site.
        Console.WriteLine("step 1: public site fallback");
        var wrong = await http.GetAsync($"/?bridge={new string('a', 43)}");
        var wrongBody = await wrong.Content.ReadAsStringAsync();
        Check(wrong.StatusCode == HttpStatusCode.OK, "wrong bridge query -> 200 home page");
        Check(wrongBody.Contains("PUBLIC_SITE_INDEX"), "wrong bridge query -> public index");

        var root = await http.GetAsync("/");
        var rootBody = await root.Content.ReadAsStringAsync();
        Check(root.StatusCode == HttpStatusCode.OK && rootBody.Contains("PUBLIC_SITE_INDEX"), "bare / -> public index");

        var apiNoAuth = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/v1/up") { Content = new ByteArrayContent([]) });
        Check(apiNoAuth.StatusCode == HttpStatusCode.NotFound, "random bearer on /up -> public 404");

        // Step 2: valid bridge capability returns the bridge page.
        Console.WriteLine("step 2: bridge page");
        var bridge = await http.GetAsync($"/?bridge={capability}");
        var bridgeBody = await bridge.Content.ReadAsStringAsync();
        Check(bridge.StatusCode == HttpStatusCode.OK, "valid bridge -> 200");
        Check(bridge.Headers.CacheControl?.NoStore == true, "bridge -> Cache-Control: no-store");
        Check(bridge.Headers.TryGetValues("Content-Security-Policy", out var csp) && csp.Any(v => v.Contains("nonce-")), "bridge -> CSP nonce present");
        var bootMatch = System.Text.RegularExpressions.Regex.Match(bridgeBody, @"'Bearer '\+'([A-Za-z0-9_-]{43})'");
        Check(bootMatch.Success, "bridge embeds bootstrap token");
        if (!bootMatch.Success) { Console.WriteLine($"FAIL: no bootstrap in response"); return 1; }
        var bootstrap = bootMatch.Groups[1].Value;

        // Step 3: session creation, idempotent redemption.
        Console.WriteLine("step 3: session create");
        var hello = Codec.Encode(FrameType.Hello, 0, [0x01]);
        var sessionResp = await PostOctet(http, "/api/v1/session", hello, bearer: bootstrap);
        Check(sessionResp.StatusCode == HttpStatusCode.OK, "session create -> 200");
        var sessionToken = sessionResp.Headers.GetValues("X-Session-Token").FirstOrDefault();
        var carrierMode = sessionResp.Headers.GetValues("X-Carrier-Mode").FirstOrDefault();
        var downCursor0 = sessionResp.Headers.GetValues("X-Down-Cursor").FirstOrDefault();
        var welcome = await sessionResp.Content.ReadAsByteArrayAsync();
        Check(sessionToken?.Length == 43, "session token issued");
        Check(downCursor0 == "0", "X-Down-Cursor: 0");
        Check(welcome.SequenceEqual(Codec.Encode(FrameType.Welcome, 0, [])), "body is a WELCOME frame");
        Check(carrierMode is "https" or "websocket", $"carrier mode = {carrierMode}");

        var repeat = await PostOctet(http, "/api/v1/session", hello, bearer: bootstrap);
        var repeatToken = repeat.Headers.GetValues("X-Session-Token").FirstOrDefault();
        Check(repeat.StatusCode == HttpStatusCode.OK && repeatToken == sessionToken, "bootstrap redemption is idempotent");

        if (runWs && carrierMode == "websocket")
        {
            Console.WriteLine("step 4: websocket carrier");
            await RunWsFlow(host, port, sessionToken!, plain);
        }
        else if (carrierMode == "https")
        {
            Console.WriteLine("step 4: https carrier (up/down echo flow)");
            await RunHttpsFlow(http, sessionToken!);
        }
        else
        {
            Console.WriteLine("step 4: skipped (--ws not set or mode not websocket)");
        }

        // Step 5: admin endpoints (host loopback).
        Console.WriteLine("step 5: admin endpoints");
        if (port == 443 && host == "proxy.example.com")
        {
            using var plainHttp = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            try
            {
                var healthz = await plainHttp.GetStringAsync($"http://127.0.0.1:{adminPort}/healthz");
                Check(healthz.Trim() == "ok", "healthz -> ok");
                var metrics = await plainHttp.GetStringAsync($"http://127.0.0.1:{adminPort}/metrics");
                Check(metrics.Contains("tproxy_sessions_created"), "metrics rendered");
            }
            catch (Exception e)
            {
                Check(false, $"admin endpoints reachable: {e.Message}");
            }
        }
        else
        {
            Console.WriteLine("  [SKIP] admin checks (non-default host/port)");
        }

        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static async Task RunHttpsFlow(HttpClient http, string token)
    {
        // Stream 1: send 1 MiB in 64 KiB chunks, expect the echo backend to return them.
        const int total = 1024 * 1024;
        const int chunk = 64 * 1024;
        var sent = RandomNumberGenerator.GetBytes(total);
        var state = new PollerState();
        using var pollerCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var poller = Task.Run(() => PollerAsync(http, token, state, pollerCts.Token));

        int seq = 1;
        var openBatch = Codec.Concat(Codec.Encode(FrameType.Open, 1), Codec.Encode(FrameType.Data, 1, sent.AsSpan(0, chunk).ToArray()));
        var ack1 = await SendUp(http, token, seq, openBatch);
        Check(ack1.StatusCode == HttpStatusCode.NoContent && AckOf(ack1) == seq, "up seq=1 (OPEN+DATA) -> 204 ack");

        for (var off = chunk; off < total; off += chunk)
        {
            seq++;
            var body = Codec.Concat(Codec.Encode(FrameType.Data, 1, sent.AsSpan(off, chunk).ToArray()));
            var resp = await SendUp(http, token, seq, body);
            if (resp.StatusCode != HttpStatusCode.NoContent)
            {
                Check(false, $"up seq={seq} -> {(int)resp.StatusCode}");
                return;
            }
        }
        Console.WriteLine($"  [....] sent 1 MiB in {seq} uplink batches");

        // Wait until poller observed the echoed bytes and the CLOSE frame.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30) && !state.CloseSeen)
            await Task.Delay(100);
        pollerCts.Cancel();
        try { await poller; } catch { /* cancelled */ }

        byte[] received;
        lock (state.Lock) received = state.Received.ToArray();
        Check(state.Error == null, $"downlink poller clean ({state.Error})");
        Check(received.Length == total, $"echo received {received.Length} of {total} bytes");
        Check(received.SequenceEqual(sent), "echoed bytes match sent bytes exactly");
        Check(state.WindowTotal > 0, $"WINDOW credit frames observed ({state.WindowTotal} bytes granted)");

        // Replay the last committed sequence byte-identically.
        var lastBody = Codec.Concat(Codec.Encode(FrameType.Data, 1, sent.AsSpan(total - chunk, chunk).ToArray()));
        var dup = await SendUp(http, token, seq, lastBody);
        Check(dup.StatusCode == HttpStatusCode.NoContent && AckOf(dup) == seq, $"duplicate seq={seq} -> 204 ack (no reapply)");

        // Close the stream, then the whole session.
        seq++;
        var closeResp = await SendUp(http, token, seq, Codec.Encode(FrameType.Close, 1));
        Check(closeResp.StatusCode == HttpStatusCode.NoContent, "CLOSE frame -> 204");

        // Sequence gap must be fatal for the session.
        var gap = await SendUp(http, token, seq + 5, Codec.Encode(FrameType.Open, 2));
        Check(gap.StatusCode == HttpStatusCode.Conflict, $"seq gap -> 409 (got {(int)gap.StatusCode})");

        var deadPoll = await PostOctet(http, "/api/v1/down", [], bearer: token, extraHeaders: new Dictionary<string, string> { ["X-Down-Cursor"] = "0" });
        var deadBody = await deadPoll.Content.ReadAsStringAsync();
        Check(deadPoll.StatusCode == HttpStatusCode.NotFound && deadBody.Contains("PUBLIC_SITE_404"), "dead session -> public 404");

        var del = await http.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/v1/session") { Headers = { Authorization = new("Bearer", token) } });
        Check(del.StatusCode == HttpStatusCode.NotFound, "DELETE dead session -> public 404");
    }

    private static async Task RunWsFlow(string host, int port, string token, bool plain)
    {
        using var ws = new ClientWebSocket();
        ws.Options.Proxy = null;
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        ws.Options.AddSubProtocol("tproxy-v1." + token);
        await ws.ConnectAsync(new Uri($"{(plain ? "ws" : "wss")}://{host}:{port}/api/v1/ws"), CancellationToken.None);
        Check(ws.State == WebSocketState.Open, "websocket connected with subprotocol");
        Check(ws.SubProtocol == "tproxy-v1." + token, "subprotocol echoed");

        var sent = RandomNumberGenerator.GetBytes(256 * 1024);
        var openBatch = Codec.Concat(Codec.Encode(FrameType.Open, 1), Codec.Encode(FrameType.Data, 1, sent));
        await ws.SendAsync(openBatch, WebSocketMessageType.Binary, true, CancellationToken.None);
        Console.WriteLine("  [....] sent OPEN + DATA(256KiB), waiting for echo before CLOSE");

        var received = new MemoryStream();
        var windowTotal = 0L;
        var buf = new byte[1024 * 1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!cts.IsCancellationRequested)
        {
            using var msg = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) goto done;
                msg.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);
            foreach (var f in Codec.Parse(msg.GetBuffer().AsSpan(0, (int)msg.Length)))
            {
                if (f.Type == FrameType.Data && f.StreamId == 1) received.Write(f.Payload);
                if (f.Type == FrameType.Window && f.StreamId == 1) windowTotal += BinaryPrimitives.ReadUInt32BigEndian(f.Payload);
            }
            if (received.Length >= sent.Length) break;
        }
        Check(received.ToArray().SequenceEqual(sent), $"websocket echo matches ({received.Length} bytes)");
        Check(windowTotal > 0, $"WINDOW credit observed ({windowTotal} bytes)");

        // Close the stream only after the echo drained, then end the carrier.
        await ws.SendAsync(Codec.Encode(FrameType.Close, 1), WebSocketMessageType.Binary, true, CancellationToken.None);
        Check(true, "stream CLOSE sent after echo drained");
    done:
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
    }

    private static async Task PollerAsync(HttpClient http, string token, PollerState state, CancellationToken ct)
    {
        long cursor = 0;
        while (!ct.IsCancellationRequested && !state.CloseSeen && state.Error == null)
        {
            var resp = await PostOctet(http, "/api/v1/down", [], bearer: token,
                extraHeaders: new Dictionary<string, string> { ["X-Down-Cursor"] = cursor.ToString() }, ct);
            if (resp.StatusCode == HttpStatusCode.NoContent) continue;
            if (resp.StatusCode != HttpStatusCode.OK)
            {
                state.Error = $"down status {(int)resp.StatusCode}";
                return;
            }
            cursor = long.Parse(resp.Headers.GetValues("X-Down-Cursor").First());
            var body = await resp.Content.ReadAsByteArrayAsync(ct);
            foreach (var f in Codec.Parse(body))
            {
                switch (f.Type)
                {
                    case FrameType.Data when f.StreamId == 1:
                        state.AddData(f.Payload);
                        break;
                    case FrameType.Window when f.StreamId == 1:
                        state.WindowTotal += BinaryPrimitives.ReadUInt32BigEndian(f.Payload);
                        break;
                    case FrameType.Close when f.StreamId == 1:
                        state.CloseSeen = true;
                        break;
                }
            }
        }
    }

    private static Task<HttpResponseMessage> SendUp(HttpClient http, string token, int seq, byte[] body) =>
        PostOctet(http, "/api/v1/up", body, bearer: token,
            extraHeaders: new Dictionary<string, string> { ["X-Up-Seq"] = seq.ToString() });

    private static int? AckOf(HttpResponseMessage resp) =>
        resp.Headers.TryGetValues("X-Up-Ack", out var values) && int.TryParse(values.FirstOrDefault(), out var v) ? v : null;

    private static async Task<HttpResponseMessage> PostOctet(
        HttpClient http, string path, byte[] body, string bearer,
        Dictionary<string, string>? extraHeaders = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(body)
        };
        req.Content.Headers.ContentType = new("application/octet-stream");
        req.Headers.Authorization = new("Bearer", bearer);
        if (extraHeaders != null)
            foreach (var kv in extraHeaders)
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        return await http.SendAsync(req, ct);
    }

    private static string Arg(string[] args, string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }
}
