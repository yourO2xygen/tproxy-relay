using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.IO.Hashing;
using System.Threading.Channels;

namespace TproxyRelay;

public sealed class StreamState(uint id)
{
    public uint Id { get; } = id;
    public TcpClient? Client;
    public NetworkStream? Net;
    public long SendAvail;            // credit for backend reads, granted by client WINDOW
    public long RecvAvail;            // remaining credit for client DATA
    public long GrantPending;         // uplink credit granted but not yet flushed as WINDOW
    public readonly SemaphoreSlim WriteLock = new(1, 1);
    public readonly SemaphoreSlim WindowSignal = new(0, int.MaxValue);
    public readonly TaskCompletionSource Connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task? Pump;
    private int _closed;
    public bool Closed => Volatile.Read(ref _closed) == 1;
    public bool MarkClosed() => Interlocked.Exchange(ref _closed, 1) == 0;
}

public sealed class Session
{
    public required string Token { get; init; }
    public required string CarrierMode { get; init; }
    public string KeyId { get; init; } = "builtin";
    public string BackendHostName { get; init; } = "";
    public int BackendPort { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public IPAddress? ClientIp;                       // accounting address of the first valid create
    public long UpBytesTotal;                         // per-session, aggregated into the key store
    public long DownBytesTotal;
    public readonly ConcurrentDictionary<uint, StreamState> Streams = new();
    public readonly ConcurrentDictionary<uint, LaneState> Lanes = new();
    public readonly Channel<FrameBuf> DownQueue;
    public readonly object Sync = new();               // seq/cursor/pending/tombstones
    public readonly CancellationTokenSource Cts = new();
    public int LastSeq;
    public ulong LastBodyHash;
    public bool LastBodyHashSet;
    public long AckedCursor;
    public long PendingCursor;
    public byte[]? PendingBatch;
    public int PendingBatchFrames;
    public long PendingBytes;                          // charged bytes incl. per-item overhead
    public int PendingItems;
    public int UpInFlight;
    public CancellationTokenSource? ActivePoll;
    public readonly SemaphoreSlim DownCollect = new(1, 1);
    private readonly HashSet<uint> _tombstones = [];
    private readonly Queue<uint> _tombstoneOrder = new();
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;
    private int _dead;
    public bool Dead => Volatile.Read(ref _dead) == 1;

    public bool LanesMode => CarrierModes.IsLanes(CarrierMode);

    public Session()
    {
        DownQueue = Channel.CreateBounded<FrameBuf>(new BoundedChannelOptions(4096)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
    public DateTime LastActivity => new(Interlocked.Read(ref _lastActivityTicks));

    public bool MarkDead() => Interlocked.Exchange(ref _dead, 1) == 0;

    public bool IsTombstoned(uint id)
    {
        lock (Sync)
            return _tombstones.Contains(id);
    }

    public void AddTombstone(uint id)
    {
        List<LaneState>? evicted = null;
        lock (Sync)
        {
            _tombstones.Add(id);
            _tombstoneOrder.Enqueue(id);
            if (_tombstoneOrder.Count > 4096)
            {
                // Evict the oldest entries instead of dropping the whole set:
                // a full reset would let a reused stream id slip past detection.
                while (_tombstoneOrder.Count > 4096)
                {
                    var old = _tombstoneOrder.Dequeue();
                    _tombstones.Remove(old);
                    if (Lanes.TryRemove(old, out var lane))
                        (evicted ??= []).Add(lane);
                }
            }
        }
        evicted?.ForEach(l => l.ReleaseAll(this));
        if (Lanes.TryGetValue(id, out var marked))
            marked.StreamClosed = true;
    }
}

public enum UpOutcome { Acked, DuplicateAcked, RetryLater, Fatal }

public sealed record UpResult(UpOutcome Outcome, int AckSeq, string? Error = null);

public sealed record DownResult(byte[]? Body, long Cursor, bool HasBatch, bool ProtocolError, bool LaneClosed = false)
{
    public static DownResult Batch(byte[] body, long cursor) => new(body, cursor, true, false);
    public static DownResult Empty(long cursor) => new(null, cursor, false, false);
    public static DownResult LaneComplete(long cursor) => new(null, cursor, false, false, true);
}

public sealed record BootstrapEntry(DateTime Expiry)
{
    public ulong BodyHash;
    public string? RedeemedToken;
    public IPAddress? Ip;
    public string KeyId = "builtin";
    public string BackendHostName = "";
    public int BackendPort;
    public string CarrierMode = "https";
}

public enum RedeemResult { Ok, RetryLater, Invalid }

public sealed partial class RelayHub
{
    /// <summary>Conservative per-item charge on top of encoded frame bytes.</summary>
    public const int ItemOverhead = 256;

    private readonly RelayOptions _opt;
    private readonly TokenMinter _minter;
    private readonly ILogger _log;
    private readonly KeyStore? _store;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, BootstrapEntry> _bootstraps = new();
    private readonly RateBucket _sessionsRate;
    private readonly RateBucket _streamsRate;
    private readonly RateBucket _bootstrapsRate;
    private int _streamsGlobal;
    private int _dialsInFlight;

    public RelayHub(RelayOptions opt, TokenMinter minter, ILogger log, KeyStore? store = null)
    {
        _opt = opt;
        _minter = minter;
        _log = log;
        _store = store;
        _sessionsRate = new RateBucket(opt.NewSessionsPerMinute, opt.NewSessionsBurst);
        _streamsRate = new RateBucket(opt.NewStreamsPerMinute, opt.NewStreamsBurst);
        _bootstrapsRate = new RateBucket(opt.NewBootstrapsPerMinute, opt.NewBootstrapsBurst);
    }

    // ---- bootstrap / session lifecycle -------------------------------------

    public string? MintBootstrap(IPAddress? ip = null, RelayProfile? profile = null)
    {
        profile ??= new RelayProfile("builtin", "builtin", _opt.Secret,
            _opt.BackendHostName, _opt.BackendPort, _opt.CarrierMode);
        if (_bootstraps.Count >= _opt.MaxBootstrapsGlobal)
        {
            Counters.LimitHit();
            return null; // cap outstanding bootstraps: unauthenticated minting must not grow memory
        }
        if (!_bootstrapsRate.TryTake())
        {
            Counters.LimitHit();
            return null;
        }
        if (_opt.MaxBootstrapsPerIp > 0 && CountBootstrapsForIp(ip) >= _opt.MaxBootstrapsPerIp)
        {
            Counters.LimitHit();
            return null;
        }
        var token = _minter.Mint(TokenMinter.KindBootstrap);
        _bootstraps[token] = new BootstrapEntry(DateTime.UtcNow.AddSeconds(_opt.BootstrapTtlSeconds))
        {
            Ip = ip,
            KeyId = profile.KeyId,
            BackendHostName = profile.BackendHostName,
            BackendPort = profile.BackendPort,
            CarrierMode = profile.CarrierMode,
        };
        Counters.BootstrapMinted();
        return token;
    }

    private int CountBootstrapsForIp(IPAddress? ip) =>
        ip == null ? 0 : _bootstraps.Values.Count(b => ip.Equals(b.Ip));

    private int CountSessionsForIp(IPAddress? ip) =>
        ip == null ? 0 : _sessions.Values.Count(s => ip.Equals(s.ClientIp));

    public RedeemResult TryRedeemBootstrap(string token, byte[] body, IPAddress? ip, out Session? session)
    {
        session = null;
        if (!_minter.TryValidate(token, TokenMinter.KindBootstrap))
            return RedeemResult.Invalid;
        if (!_bootstraps.TryGetValue(token, out var entry))
            return RedeemResult.Invalid;
        if (entry.Expiry < DateTime.UtcNow)
        {
            _bootstraps.TryRemove(token, out _);
            return RedeemResult.Invalid;
        }
        lock (entry)
        {
            if (entry.RedeemedToken != null)
            {
                // Idempotent replay of the same creation request.
                if (entry.BodyHash != Hash64(body))
                    return RedeemResult.Invalid;
                var existing = _sessions.GetValueOrDefault(entry.RedeemedToken);
                if (existing == null || existing.Dead)
                    return RedeemResult.Invalid;
                session = existing;
                return RedeemResult.Ok;
            }
            if (_sessions.Count >= _opt.MaxSessionsGlobal ||
                !_sessionsRate.TryTake() ||
                (_opt.MaxSessionsPerIp > 0 && CountSessionsForIp(ip) >= _opt.MaxSessionsPerIp))
            {
                Counters.LimitHit();
                return RedeemResult.RetryLater; // bootstrap stays unconsumed, retry is byte-identical
            }
            var created = new Session
            {
                Token = _minter.Mint(TokenMinter.KindSession),
                ClientIp = ip,
                CarrierMode = entry.CarrierMode,
                KeyId = entry.KeyId,
                BackendHostName = entry.BackendHostName,
                BackendPort = entry.BackendPort,
            };
            if (created.LanesMode)
                created.Lanes[0] = new LaneState(0); // session-level lane (PONG traffic)
            _sessions[created.Token] = created;
            Counters.SessionCreated();
            Counters.SessionActiveUp();
            entry.RedeemedToken = created.Token;
            entry.BodyHash = Hash64(body);
            session = created;
            _log.LogInformation("event=session_created");
            return RedeemResult.Ok;
        }
    }

    public Session? AuthSession(string? token)
    {
        if (token == null || token.Length != 43 || !_minter.TryValidate(token, TokenMinter.KindSession))
            return null;
        var session = _sessions.GetValueOrDefault(token);
        if (session == null || session.Dead)
            return null;
        session.Touch();
        return session;
    }

    public void CloseSession(Session session, string reason)
    {
        if (!session.MarkDead())
            return;
        AggregateTraffic(session); // final flush of the session's per-key deltas
        _sessions.TryRemove(session.Token, out _);
        Counters.SessionActiveDown();
        session.Cts.Cancel();
        session.DownQueue.Writer.TryComplete();
        foreach (var st in session.Streams.Values)
            CloseStreamInternal(session, st);
        DrainSessionCharges(session);
        foreach (var lane in session.Lanes.Values)
            lane.ReleaseAll(session);
        _log.LogInformation("event=session_closed reason={Reason}", reason);
    }

    /// <summary>
    /// REL-001: once the carriers are gone nobody drains the queues — release
    /// every outstanding charge of a dead session here, or the global downlink
    /// gate slowly absorbs its budget (guaranteed self-DoS after enough client
    /// disconnects). Exactly-once per session (guarded by MarkDead); frames are
    /// claimed atomically via TryRead, so racing carriers each release exactly
    /// their own share.
    /// </summary>
    private static void DrainSessionCharges(Session session)
    {
        while (session.DownQueue.Reader.TryRead(out var f))
        {
            ReleaseCharge(session, f.Length, 1);
            f.Return();
        }
        lock (session.Sync)
        {
            if (session.PendingBatch != null)
            {
                ReleaseCharge(session, session.PendingBatch.Length, session.PendingBatchFrames);
                session.PendingBatch = null;
            }
        }
    }

    public int BootstrapCount => _bootstraps.Count;

    private static ulong Hash64(byte[] body) => XxHash3.HashToUInt64(body);

    public Task StartReaper(CancellationToken ct) => Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
            catch (OperationCanceledException) { break; }
            var now = DateTime.UtcNow;
            // REL-011: one broken session must never kill the idle reaper —
            // silent death here would pin every session slot forever.
            foreach (var s in _sessions.Values)
            {
                try
                {
                    if ((now - s.LastActivity).TotalSeconds > _opt.ReconnectGraceSeconds)
                        CloseSession(s, "idle");
                    AggregateTraffic(s);
                }
                catch (Exception e)
                {
                    _log.LogError("event=reaper_session_failed err={Error}", e.Message);
                }
            }
            foreach (var kv in _bootstraps)
                if (kv.Value.Expiry < now)
                    _bootstraps.TryRemove(kv.Key, out _);
        }
    });

