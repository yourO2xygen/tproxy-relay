using System.Net;
using Xunit;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using TproxyRelay;

namespace TproxyRelay.Tests;

/// <summary>In-process TCP echo backend: accepts connections and mirrors bytes.</summary>
public sealed class EchoBackend : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly List<TcpClient> _clients = [];
    private readonly CancellationTokenSource _cts = new();

    private EchoBackend(TcpListener listener) => _listener = listener;

    public static async Task<EchoBackend> StartAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var backend = new EchoBackend(listener);
        _ = backend.RunAsync();
        await Task.Yield();
        return backend;
    }

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
            lock (_clients) _clients.Add(client);
            _ = EchoLoop(client);
        }
    }

    private async Task EchoLoop(TcpClient client)
    {
        using var _ = client;
        try
        {
            var buf = new byte[16 * 1024];
            var stream = client.GetStream();
            while (!_cts.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(buf, _cts.Token);
                if (n == 0) break;
                await stream.WriteAsync(buf.AsMemory(0, n), _cts.Token);
            }
        }
        catch { /* client gone */ }
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        lock (_clients)
            foreach (var c in _clients)
                try { c.Close(); } catch { }
        await Task.Yield();
        _cts.Dispose();
    }
}

public class RelayHubTests : IAsyncLifetime
{
    private EchoBackend _backend = null!;
    private RelayHub _hub = null!;
    private RelayOptions _opt = null!;

    public async Task InitializeAsync()
    {
        _backend = await EchoBackend.StartAsync();
        _opt = new RelayOptions($"127.0.0.1:{_backend.Port}")
        {
            Secret = Convert.FromHexString("000102030405060708090a0b0c0d0e0f"),
            MaxStreamsGlobal = 4096,
            MaxPendingBytesPerSession = 32 * 1024 * 1024,
        };
        _hub = new RelayHub(_opt, new TokenMinter(new byte[32]), NullLogger.Instance);
    }

    public async Task DisposeAsync() => await _backend.DisposeAsync();

    private static byte[] Hello() => FrameCodec.Encode(FrameType.Hello, 0, [1]);

    private Session NewSession()
    {
        var bootstrap = _hub.MintBootstrap();
        Assert.NotNull(bootstrap);
        var result = _hub.TryRedeemBootstrap(bootstrap!, Hello(), null, out var session);
        Assert.Equal(RedeemResult.Ok, result);
        Assert.NotNull(session);
        return session!;
    }

    private async Task<List<Frame>> DownAsync(Session session, long cursor, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var down = await _hub.GetDown(session, cursor, cts.Token);
        if (!down.HasBatch) return [];
        return FrameCodec.ParseAll(down.Body!);
    }

    [Fact]
    public void Bootstrap_Redemption_IsIdempotent_SameBody_SameSession()
    {
        var bootstrap = _hub.MintBootstrap()!;
        var r1 = _hub.TryRedeemBootstrap(bootstrap, Hello(), null, out var s1);
        var r2 = _hub.TryRedeemBootstrap(bootstrap, Hello(), null, out var s2);
        Assert.Equal(RedeemResult.Ok, r1);
        Assert.Equal(RedeemResult.Ok, r2);
        Assert.Equal(s1!.Token, s2!.Token);
    }

    [Fact]
    public void Bootstrap_Replay_WithDifferentBody_IsInvalid()
    {
        var bootstrap = _hub.MintBootstrap()!;
        Assert.Equal(RedeemResult.Ok, _hub.TryRedeemBootstrap(bootstrap, Hello(), null, out _));
        var other = FrameCodec.Encode(FrameType.Hello, 0, [2]);
        Assert.Equal(RedeemResult.Invalid, _hub.TryRedeemBootstrap(bootstrap, other, null, out _));
    }

    [Fact]
    public void Bootstrap_Cap_ReturnsNull()
    {
        var small = new RelayHub(
            new RelayOptions($"127.0.0.1:{_backend.Port}")
            {
                Secret = Convert.FromHexString("000102030405060708090a0b0c0d0e0f"),
                MaxBootstrapsGlobal = 1,
            },
            new TokenMinter(new byte[32]), NullLogger.Instance);
        Assert.NotNull(small.MintBootstrap());
        Assert.Null(small.MintBootstrap());
    }

    [Fact]
    public async Task Up_EchoFlows_Back_And_WindowGranted()
    {
        var session = NewSession();
        var open = FrameCodec.Encode(FrameType.Open, 5);
        var payload = new byte[1000];
        Random.Shared.NextBytes(payload);
        var data = FrameCodec.Encode(FrameType.Data, 5, payload);

        var ack = await _hub.ApplyUp(session, 1, open.Concat(data).ToArray());
        Assert.Equal(UpOutcome.Acked, ack.Outcome);

        // downlink: WINDOW credit + echoed DATA must both arrive
        var gotWindow = false;
        var echo = new byte[0];
        for (var i = 0; i < 10 && (!gotWindow || echo.Length == 0); i++)
        {
            foreach (var f in await DownAsync(session, 0))
            {
                if (f.Type == FrameType.Window) gotWindow = true;
                if (f.Type == FrameType.Data && f.StreamId == 5) echo = f.Payload.ToArray();
            }
        }
        Assert.True(gotWindow, "WINDOW credit not received");
        Assert.Equal(payload, echo);
    }

