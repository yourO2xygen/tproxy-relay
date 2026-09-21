using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace TproxyRelay;

/// <summary>
/// Per-lane carrier state for the lanes modes (https-lanes, websocket-lanes):
/// an independent ordered queue plus seq/cursor/retry machinery, mirroring the
/// session-level state of the multiplexed carriers (PROTOCOL.md).
/// </summary>
public sealed class LaneState(uint id)
{
    public readonly uint Id = id;
    public readonly Channel<FrameBuf> Queue = Channel.CreateBounded<FrameBuf>(new BoundedChannelOptions(1024)
    {
        SingleReader = false,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    public readonly object Sync = new();
    public int LastSeq;
    public ulong LastBodyHash;
    public bool LastBodyHashSet;
    public long AckedCursor;
    public long PendingCursor;
    public byte[]? PendingBatch;
    public int PendingBatchFrames;
    public int UpInFlight;
    public CancellationTokenSource? ActivePoll;
    public readonly SemaphoreSlim Collect = new(1, 1);
    public long QueuedCharge;
    public int QueuedItems;
    public volatile bool StreamClosed;
    public WebSocket? AttachedSocket;     // websocket-lanes: one socket per lane
    private int _released;

    /// <summary>
    /// Tombstone-eviction and session-close path: release everything the lane
    /// still held. Exactly-once (eviction can race CloseSession) and the
    /// PendingBatch release happens under Sync — the GetDownLane ack path
    /// clears it under the same lock (REL-018 double-release race).
    /// </summary>
    public void ReleaseAll(Session session)
    {
        if (Interlocked.Exchange(ref _released, 1) == 1)
            return;
        Queue.Writer.TryComplete();
        while (Queue.Reader.TryRead(out var f))
        {
            RelayHub.ReleaseCharge(session, f.Length, 1);
            Interlocked.Add(ref QueuedCharge, -(f.Length + RelayHub.ItemOverhead));
            Interlocked.Decrement(ref QueuedItems);
            f.Return();
        }
        lock (Sync)
        {
            if (PendingBatch != null)
            {
                RelayHub.ReleaseCharge(session, PendingBatch.Length, PendingBatchFrames);
                Interlocked.Add(ref QueuedCharge, -(PendingBatch.Length + RelayHub.ItemOverhead * PendingBatchFrames));
                Interlocked.Add(ref QueuedItems, -PendingBatchFrames);
                PendingBatch = null;
            }
        }
    }
}

public enum LaneOutcome { Ok, RetryLater, LaneClosed, Fatal }

public sealed record LaneUpResult(LaneOutcome Outcome, int AckSeq, string? Error = null);

public sealed partial class RelayHub
{
    /// <summary>Per-lane queue bound: 8 MiB of charged bytes.</summary>
    public const long LaneMaxCharge = 8 * 1024 * 1024;
    /// <summary>Per-lane queue bound: queued frame items.</summary>
    public const int LaneMaxItems = 1024;

    // ---- https-lanes uplink ---------------------------------------------------

    public async Task<LaneUpResult> ApplyUpLane(Session session, uint laneId, int seq, byte[] body)
    {
        if (session.Dead)
            return new LaneUpResult(LaneOutcome.Fatal, 0, "session closed");
        if (laneId != 0 && session.IsTombstoned(laneId))
        {
            // Late well-formed frames for an evicted lane are acknowledged and
            // ignored rather than failing the session.
            return new LaneUpResult(LaneOutcome.LaneClosed, seq);
        }
        var lane = GetOrCreateLane(session, laneId);
        if (Interlocked.CompareExchange(ref lane.UpInFlight, 1, 0) != 0)
            return new LaneUpResult(LaneOutcome.RetryLater, lane.LastSeq, "concurrent uplink");

        try
        {
            List<Frame> frames;
            lock (lane.Sync)
            {
                if (seq == lane.LastSeq)
                {
                    if (lane.LastBodyHashSet && lane.LastBodyHash == Hash64(body))
                        return new LaneUpResult(LaneOutcome.Ok, seq); // byte-identical retry
                    return new LaneUpResult(LaneOutcome.Fatal, seq, "duplicate seq with different body");
                }
                if (seq != lane.LastSeq + 1)
                    return new LaneUpResult(LaneOutcome.Fatal, seq, "sequence gap");
            }

            frames = FrameCodec.ParseAll(body, _opt.MaxFramePayload);
            if (laneId != 0 && lane.LastSeq == 0 && frames[0].Type != FrameType.Open)
                return new LaneUpResult(LaneOutcome.Fatal, seq, "lane must begin with OPEN");
            foreach (var f in frames)
            {
                if (laneId == 0)
                {
                    // Lane zero carries only session-level PONG traffic.
                    if (f.StreamId != 0 || f.Type != FrameType.Pong || f.Payload.Length > 64)
                        return new LaneUpResult(LaneOutcome.Fatal, seq, "lane zero accepts only PONG");
                    continue;
                }
                if (f.StreamId != laneId)
                    return new LaneUpResult(LaneOutcome.Fatal, seq, $"cross-lane frame {f.StreamId} on lane {laneId}");
                if (!FrameCodec.IsValidClientFrame(f))
                    return new LaneUpResult(LaneOutcome.Fatal, seq, $"invalid frame type={f.Type:X2}");
            }

            // Whole-batch budget pre-validation (503 leaves it unapplied).
            var dataBytes = 0L;
            foreach (var f in frames)
                if (f.Type == FrameType.Data)
                    dataBytes += f.Payload.Length;
            if (Interlocked.Read(ref lane.QueuedCharge) + dataBytes > LaneMaxCharge ||
                Interlocked.Read(ref session.PendingBytes) + dataBytes > _opt.MaxPendingBytesPerSession)
            {
                Counters.LimitHit();
                return new LaneUpResult(LaneOutcome.RetryLater, lane.LastSeq, "lane data queue budget");
            }

            Counters.FramesIn(frames.Count);
            await ApplyFrames(session, frames);
            lock (lane.Sync)
            {
                lane.LastSeq = seq;
                lane.LastBodyHash = Hash64(body);
                lane.LastBodyHashSet = true;
            }
            FlushWindowGrants(session);
            Counters.UpBatch(body.Length);
            Interlocked.Add(ref session.UpBytesTotal, body.Length);
            return new LaneUpResult(LaneOutcome.Ok, seq);
        }
        catch (BudgetException)
        {
            return new LaneUpResult(LaneOutcome.RetryLater, lane.LastSeq, "lane data queue budget");
        }
        catch (FrameException e)
        {
            return new LaneUpResult(LaneOutcome.Fatal, seq, e.Message);
        }
        finally
        {
            Volatile.Write(ref lane.UpInFlight, 0);
        }
    }

    private static LaneState GetOrCreateLane(Session session, uint laneId)
    {
        if (session.Lanes.TryGetValue(laneId, out var existing))
            return existing;
        // Only OPEN traffic may create a nonzero lane; lane 0 pre-exists.
        return session.Lanes.GetOrAdd(laneId, _ => new LaneState(laneId));
    }

    // ---- https-lanes downlink ---------------------------------------------------

    public async Task<DownResult> GetDownLane(Session session, uint laneId, long cursor, CancellationToken ct)
    {
        if (session.Dead)
            return new DownResult(null, cursor, false, true);
        if (!session.Lanes.TryGetValue(laneId, out var lane))
            return new DownResult(null, cursor, false, true); // unknown/evicted lane

        byte[]? replay;
        lock (lane.Sync)
        {
            if (cursor != lane.AckedCursor && cursor != lane.PendingCursor)
                return new DownResult(null, cursor, false, true);
            if (cursor == lane.AckedCursor && lane.PendingBatch != null)
            {
                replay = lane.PendingBatch;
            }
            else
            {
                replay = null;
                if (cursor == lane.PendingCursor && lane.PendingBatch != null)
                {
                    lane.AckedCursor = cursor;
                    ReleaseLaneCharge(session, lane, lane.PendingBatch.Length, lane.PendingBatchFrames);
                    lane.PendingBatch = null;
                }
            }
        }
        if (replay != null)
        {
            Counters.DownBatch(replay.Length);
            return DownResult.Batch(replay, lane.PendingCursor);
        }

        // Lane shutdown signal, checked before parking: stream closed and
        // everything delivered+acked (the CLOSE frame went out earlier).
        lock (lane.Sync)
        {
            if (laneId != 0 && lane.StreamClosed && lane.PendingBatch == null &&
                Volatile.Read(ref lane.QueuedItems) == 0)
                return DownResult.LaneComplete(cursor);
        }

        if (!await lane.Collect.WaitAsync(0))
            return DownResult.Empty(cursor); // superseded by a newer poll on this lane

        var myCancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var prev = Interlocked.Exchange(ref lane.ActivePoll, myCancel);
        if (prev != null)
        {
            try { prev.Cancel(); } catch (ObjectDisposedException) { /* already disposed */ }
        }
        try
        {
            FlushWindowGrants(session);
            var reader = lane.Queue.Reader;
            var frames = new List<FrameBuf>();
            var bytes = 0;
            var deadline = Environment.TickCount64 + _opt.LongPollSeconds * 1000L;

            while (frames.Count == 0)
            {
                if (session.Dead)
                    return new DownResult(null, cursor, false, true);
                var remain = deadline - Environment.TickCount64;
                if (remain <= 0)
                    break;
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(remain));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    timeoutCts.Token, myCancel.Token, ct, session.Cts.Token);
                bool hasData;
                try
                {
                    hasData = await reader.WaitToReadAsync(linked.Token);
                }
                catch (OperationCanceledException)
                {
                    myCancel.Token.ThrowIfCancellationRequested();
                    session.Cts.Token.ThrowIfCancellationRequested();
                    ct.ThrowIfCancellationRequested();
                    break; // long-poll timeout
                }
                if (myCancel.IsCancellationRequested)
                {
                    ReturnFrames(frames); // newer poll won
                    return DownResult.Empty(cursor);
                }
                if (!hasData)
                    break; // channel completed
                while (frames.Count < FrameCodec.MaxBatchFrames &&
                       bytes < _opt.DownBatchTargetBytes &&
                       reader.TryRead(out var f))
                {
                    frames.Add(f);
                    bytes += f.Length;
                }
            }

            // Lane shutdown signal: stream closed and everything delivered+acked.
            if (frames.Count == 0)
            {
                ReturnFrames(frames);
                lock (lane.Sync)
                {
                    var closedNow = lane.StreamClosed && lane.PendingBatch == null && lane.QueuedItems == 0;
                    return closedNow && laneId != 0
                        ? DownResult.LaneComplete(cursor)
                        : DownResult.Empty(cursor);
                }
            }

            var frameCount = frames.Count;
            var body = new byte[bytes];
            var off = 0;
            foreach (var f in frames)
            {
                f.Span.CopyTo(body.AsSpan(off));
                off += f.Length;
            }
            ReturnFrames(frames);
            long newCursor;
            lock (lane.Sync)
            {
                if (session.Dead)
                {
                    // Raced CloseSession (which releases the lane via
                    // ReleaseAll): installing the batch now would leak its
                    // charge with no acker left (REL-001).
                    ReleaseLaneCharge(session, lane, body.Length, frameCount);
                    Counters.DownBatch(body.Length);
                    return DownResult.Empty(cursor);
                }
                lane.PendingCursor = lane.AckedCursor + 1;
                lane.PendingBatch = body;
                lane.PendingBatchFrames = frameCount;
                newCursor = lane.PendingCursor;
            }
            Counters.DownBatch(body.Length);
            Interlocked.Add(ref session.DownBytesTotal, body.Length);
            return DownResult.Batch(body, newCursor);
        }
        catch (OperationCanceledException)
        {
            return DownResult.Empty(cursor); // client disconnected mid-poll
        }
        finally
        {
            Interlocked.CompareExchange(ref lane.ActivePoll, null, myCancel);
            lane.Collect.Release();
            myCancel.Dispose(); // REL-013: per-poll CTS must not leak
        }
    }