    /// <summary>Flushes per-session byte deltas into the daily per-key aggregates.</summary>
    private void AggregateTraffic(Session s)
    {
        if (_store == null)
            return;
        var up = Interlocked.Exchange(ref s.UpBytesTotal, 0);
        var down = Interlocked.Exchange(ref s.DownBytesTotal, 0);
        if (up != 0 || down != 0)
        {
            try { _store.AggregateTraffic(s.KeyId, up, down); }
            catch (Exception e)
            {
                Interlocked.Add(ref s.UpBytesTotal, up); // not lost: retry next tick
                Interlocked.Add(ref s.DownBytesTotal, down);
                _log.LogWarning("event=traffic_aggregate_failed err={Error}", e.Message);
            }
        }
    }

    public sealed record SessionSnapshot(
        string Token, string KeyId, DateTime CreatedUtc, DateTime LastActivity,
        IPAddress? ClientIp, int Streams);

    /// <summary>Sanitized live-session list for the admin surface (no tokens in output).</summary>
    public IReadOnlyList<SessionSnapshot> SessionsSnapshot() =>
        _sessions.Values.Select(s => new SessionSnapshot(
            "", s.KeyId, s.CreatedUtc, s.LastActivity, s.ClientIp, s.Streams.Count)).ToArray();

