using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using TproxyRelay;

namespace TproxyRelay.Tests;

/// <summary>
/// REL-001/REL-018: closing a session must return every outstanding charge
/// (queue frames, pending batches, lane queues) to the global gauges —
/// otherwise ordinary client disconnects slowly exhaust the downlink gate.
/// </summary>
public class SessionBudgetTests
{
    private static RelayHub NewHub() =>
        new(new RelayOptions($"127.0.0.1:1")
            {
                Secret = Convert.FromHexString("000102030405060708090a0b0c0d0e0f"),
            },
            new TokenMinter(new byte[32]), NullLogger.Instance);

    private static (long Bytes, long Items) Gauges() =>
        (Counters.PendingBytesGauge, Counters.PendingItemsGauge);

    [Fact]
    public async Task CloseSession_Releases_QueueFrames_And_PendingBatch_Mux()
    {
        var hub = NewHub();
        var s = new Session { Token = "budget-mux", CarrierMode = "https" };
        var before = Gauges();

        for (var k = 0; k < 3; k++)
            await hub.EnqueueDownMuxAsync(s, FrameBuf.Buy(new byte[64]));
        lock (s.Sync)
        {
            // Simulate a GetDown batch handed to the client but never acked.
            s.PendingBatch = new byte[128];
            s.PendingBatchFrames = 2;
            var charge = 128 + 2 * RelayHub.ItemOverhead;
            Interlocked.Add(ref s.PendingBytes, charge);
            Interlocked.Add(ref s.PendingItems, 2);
            Counters.PendingBytes(charge);
            Counters.PendingItems(2);
        }

        var charged = Gauges();
        Assert.True(charged.Bytes > before.Bytes);
        Assert.True(charged.Items > before.Items);

        hub.CloseSession(s, "test");

        Assert.Equal(before, Gauges());
        Assert.Null(s.PendingBatch);
        Assert.False(s.DownQueue.Reader.TryPeek(out _));
    }

    [Fact]
    public async Task CloseSession_Releases_LaneQueues_And_LanePendingBatch()
    {
        var hub = NewHub();
        var s = new Session { Token = "budget-lanes", CarrierMode = "https-lanes" };
        var lane = new LaneState(7);
        s.Lanes[7] = lane;
        var before = Gauges();

        await hub.EnqueueDownLaneAsync(s, FrameBuf.Buy(FrameCodec.EncodeWindow(7, 64)));
        lock (lane.Sync)
        {
            lane.PendingBatch = new byte[32];
            lane.PendingBatchFrames = 1;
            var charge = 32 + RelayHub.ItemOverhead;
            Interlocked.Add(ref lane.QueuedCharge, charge);
            Interlocked.Add(ref lane.QueuedItems, 1);
            Interlocked.Add(ref s.PendingBytes, charge);
            Interlocked.Add(ref s.PendingItems, 1);
            Counters.PendingBytes(charge);
            Counters.PendingItems(1);
        }

        Assert.True(Gauges().Bytes > before.Bytes);

        hub.CloseSession(s, "test");

        Assert.Equal(before, Gauges());
        Assert.Equal(0, Volatile.Read(ref lane.QueuedCharge));
        Assert.Equal(0, Volatile.Read(ref lane.QueuedItems));
        Assert.Null(lane.PendingBatch);
    }

    [Fact]
    public async Task ReleaseAll_Is_Idempotent_Under_Repeated_Calls()
    {
        var hub = NewHub();
        var s = new Session { Token = "budget-idem", CarrierMode = "https-lanes" };
        var lane = new LaneState(3);
        s.Lanes[3] = lane;
        var before = Gauges();

        await hub.EnqueueDownLaneAsync(s, FrameBuf.Buy(FrameCodec.EncodeWindow(3, 64)));
        hub.CloseSession(s, "test");
        Assert.Equal(before, Gauges());

        // Tombstone eviction racing the session close must not double-release.
        lane.ReleaseAll(s);
        lane.ReleaseAll(s);
        hub.CloseSession(s, "again"); // MarkDead guard

        Assert.Equal(before, Gauges());
    }

    [Fact]
    public async Task Repeated_CloseSession_Does_Not_Double_Release()
    {
        var hub = NewHub();
        var s = new Session { Token = "budget-once", CarrierMode = "https" };
        var before = Gauges();

        await hub.EnqueueDownMuxAsync(s, FrameBuf.Buy(new byte[16]));
        hub.CloseSession(s, "first");
        hub.CloseSession(s, "second");

        Assert.Equal(before, Gauges());
    }
}