    // ---- websocket-lanes ---------------------------------------------------------

    /// <summary>Runs one per-stream WebSocket lane (subprotocol tproxy-lane-v1).</summary>
    public async Task RunWebSocketLane(Session session, uint laneId, WebSocket ws, CancellationToken ct)
    {
        if (!session.Lanes.TryGetValue(laneId, out var lane))
            return; // racing eviction
        lane.AttachedSocket = ws;
        var established = false;
        try
        {
            var sendTask = Task.Run(async () =>
            {
                var reader = lane.Queue.Reader;
                while (!ct.IsCancellationRequested && !session.Dead && ws.State == WebSocketState.Open)
                {
                    var frames = new List<FrameBuf>();
                    var bytes = 0;
                    while (frames.Count < FrameCodec.MaxBatchFrames &&
                           bytes < _opt.DownBatchTargetBytes &&
                           reader.TryRead(out var f))
                    {
                        frames.Add(f);
                        bytes += f.Length;
                    }
                    if (frames.Count == 0)
                    {
                        var hasData = await reader.WaitToReadAsync(ct);
                        if (!hasData)
                            break;
                        continue;
                    }
                    var frameCount = frames.Count;
                    var body = new byte[bytes];
                    var off = 0;
                    foreach (var f in frames)
                    {
                        f.Span.CopyTo(body.AsSpan(off));
                        off += f.Length;
                    }
                    ReturnFrames(frames);
                    ReleaseLaneCharge(session, lane, body.Length, frameCount);
                    Counters.DownBatch(body.Length);
                    Interlocked.Add(ref session.DownBytesTotal, body.Length);
                    await ws.SendAsync(body, WebSocketMessageType.Binary, true, ct);
                    session.Touch();
                    // Lane fully drained after its stream closed: the socket's
                    // job is done (the CLOSE frame has been delivered).
                    if (lane.StreamClosed && Volatile.Read(ref lane.QueuedItems) == 0)
                    {
                        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "lane closed", CancellationToken.None); }
                        catch { /* ignore */ }
                        return;
                    }
                }
            }, ct);