    [Fact]
    public async Task Up_DuplicateSeq_SameBody_IsAcked()
    {
        var session = NewSession();
        var body = FrameCodec.Encode(FrameType.Open, 5);
        Assert.Equal(UpOutcome.Acked, (await _hub.ApplyUp(session, 1, body)).Outcome);
        Assert.Equal(UpOutcome.DuplicateAcked, (await _hub.ApplyUp(session, 1, body)).Outcome);
    }

    [Fact]
    public async Task Up_SequenceGap_IsFatal()
    {
        var session = NewSession();
        var body = FrameCodec.Encode(FrameType.Open, 5);
        var result = await _hub.ApplyUp(session, 2, body);
        Assert.Equal(UpOutcome.Fatal, result.Outcome);
    }

    [Fact]
    public async Task Up_BudgetPrecheck_LeavesBatchUnapplied()
    {
        var tight = new RelayOptions($"127.0.0.1:{_backend.Port}")
        {
            Secret = Convert.FromHexString("000102030405060708090a0b0c0d0e0f"),
            MaxPendingBytesPerSession = 4 * 1024, // smaller than the batch below
        };
        var hub = new RelayHub(tight, new TokenMinter(new byte[32]), NullLogger.Instance);
        var bootstrap = hub.MintBootstrap()!;
        var session = hub.TryRedeemBootstrap(bootstrap, Hello(), null, out var s) == RedeemResult.Ok
            ? s! : throw new InvalidOperationException();

        var open = FrameCodec.Encode(FrameType.Open, 5);
        Assert.Equal(UpOutcome.Acked, (await hub.ApplyUp(session, 1, open)).Outcome);

        var data = FrameCodec.Encode(FrameType.Data, 5, new byte[8 * 1024]);
        var retry = await hub.ApplyUp(session, 2, data);
        Assert.Equal(UpOutcome.RetryLater, retry.Outcome);

        // Atomicity: seq stays uncommitted and the session still accepts
        // the byte-identical retry path (a later, smaller batch at the same seq).
        Assert.Equal(1, session.LastSeq);
        Assert.False(session.Dead);
    }

    [Fact]
    public async Task Streams_GlobalCap_SendsCloseFrame()
    {
        var capped = new RelayOptions($"127.0.0.1:{_backend.Port}")
        {
            Secret = Convert.FromHexString("000102030405060708090a0b0c0d0e0f"),
            MaxStreamsGlobal = 1,
        };
        var hub = new RelayHub(capped, new TokenMinter(new byte[32]), NullLogger.Instance);
        var bootstrap = hub.MintBootstrap()!;
        Assert.Equal(RedeemResult.Ok, hub.TryRedeemBootstrap(bootstrap, Hello(), null, out var session));

        var up1 = await hub.ApplyUp(session!, 1, FrameCodec.Encode(FrameType.Open, 1));
        Assert.Equal(UpOutcome.Acked, up1.Outcome);
        var up2 = await hub.ApplyUp(session!, 2, FrameCodec.Encode(FrameType.Open, 2));
        Assert.Equal(UpOutcome.Acked, up2.Outcome);

        // second OPEN must produce a CLOSE frame for the capped stream
        var sawClose = false;
        for (var i = 0; i < 10 && !sawClose; i++)
            foreach (var f in await DownAsync(session!, 0, 500))
                if (f.Type == FrameType.Close && f.StreamId == 2)
                    sawClose = true;
        Assert.True(sawClose, "no CLOSE frame for stream over global cap");
    }

    [Fact]
    public void Tombstones_EvictOldest_ButKeepRecent()
    {
        var session = new Session { Token = "test" };
        for (uint i = 1; i <= 5000; i++)
            session.AddTombstone(i);
        Assert.False(session.IsTombstoned(1));   // evicted
        Assert.False(session.IsTombstoned(900)); // evicted
        Assert.True(session.IsTombstoned(4999)); // retained
        Assert.True(session.IsTombstoned(5000));
    }

    [Fact]
    public async Task CloseSession_StopsStreams_AndQueueCompletes()
    {
        var session = NewSession();
        Assert.Equal(UpOutcome.Acked,
            (await _hub.ApplyUp(session, 1, FrameCodec.Encode(FrameType.Open, 9))).Outcome);
        Assert.True(session.Streams.ContainsKey(9));

        _hub.CloseSession(session, "test");
        Assert.True(session.Dead);
        Assert.False(session.Streams.ContainsKey(9));
        Assert.True(session.DownQueue.Reader.Completion.IsCompleted ||
                    session.Cts.IsCancellationRequested);
    }
}
