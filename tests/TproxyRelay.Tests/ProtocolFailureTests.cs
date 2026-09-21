using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using TproxyRelay;

namespace TproxyRelay.Tests;

/// <summary>TEST-006: protocol failure paths that previously had no asserts
/// at all — duplicate seq with a different body, foreign cursors, concurrent
/// uplink, dial-cap rejection.</summary>
public sealed class ProtocolFailureTests : IAsyncLifetime
{
    private EchoBackend _backend = null!;
    private RelayHub _hub = null!;

    public async Task InitializeAsync()
    {
        _backend = await EchoBackend.StartAsync();
        _hub = new RelayHub(new RelayOptions($"127.0.0.1:{_backend.Port}")
        {
            Secret = Convert.FromHexString("000102030405060708090a0b0c0d0e0f"),
        }, new TokenMinter(new byte[32]), NullLogger.Instance);
    }

    public async Task DisposeAsync() => await _backend.DisposeAsync();

    private async Task<Session> SessionAsync(string mode = "https")
    {
        var bootstrap = _hub.MintBootstrap();
        Assert.NotNull(bootstrap);
        var r = _hub.TryRedeemBootstrap(bootstrap!, FrameCodec.Encode(FrameType.Hello, 0, [1]), null, out var s);
        Assert.Equal(RedeemResult.Ok, r);
        Assert.NotNull(s);
        return s!;
    }

    private static byte[] Up(params byte[][] frames)
    {
        var total = frames.Sum(f => f.Length);
        var body = new byte[total];
        var off = 0;
        foreach (var f in frames)
        {
            f.CopyTo(body.AsSpan(off));
            off += f.Length;
        }
        return body;
    }

    [Fact]
    public async Task Duplicate_Seq_With_Different_Body_Is_Fatal()
    {
        var s = await SessionAsync();
        var first = Up(FrameCodec.Encode(FrameType.Open, 1));
        Assert.Equal(UpOutcome.Acked, (await _hub.ApplyUp(s, 1, first)).Outcome);
        var replay = Up(FrameCodec.Encode(FrameType.Open, 2));
        var result = await _hub.ApplyUp(s, 1, replay);
        Assert.Equal(UpOutcome.Fatal, result.Outcome);
        Assert.Contains("duplicate seq", result.Error);
        // Fatal instructs the endpoint to close; the hub itself stays neutral.
        Assert.False(s.Dead);
    }

    [Fact]
    public async Task Byte_Identical_Replay_Is_Acked()
    {
        var s = await SessionAsync();
        var body = Up(FrameCodec.Encode(FrameType.Open, 1));
        Assert.Equal(UpOutcome.Acked, (await _hub.ApplyUp(s, 1, body)).Outcome);
        Assert.Equal(UpOutcome.DuplicateAcked, (await _hub.ApplyUp(s, 1, body)).Outcome);
        Assert.False(s.Dead);
    }

    [Fact]
    public async Task Foreign_Cursor_Is_A_Protocol_Error()
    {
        var s = await SessionAsync();
        using var cts = new CancellationTokenSource(2000);
        var result = await _hub.GetDown(s, 4242, cts.Token);
        Assert.True(result.ProtocolError);
    }

    [Fact]
    public async Task Concurrent_Uplink_Is_Rejected_Not_Applied()
    {
        var s = await SessionAsync();
        Interlocked.Exchange(ref s.UpInFlight, 1); // an uplink already in flight
        var result = await _hub.ApplyUp(s, 1, Up(FrameCodec.Encode(FrameType.Open, 1)));
        Assert.Equal(UpOutcome.RetryLater, result.Outcome);
        Assert.False(s.Dead);
    }

    [Fact]
    public async Task Dial_Cap_Rejects_The_Stream_Not_The_Session()
    {
        var hub = new RelayHub(new RelayOptions($"127.0.0.1:{_backend.Port}")
        {
            Secret = Convert.FromHexString("000102030405060708090a0b0c0d0e0f"),
            MaxBackendDialsInFlight = 0, // every dial is over the cap
        }, new TokenMinter(new byte[32]), NullLogger.Instance);
        var bootstrap = hub.MintBootstrap();
        Assert.Equal(RedeemResult.Ok,
            hub.TryRedeemBootstrap(bootstrap!, FrameCodec.Encode(FrameType.Hello, 0, [1]), null, out var s));
        Assert.Equal(UpOutcome.Acked,
            (await hub.ApplyUp(s!, 1, Up(FrameCodec.Encode(FrameType.Open, 7)))).Outcome);
        Assert.False(s!.Dead); // stream-scoped: the session survives
        // The rejection is a CLOSE frame for stream 7 in the downlink queue.
        using var cts = new CancellationTokenSource(2000);
        var down = await hub.GetDown(s, 0, cts.Token);
        Assert.True(down.HasBatch);
        var frames = FrameCodec.ParseAll(down.Body!.Payload);
        Assert.Contains(frames, f => f.Type == FrameType.Close && f.StreamId == 7);
    }

    [Fact]
    public void ParseAll_Rejects_Batches_Over_MaxBatchFrames()
    {
        var frame = FrameCodec.Encode(FrameType.Pong, 0);
        var body = new byte[frame.Length * (FrameCodec.MaxBatchFrames + 1)];
        for (var i = 0; i <= FrameCodec.MaxBatchFrames; i++)
            frame.CopyTo(body.AsSpan(i * frame.Length));
        Assert.Throws<FrameException>(() => FrameCodec.ParseAll(body, 1024 * 1024));
    }
}