            var buf = new byte[64 * 1024];
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (result.MessageType != WebSocketMessageType.Binary)
                        goto laneFail; // text messages close the lane only
                    ms.Write(buf, 0, result.Count);
                    if (ms.Length > _opt.DownBatchTargetBytes)
                        goto laneFail; // oversized message
                }
                while (!result.EndOfMessage);

                List<Frame> frames;
                try
                {
                    frames = FrameCodec.ParseAll(ms.GetBuffer().AsMemory(0, (int)ms.Length), _opt.MaxFramePayload);
                }
                catch (FrameException)
                {
                    goto laneFail; // malformed batch closes the lane only
                }
                foreach (var f in frames)
                {
                    if (f.StreamId != laneId || !FrameCodec.IsValidClientFrame(f))
                        goto laneFail; // cross-lane or invalid frame
                    if (!established && f.Type != FrameType.Open)
                        goto laneFail; // first message must begin with OPEN
                }
                if (!established)
                {
                    if (frames.Count != 1)
                        goto laneFail;
                    established = true;
                }
                Counters.FramesIn(frames.Count);
                session.Touch();
                try
                {
                    await ApplyFrames(session, frames);
                }
                catch (BudgetException)
                {
                    goto laneFail; // lane-scoped budget: close this stream only
                }
                FlushWindowGrants(session);
                Counters.UpBatch((int)ms.Length);
                Interlocked.Add(ref session.UpBytesTotal, ms.Length);
                continue;

            laneFail:
                {
                    await CloseLaneAndSocket(session, laneId, ws, sendTask);
                    return;
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException or FrameException)
        {
        }
        finally
        {
            lane.AttachedSocket = null;
            // Unexpected socket loss closes only this backend stream.
            if (session.Streams.TryGetValue(laneId, out var st) && !st.Closed)
                CloseStreamInternal(session, st);
        }
    }

    private async Task CloseLaneAndSocket(Session session, uint laneId, WebSocket ws, Task sendTask)
    {
        if (session.Streams.TryGetValue(laneId, out var st) && !st.Closed)
        {
            CloseStreamInternal(session, st);
            // Deliver the CLOSE frame before the socket goes away.
            _ = EnqueueDownAsync(session, FrameBuf.Buy(FrameCodec.Encode(FrameType.Close, laneId)));
            var laneNow = session.Lanes.GetValueOrDefault(laneId);
            for (var i = 0; laneNow != null && i < 40 && Volatile.Read(ref laneNow.QueuedItems) > 0; i++)
                await Task.Delay(50); // best-effort flush (<=2s) before close
        }
        try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "lane closed", CancellationToken.None); }
        catch { /* ignore */ }
        try { await sendTask; } catch { /* ignore */ }
    }
}