    /// <summary>Closes every live session of one key (revocation path).</summary>
    public int CloseAllSessionsForKey(string keyId, string reason)
    {
        var n = 0;
        foreach (var s in _sessions.Values.Where(v => v.KeyId == keyId).ToArray())
        {
            CloseSession(s, reason);
            n++;
        }
        return n;
    }

    // ---- uplink -------------------------------------------------------------

    public async Task<UpResult> ApplyUp(Session session, int seq, byte[] body)
    {
        if (session.Dead)
            return new UpResult(UpOutcome.Fatal, session.LastSeq, "session closed");
        if (Interlocked.CompareExchange(ref session.UpInFlight, 1, 0) != 0)
            return new UpResult(UpOutcome.RetryLater, session.LastSeq, "concurrent uplink");

        try
        {
            List<Frame> frames;
            lock (session.Sync)
            {
                if (seq == session.LastSeq)
                {
                    if (session.LastBodyHashSet && session.LastBodyHash == Hash64(body))
                        return new UpResult(UpOutcome.DuplicateAcked, seq);
                    return new UpResult(UpOutcome.Fatal, seq, "duplicate seq with different body");
                }
                if (seq != session.LastSeq + 1)
                    return new UpResult(UpOutcome.Fatal, seq, "sequence gap");
            }

            frames = FrameCodec.ParseAll(body, _opt.MaxFramePayload);
            foreach (var f in frames)
                if (!FrameCodec.IsValidClientFrame(f))
                    return new UpResult(UpOutcome.Fatal, seq, $"invalid frame type={f.Type:X2} stream={f.StreamId}");
            // Pre-validate the session budget for the WHOLE batch: a 503 must leave
            // the batch fully unapplied (byte-identical retry), never half-written.
            var dataBytes = 0L;
            foreach (var f in frames)
                if (f.Type == FrameType.Data)
                    dataBytes += f.Payload.Length;
            if (Interlocked.Read(ref session.PendingBytes) + dataBytes > _opt.MaxPendingBytesPerSession)
            {
                Counters.LimitHit();
                return new UpResult(UpOutcome.RetryLater, session.LastSeq, "data queue budget");
            }
            Counters.FramesIn(frames.Count);
            await ApplyFrames(session, frames);
            lock (session.Sync)
            {
                session.LastSeq = seq;
                session.LastBodyHash = Hash64(body);
                session.LastBodyHashSet = true;
            }
            FlushWindowGrants(session);
            Counters.UpBatch(body.Length);
            Interlocked.Add(ref session.UpBytesTotal, body.Length);
            return new UpResult(UpOutcome.Acked, seq);
        }
        catch (BudgetException)
        {
            return new UpResult(UpOutcome.RetryLater, session.LastSeq, "data queue budget");
        }
        catch (FrameException e)
        {
            return new UpResult(UpOutcome.Fatal, seq, e.Message);
        }
        catch (OperationCanceledException)
        {
            return new UpResult(UpOutcome.Fatal, session.LastSeq, "cancelled");
        }
        finally
        {
            Volatile.Write(ref session.UpInFlight, 0);
        }
    }

