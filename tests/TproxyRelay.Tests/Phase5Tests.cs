using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using TproxyRelay;

namespace TproxyRelay.Tests;

public class Phase5LanesTests : IAsyncLifetime
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
            CarrierMode = "https-lanes",
        };
        _hub = new RelayHub(_opt, new TokenMinter(new byte[32]), NullLogger.Instance);
    }

    public async Task DisposeAsync() => await _backend.DisposeAsync();

    private static byte[] Hello() => FrameCodec.Encode(FrameType.Hello, 0, [1]);

    private Session NewSession(RelayHub? hub = null)
    {
        hub ??= _hub;
        var bootstrap = hub.MintBootstrap();
        Assert.NotNull(bootstrap);
        Assert.Equal(RedeemResult.Ok, hub.TryRedeemBootstrap(bootstrap!, Hello(), null, out var session));
        return session!;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var all = new byte[parts.Sum(p => p.Length)];
        var off = 0;
        foreach (var p in parts)
        {
            p.CopyTo(all.AsSpan(off));
            off += p.Length;
        }
        return all;
    }

    [Fact]
    public async Task Lane_Echo_Flows_And_Close_Signals_LaneComplete()
    {
        var session = NewSession();
        var payload = new byte[700];
        Random.Shared.NextBytes(payload);
        var open = FrameCodec.Encode(FrameType.Open, 5);
        var data = FrameCodec.Encode(FrameType.Data, 5, payload);

        Assert.Equal(LaneOutcome.Ok, (await _hub.ApplyUpLane(session, 5, 1, Concat(open, data))).Outcome);

        // Downlink carries only lane-5 frames (WINDOW + echoed DATA).
        using var cts = new CancellationTokenSource(5000);
        DownResult down = default;
        var cursor = 0L;
        for (var i = 0; i < 20; i++)
        {
            down = await _hub.GetDownLane(session, 5, cursor, cts.Token);
            if (down.HasBatch && FrameCodec.ParseAll(down.Body!.Payload).Any(f => f.Type == FrameType.Data))
                break;
            if (down.HasBatch)
                cursor = down.Cursor; // acknowledging happens on the next poll
        }
        Assert.True(down.HasBatch);
        var frames = FrameCodec.ParseAll(down.Body!.Payload);
        Assert.All(frames, f => Assert.Equal(5u, f.StreamId));
        Assert.Contains(frames, f => f.Type == FrameType.Window);
        Assert.Contains(frames, f => f.Type == FrameType.Data);
        cursor = down.Cursor;

        // Acknowledge, then close the stream from the client.
        var acked = await _hub.GetDownLane(session, 5, cursor, cts.Token);
        Assert.False(acked.HasBatch);
        Assert.Equal(LaneOutcome.Ok,
            (await _hub.ApplyUpLane(session, 5, 2, FrameCodec.Encode(FrameType.Close, 5))).Outcome);

        // Client-initiated CLOSE needs no echo frame: once the lane drained,
        // the next poll reports the lane as complete.
        DownResult complete = default;
        for (var i = 0; i < 20; i++)
        {
            complete = await _hub.GetDownLane(session, 5, cursor, cts.Token);
            if (complete.LaneClosed) break;
            if (complete.HasBatch)
                cursor = complete.Cursor; // ack leftovers on the next poll
        }
        Assert.True(complete.LaneClosed);
    }

    [Fact]
    public async Task Lanes_Have_Independent_Sequence_Numbers()
    {
        var session = NewSession();
        Assert.Equal(LaneOutcome.Ok,
            (await _hub.ApplyUpLane(session, 5, 1, FrameCodec.Encode(FrameType.Open, 5))).Outcome);
        Assert.Equal(LaneOutcome.Ok,
            (await _hub.ApplyUpLane(session, 7, 1, FrameCodec.Encode(FrameType.Open, 7))).Outcome);
        // Both lanes committed seq 1; a seq-2 gap check is per lane.
        Assert.Equal(LaneOutcome.Ok,
            (await _hub.ApplyUpLane(session, 7, 2, FrameCodec.Encode(FrameType.Data, 7, new byte[10]))).Outcome);
        var gap = await _hub.ApplyUpLane(session, 5, 3, FrameCodec.Encode(FrameType.Data, 5, new byte[10]));
        Assert.Equal(LaneOutcome.Fatal, gap.Outcome); // gap on lane 5 only
    }

    [Fact]
    public async Task CrossLane_Frame_Is_Fatal()
    {
        var session = NewSession();
        var result = await _hub.ApplyUpLane(session, 5, 1,
            Concat(FrameCodec.Encode(FrameType.Open, 5), FrameCodec.Encode(FrameType.Data, 9, new byte[8])));
        Assert.Equal(LaneOutcome.Fatal, result.Outcome);
        Assert.Contains("cross-lane", result.Error);
    }

    [Fact]
    public async Task Lane_Zero_Accepts_Only_Pong()
    {
        var session = NewSession();
        var pong = FrameCodec.Encode(FrameType.Pong, 0, new byte[8]);
        Assert.Equal(LaneOutcome.Ok, (await _hub.ApplyUpLane(session, 0, 1, pong)).Outcome);
        var data = FrameCodec.Encode(FrameType.Data, 0, new byte[8]);
        var bad = await _hub.ApplyUpLane(session, 0, 2, data);
        Assert.Equal(LaneOutcome.Fatal, bad.Outcome);
    }

    [Fact]
    public async Task Lane_Must_Begin_With_Open()
    {
        var session = NewSession();
        var result = await _hub.ApplyUpLane(session, 5, 1, FrameCodec.Encode(FrameType.Data, 5, new byte[8]));
        Assert.Equal(LaneOutcome.Fatal, result.Outcome);
        Assert.Contains("OPEN", result.Error);
    }

    [Fact]
    public async Task Lane_Budget_RetryLater_Leaves_Batch_Unapplied()
    {
        var opt = new RelayOptions($"127.0.0.1:{_backend.Port}")
        {
            Secret = Convert.FromHexString("000102030405060708090a0b0c0d0e0f"),
            CarrierMode = "https-lanes",
            MaxPendingBytesPerSession = 4 * 1024,
        };
        var hub = new RelayHub(opt, new TokenMinter(new byte[32]), NullLogger.Instance);
        var session = NewSession(hub);
        Assert.Equal(LaneOutcome.Ok,
            (await hub.ApplyUpLane(session, 5, 1, FrameCodec.Encode(FrameType.Open, 5))).Outcome);
        var result = await hub.ApplyUpLane(session, 5, 2,
            FrameCodec.Encode(FrameType.Data, 5, new byte[8 * 1024]));
        Assert.Equal(LaneOutcome.RetryLater, result.Outcome);
        // The stream survived and the seq stayed uncommitted.
        Assert.Contains(5u, session.Streams.Keys);
        var retry = await hub.ApplyUpLane(session, 5, 2, FrameCodec.Encode(FrameType.Close, 5));
        Assert.Equal(LaneOutcome.Ok, retry.Outcome);
    }

    [Fact]
    public void LanesMode_Session_Gets_LaneZero()
    {
        var session = NewSession();
        Assert.True(session.LanesMode);
        Assert.Contains(0u, session.Lanes.Keys);
        var wsOpt = new RelayOptions($"127.0.0.1:{_backend.Port}")
        {
            Secret = Convert.FromHexString("000102030405060708090a0b0c0d0e0f"),
            CarrierMode = "websocket-lanes",
        };
        var wsHub = new RelayHub(wsOpt, new TokenMinter(new byte[32]), NullLogger.Instance);
        var wsSession = NewSession(wsHub);
        Assert.True(wsSession.LanesMode);
        Assert.Contains(0u, wsSession.Lanes.Keys);
    }
}