    private async Task ApplyFrames(Session session, List<Frame> frames)
    {
        foreach (var f in frames)
        {
            if (session.Dead)
                return;
            switch (f.Type)
            {
                case FrameType.Open:
                    await OpenStream(session, f.StreamId);
                    break;
                case FrameType.Data:
                    await WriteToBackend(session, f.StreamId, f.Payload);
                    break;
                case FrameType.Close:
                    ClientCloseStream(session, f.StreamId);
                    break;
                case FrameType.Window:
                    GrantSendCredit(session, f.StreamId, BinaryPrimitives.ReadUInt32BigEndian(f.Payload.Span));
                    break;
                case FrameType.Pong:
                    break;
                default:
                    throw new FrameException($"unexpected frame type {f.Type:X2}");
            }
        }
    }

    private async Task OpenStream(Session session, uint id)
    {
        if (id == 0)
            throw new FrameException("OPEN on stream zero");
        if (session.Streams.ContainsKey(id) || session.IsTombstoned(id))
            throw new FrameException($"stream id {id} reused");
        if (session.Streams.Count >= _opt.MaxStreamsPerSession || !_streamsRate.TryTake())
        {
            Counters.LimitHit();
            // Capacity or creation-rate rejection is stream-scoped: the session
            // and its other streams stay alive (PROTOCOL.md).
            await EnqueueDownAsync(session, FrameBuf.Buy(FrameCodec.Encode(FrameType.Close, id)));
            return;
        }
        if (Interlocked.Increment(ref _streamsGlobal) > _opt.MaxStreamsGlobal)
        {
            Interlocked.Decrement(ref _streamsGlobal);
            Counters.LimitHit();
            await EnqueueDownAsync(session, FrameBuf.Buy(FrameCodec.Encode(FrameType.Close, id)));
            return;
        }
        var st = new StreamState(id)
        {
            SendAvail = FrameCodec.InitialStreamWindow,
            RecvAvail = FrameCodec.InitialStreamWindow
        };
        if (!session.Streams.TryAdd(id, st))
        {
            Interlocked.Decrement(ref _streamsGlobal);
            throw new FrameException($"stream id {id} reused");
        }
        Counters.StreamActiveUp();
        st.Pump = Task.Run(() => DialAndPump(session, st));
        _log.LogDebug("event=stream_open id={Id}", id);
    }

    private async Task WriteToBackend(Session session, uint id, ReadOnlyMemory<byte> payload)
    {
        if (session.IsTombstoned(id))
            return; // late DATA for a closed stream: ignore
        if (!session.Streams.TryGetValue(id, out var st))
            throw new FrameException($"DATA for unknown stream {id}");
        if (st.Closed)
            return;
        if (payload.Length > Volatile.Read(ref st.RecvAvail))
            throw new FrameException($"DATA beyond receive credit on stream {id}");
        Interlocked.Add(ref st.RecvAvail, -payload.Length);

        if (session.PendingBytes + payload.Length > _opt.MaxPendingBytesPerSession)
        {
            Interlocked.Add(ref st.RecvAvail, payload.Length);
            throw new BudgetException("session pending budget exceeded");
        }

        await st.WriteLock.WaitAsync(session.Cts.Token);
        try
        {
            if (st.Closed)
                return;
            if (st.Net == null)
            {
                // A failed dial cancels the Connected task: this stream dies,
                // the session must survive (backend blips should not force
                // every client into a full re-bootstrap).
                try { await st.Connected.Task.WaitAsync(session.Cts.Token); }
                catch (OperationCanceledException) { return; }
                if (st.Net == null || st.Closed)
                    return;
            }
            try
            {
                await st.Net.WriteAsync(payload, session.Cts.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                // Backend socket broke: close only this stream, tell the client.
                CloseStreamInternal(session, st);
                await EnqueueDownAsync(session, FrameBuf.Buy(FrameCodec.Encode(FrameType.Close, id)));
                return;
            }
            Interlocked.Add(ref st.RecvAvail, payload.Length);
            // Credit back is coalesced per stream and flushed at batch boundaries,
            // instead of one tiny WINDOW frame per DATA frame.
            Interlocked.Add(ref st.GrantPending, payload.Length);
        }
        finally
        {
            st.WriteLock.Release();
        }
    }

    private void ClientCloseStream(Session session, uint id)
    {
        if (session.IsTombstoned(id))
            return;
        if (session.Streams.TryGetValue(id, out var st))
        {
            CloseStreamInternal(session, st);
            _log.LogDebug("event=stream_close id={Id} by=client", id);
        }
    }

    private void GrantSendCredit(Session session, uint id, uint amount)
    {
        if (session.IsTombstoned(id))
            return;
        if (!session.Streams.TryGetValue(id, out var st))
            throw new FrameException($"WINDOW for unknown stream {id}");
        Interlocked.Add(ref st.SendAvail, amount);
        st.WindowSignal.Release();
    }

    private async Task DialAndPump(Session session, StreamState st)
    {
        try
        {
            if (Interlocked.Increment(ref _dialsInFlight) > _opt.MaxBackendDialsInFlight)
            {
                Interlocked.Decrement(ref _dialsInFlight);
                Counters.LimitHit();
                st.Connected.TrySetCanceled(); // this stream dies, the session survives
                return;
            }
            Counters.DialInFlightSet(_dialsInFlight);
            try
            {
                using var dialCts = CancellationTokenSource.CreateLinkedTokenSource(session.Cts.Token);
                dialCts.CancelAfter(TimeSpan.FromSeconds(5));
                var tcp = new TcpClient();
                st.Client = tcp;
                var host = session.BackendHostName.Length > 0 ? session.BackendHostName : _opt.BackendHostName;
                var port = session.BackendPort > 0 ? session.BackendPort : _opt.BackendPort;
                try
                {
                    await tcp.ConnectAsync(host, port, dialCts.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or SocketException)
                {
                    try { tcp.Close(); } catch { /* ignore */ }
                    throw;
                }
                tcp.NoDelay = true; // small MTProto packets must not wait in Nagle
                st.Net = tcp.GetStream();
                st.Connected.TrySetResult();
                _log.LogDebug("event=backend_connected id={Id}", st.Id);
            }
            finally
            {
                Interlocked.Decrement(ref _dialsInFlight);
                Counters.DialInFlightSet(_dialsInFlight);
            }

            var buf = new byte[FrameCodec.DataChunk];
            while (!st.Closed && !session.Cts.IsCancellationRequested)
            {
                if (Volatile.Read(ref st.SendAvail) <= 0)
                {
                    await st.WindowSignal.WaitAsync(session.Cts.Token);
                    continue;
                }
                var want = (int)Math.Min(buf.Length, Volatile.Read(ref st.SendAvail));
                var n = await st.Net.ReadAsync(buf.AsMemory(0, want), session.Cts.Token);
                if (n == 0)
                    break; // backend EOF
                Interlocked.Add(ref st.SendAvail, -n);
                await EnqueueDownAsync(session, FrameBuf.RentData(st.Id, buf.AsSpan(0, n)));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException or ChannelClosedException)
        {
            // dial failure, cancel, or socket error: fall through to close
        }
        finally
        {
            if (!st.Connected.Task.IsCompleted)
                st.Connected.TrySetCanceled();
            if (CloseStreamInternal(session, st) &&
                !session.Dead && !session.Cts.IsCancellationRequested)
                _ = EnqueueDownAsync(session, FrameBuf.Buy(FrameCodec.Encode(FrameType.Close, st.Id)));
            _log.LogDebug("event=stream_close id={Id} by=backend", st.Id);
        }
    }

    private bool CloseStreamInternal(Session session, StreamState st)
    {
        if (!st.MarkClosed())
            return false;
        try { st.Client?.Close(); } catch { /* ignore */ }
        session.AddTombstone(st.Id);
        session.Streams.TryRemove(st.Id, out _);
        Interlocked.Decrement(ref _streamsGlobal);
        Counters.StreamActiveDown();
        return true;
    }

    // ---- downlink -----------------------------------------------------------

    private async ValueTask EnqueueDownAsync(Session session, FrameBuf frame)
    {
        if (session.Dead)
        {
            _log.LogDebug("event=frame_dropped reason=session_dead type={Type} id={Id}", frame.Span[0], (uint)((frame.Span[1] << 16) | (frame.Span[2] << 8) | frame.Span[3]));
            frame.Return();
            return;
        }
        if (session.LanesMode)
        {
            await EnqueueDownLaneAsync(session, frame);
            return;
        }
        await EnqueueDownMuxAsync(session, frame);
    }

    internal async ValueTask EnqueueDownLaneAsync(Session session, FrameBuf frame)
    {
        // Lanes carrier: every relay frame belongs to the lane of its stream id.
        var sid = (uint)((frame.Span[1] << 16) | (frame.Span[2] << 8) | frame.Span[3]);
        if (!session.Lanes.TryGetValue(sid, out var lane))
        {
            // Tombstone-evicted or never-opened lane: late frames are ignored.
            _log.LogDebug("event=frame_dropped reason=unknown_lane lane={Lane}", sid);
            frame.Return();
            return;
        }
        var charge = frame.Length + ItemOverhead;
        Interlocked.Add(ref session.PendingBytes, charge);
        Interlocked.Increment(ref session.PendingItems);
        Interlocked.Add(ref lane.QueuedCharge, charge);
        Interlocked.Increment(ref lane.QueuedItems);
        Counters.PendingBytes(charge);
        Counters.PendingItems(1);
        Counters.FramesOut(1);
        if (lane.QueuedCharge > LaneMaxCharge ||
            Interlocked.Read(ref session.PendingBytes) >
                _opt.MaxPendingBytesPerSession + _opt.DownBatchTargetBytes ||
            Counters.PendingBytesGauge > _opt.MaxPendingBytesGlobal ||
            Counters.PendingItemsGauge > _opt.MaxPendingItemsGlobal)
        {
            Counters.LimitHit();
            if (lane.QueuedCharge > LaneMaxCharge)
            {
                ReleaseLaneCharge(session, lane, frame.Length, 1);
                frame.Return();
                if (session.Streams.TryGetValue(sid, out var st))
                {
                    CloseStreamInternal(session, st);
                    _ = EnqueueDownAsync(session, FrameBuf.Buy(FrameCodec.Encode(FrameType.Close, sid)));
                }
                return;
            }
            Counters.PendingBytes(-charge);
            Counters.PendingItems(-1);
            Interlocked.Add(ref session.PendingBytes, -charge);
            Interlocked.Decrement(ref session.PendingItems);
            frame.Return();
            CloseSession(session, "downlink budget");
            return;
        }
        try { await lane.Queue.Writer.WriteAsync(frame, session.Cts.Token); }
        catch (ChannelClosedException) { ReleaseLaneCharge(session, lane, frame.Length, 1); frame.Return(); _log.LogDebug("event=frame_dropped reason=lane_queue_closed lane={Lane}", sid); }
        catch (OperationCanceledException) { ReleaseLaneCharge(session, lane, frame.Length, 1); frame.Return(); _log.LogDebug("event=frame_dropped reason=lane_session_canceled lane={Lane}", sid); }
    }

    private static void ReleaseLaneCharge(Session session, LaneState lane, int encodedBytes, int items)
    {
        var release = encodedBytes + items * ItemOverhead;
        Interlocked.Add(ref session.PendingBytes, -release);
        Interlocked.Add(ref session.PendingItems, -items);
        Interlocked.Add(ref lane.QueuedCharge, -release);
        Interlocked.Add(ref lane.QueuedItems, -items);
        Counters.PendingBytes(-release);
        Counters.PendingItems(-items);
    }

    internal async ValueTask EnqueueDownMuxAsync(Session session, FrameBuf frame)
    {
        var charge = frame.Length + ItemOverhead;
        Interlocked.Add(ref session.PendingBytes, charge);
        Interlocked.Increment(ref session.PendingItems);
        Counters.PendingBytes(charge);
        Counters.PendingItems(1);
        Counters.FramesOut(1);
        // Downlink gates: a client that grants WINDOW but never drains its down
        // queue must not grow relay memory without bound — per session and
        // process-wide (bytes and item counts).
        if (Interlocked.Read(ref session.PendingBytes) >
                _opt.MaxPendingBytesPerSession + _opt.DownBatchTargetBytes ||
            Counters.PendingBytesGauge > _opt.MaxPendingBytesGlobal ||
            Counters.PendingItemsGauge > _opt.MaxPendingItemsGlobal)
        {
            Counters.LimitHit();
            Counters.PendingBytes(-charge);
            Counters.PendingItems(-1);
            Interlocked.Add(ref session.PendingBytes, -charge);
            Interlocked.Decrement(ref session.PendingItems);
            frame.Return();
            CloseSession(session, "downlink budget");
            return;
        }
        // Bounded channel in Wait mode: when the queue is full the producer
        // (stream pump) suspends here — natural backpressure that also keeps
        // per-stream frame order. No fire-and-forget fallback: that reordered
        // frames and grew an unbounded task backlog.
        try { await session.DownQueue.Writer.WriteAsync(frame, session.Cts.Token); }
        catch (ChannelClosedException) { ReleaseCharge(session, frame.Length, 1); frame.Return(); _log.LogDebug("event=frame_dropped reason=queue_closed"); }
        catch (OperationCanceledException) { ReleaseCharge(session, frame.Length, 1); frame.Return(); _log.LogDebug("event=frame_dropped reason=session_canceled"); }
    }

    /// <summary>Releases the byte+item charge of drained frames.</summary>
    internal static void ReleaseCharge(Session session, int encodedBytes, int items)
    {
        var release = encodedBytes + items * ItemOverhead;
        Interlocked.Add(ref session.PendingBytes, -release);
        Interlocked.Add(ref session.PendingItems, -items);
        Counters.PendingBytes(-release);
        Counters.PendingItems(-items);
    }

    /// <summary>Flush coalesced WINDOW credits into the down queue.</summary>
    private void FlushWindowGrants(Session session)
    {
        if (session.Dead)
            return;
        foreach (var st in session.Streams.Values)
        {
            var granted = Interlocked.Exchange(ref st.GrantPending, 0);
            if (granted <= 0 || st.Closed)
                continue;
            _ = EnqueueDownAsync(session, FrameBuf.Buy(FrameCodec.EncodeWindow(st.Id, (uint)granted)));
        }
    }

    public async Task<DownResult> GetDown(Session session, long cursor, CancellationToken ct)
    {
        if (session.Dead)
            return new DownResult(null, cursor, false, true);

        // Replay the unacknowledged batch byte-for-byte.
        byte[]? replay;
        lock (session.Sync)
        {
            if (cursor != session.AckedCursor && cursor != session.PendingCursor)
                return new DownResult(null, cursor, false, true);
            if (cursor == session.AckedCursor && session.PendingBatch != null)
            {
                replay = session.PendingBatch;
            }
            else
            {
                replay = null;
                if (cursor == session.PendingCursor && session.PendingBatch != null)
                {
                    // Client acknowledged the pending batch: charges are held
                    // until this cursor acknowledges them (PROTOCOL.md).
                    session.AckedCursor = cursor;
                    ReleaseCharge(session, session.PendingBatch.Length, session.PendingBatchFrames);
                    session.PendingBatch = null;
                }
            }
        }
        if (replay != null)
        {
            Counters.DownBatch(replay.Length);
            return DownResult.Batch(replay, session.PendingCursor); // replay: not re-counted per key
        }

        if (!await session.DownCollect.WaitAsync(0))
            return DownResult.Empty(cursor); // superseded by a newer poll

        var myCancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var prev = Interlocked.Exchange(ref session.ActivePoll, myCancel);
        if (prev != null)
        {
            try { prev.Cancel(); } catch (ObjectDisposedException) { /* already disposed */ }
        }
        try
        {
            FlushWindowGrants(session); // ride along: credits flush even while idle-polling
            var reader = session.DownQueue.Reader;
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

            if (frames.Count == 0)
            {
                ReturnFrames(frames);
                return DownResult.Empty(cursor);
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
            lock (session.Sync)
            {
                if (session.Dead)
                {
                    // Raced CloseSession after our TryReads: never install a
                    // batch on a dead session — with no acker left its charge
                    // would leak into the global gauges (REL-001).
                    ReleaseCharge(session, body.Length, frameCount);
                    Counters.DownBatch(body.Length);
                    return DownResult.Empty(cursor);
                }
                session.PendingCursor = session.AckedCursor + 1;
                session.PendingBatch = body;
                session.PendingBatchFrames = frameCount;
                newCursor = session.PendingCursor;
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
            Interlocked.CompareExchange(ref session.ActivePoll, null, myCancel);
            session.DownCollect.Release();
            myCancel.Dispose(); // REL-013: per-poll CTS must not leak
        }
    }

    private static void ReturnFrames(List<FrameBuf> frames)
    {
        foreach (var f in frames)
            f.Return();
        frames.Clear();
    }

    public void CloseAllSessions(string reason)
    {
        foreach (var s in _sessions.Values)
            CloseSession(s, reason);
    }

    // ---- websocket carrier ----------------------------------------------------

    public async Task RunWebSocket(Session session, WebSocket ws, CancellationToken ct)
    {
        var sendTask = Task.Run(async () =>
        {
            var reader = session.DownQueue.Reader;
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
                // WS drain releases the charge immediately: no cursor replay
                // exists on this carrier, so delivery == acknowledgement.
                ReleaseCharge(session, body.Length, frameCount);
                _log.LogDebug("event=ws_send bytes={Bytes} frames={Frames}", body.Length, frameCount);
                Counters.DownBatch(body.Length);
                Interlocked.Add(ref session.DownBytesTotal, body.Length);
                await ws.SendAsync(body, WebSocketMessageType.Binary, true, ct);
                session.Touch(); // ws carrier: activity keeps the idle reaper away
                _log.LogDebug("event=ws_sent bytes={Bytes}", body.Length);
            }
        }, ct);

        var buf = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                using var ms = new System.IO.MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        goto done;
                    if (result.MessageType == WebSocketMessageType.Text)
                        goto done; // text messages are rejected
                    ms.Write(buf, 0, result.Count);
                    if (ms.Length > _opt.DownBatchTargetBytes)
                        goto done; // oversized message
                }
                while (!result.EndOfMessage);

                var frames = FrameCodec.ParseAll(ms.GetBuffer().AsMemory(0, (int)ms.Length), _opt.MaxFramePayload);
                foreach (var f in frames)
                    if (!FrameCodec.IsValidClientFrame(f))
                        goto done;
                _log.LogDebug("event=ws_recv bytes={Bytes} frames={Frames}", ms.Length, frames.Count);
                Counters.FramesIn(frames.Count);
                session.Touch(); // ws carrier: activity keeps the idle reaper away
                try
                {
                    await ApplyFrames(session, frames);
                }
                catch (BudgetException)
                {
                    // Budget is exhausted: frames of this batch are partially
                    // applied and there is no seq/retry on the ws carrier, so the
                    // only safe outcome is a clean session restart.
                    CloseSession(session, "ws data queue budget");
                    goto done;
                }
                FlushWindowGrants(session);
                Counters.UpBatch((int)ms.Length);
                Interlocked.Add(ref session.UpBytesTotal, ms.Length);
            }
        done:;
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException or FrameException)
        {
        }
        finally
        {
            CloseSession(session, "websocket carrier closed");
            try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None); }
            catch { /* ignore */ }
            try { await sendTask; } catch { /* ignore */ }
        }
    }
}
