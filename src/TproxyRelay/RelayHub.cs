using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace TproxyRelay;

public sealed class StreamState(uint id)
{
    public uint Id { get; } = id;
    public TcpClient? Client;
    public NetworkStream? Net;
    public long SendAvail;            // credit for backend reads, granted by client WINDOW
    public long RecvAvail;            // remaining credit for client DATA
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
    public readonly ConcurrentDictionary<uint, StreamState> Streams = new();
    public readonly Channel<byte[]> DownQueue;
    public readonly object Sync = new();               // seq/cursor/pending/tombstones
    public readonly CancellationTokenSource Cts = new();
    public int LastSeq;
    public byte[]? LastBodyHash;
    public long AckedCursor;
    public long PendingCursor;
    public byte[]? PendingBatch;
    public long PendingBytes;
    public int UpInFlight;
    public CancellationTokenSource? ActivePoll;
    public readonly SemaphoreSlim DownCollect = new(1, 1);
    private readonly HashSet<uint> _tombstones = [];
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;
    private int _dead;
    public bool Dead => Volatile.Read(ref _dead) == 1;

    public Session()
    {
        DownQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4096)
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
        lock (Sync)
        {
            _tombstones.Add(id);
            if (_tombstones.Count > 4096)
                _tombstones.Clear();
        }
    }
}

public enum UpOutcome { Acked, DuplicateAcked, RetryLater, Fatal }

public sealed record UpResult(UpOutcome Outcome, int AckSeq, string? Error = null);

public sealed record DownResult(byte[]? Body, long Cursor, bool HasBatch, bool ProtocolError)
{
    public static DownResult Batch(byte[] body, long cursor) => new(body, cursor, true, false);
    public static DownResult Empty(long cursor) => new(null, cursor, false, false);
}

public sealed record BootstrapEntry(DateTime Expiry)
{
    public byte[]? BodyHash;
    public string? RedeemedToken;
}

public enum RedeemResult { Ok, RetryLater, Invalid }

public sealed class RelayHub
{
    private readonly RelayOptions _opt;
    private readonly TokenMinter _minter;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, BootstrapEntry> _bootstraps = new();

    public RelayHub(RelayOptions opt, TokenMinter minter, ILogger log)
    {
        _opt = opt;
        _minter = minter;
        _log = log;
    }

    // ---- bootstrap / session lifecycle -------------------------------------

    public string MintBootstrap()
    {
        var token = _minter.Mint(TokenMinter.KindBootstrap);
        _bootstraps[token] = new BootstrapEntry(DateTime.UtcNow.AddSeconds(_opt.BootstrapTtlSeconds));
        Counters.BootstrapMinted();
        return token;
    }

    public RedeemResult TryRedeemBootstrap(string token, byte[] body, out Session? session)
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
                if (entry.BodyHash == null || !entry.BodyHash.AsSpan().SequenceEqual(SHA256.HashData(body)))
                    return RedeemResult.Invalid;
                var existing = _sessions.GetValueOrDefault(entry.RedeemedToken);
                if (existing == null || existing.Dead)
                    return RedeemResult.Invalid;
                session = existing;
                return RedeemResult.Ok;
            }
            if (_sessions.Count >= _opt.MaxSessionsGlobal)
                return RedeemResult.RetryLater;
            var created = new Session { Token = _minter.Mint(TokenMinter.KindSession) };
            _sessions[created.Token] = created;
            Counters.SessionCreated();
            Counters.SessionActiveUp();
            entry.RedeemedToken = created.Token;
            entry.BodyHash = SHA256.HashData(body);
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
        _sessions.TryRemove(session.Token, out _);
        Counters.SessionActiveDown();
        session.Cts.Cancel();
        session.DownQueue.Writer.TryComplete();
        foreach (var st in session.Streams.Values)
            CloseStreamInternal(session, st, enqueueClose: false);
        _log.LogInformation("event=session_closed reason={Reason}", reason);
    }

    public int BootstrapCount => _bootstraps.Count;

    public Task StartReaper(CancellationToken ct) => Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
            catch (OperationCanceledException) { break; }
            var now = DateTime.UtcNow;
            foreach (var s in _sessions.Values)
                if ((now - s.LastActivity).TotalSeconds > _opt.SessionIdleTtlSeconds)
                    CloseSession(s, "idle");
            foreach (var kv in _bootstraps)
                if (kv.Value.Expiry < now)
                    _bootstraps.TryRemove(kv.Key, out _);
        }
    });

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
                    var hash = SHA256.HashData(body);
                    if (session.LastBodyHash != null && hash.AsSpan().SequenceEqual(session.LastBodyHash))
                        return new UpResult(UpOutcome.DuplicateAcked, seq);
                    return new UpResult(UpOutcome.Fatal, seq, "duplicate seq with different body");
                }
                if (seq != session.LastSeq + 1)
                    return new UpResult(UpOutcome.Fatal, seq, "sequence gap");
            }

            frames = FrameCodec.ParseAll(body);
            foreach (var f in frames)
                if (!FrameCodec.IsValidClientFrame(f))
                    return new UpResult(UpOutcome.Fatal, seq, $"invalid frame type={f.Type:X2} stream={f.StreamId}");
            Counters.FramesIn(frames.Count);
            await ApplyFrames(session, frames);
            lock (session.Sync)
            {
                session.LastSeq = seq;
                session.LastBodyHash = SHA256.HashData(body);
            }
            Counters.UpBatch(body.Length);
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
                    OpenStream(session, f.StreamId);
                    break;
                case FrameType.Data:
                    await WriteToBackend(session, f.StreamId, f.Payload);
                    break;
                case FrameType.Close:
                    ClientCloseStream(session, f.StreamId);
                    break;
                case FrameType.Window:
                    GrantSendCredit(session, f.StreamId, BinaryPrimitives.ReadUInt32BigEndian(f.Payload));
                    break;
                case FrameType.Pong:
                    break;
                default:
                    throw new FrameException($"unexpected frame type {f.Type:X2}");
            }
        }
    }

    private void OpenStream(Session session, uint id)
    {
        if (id == 0)
            throw new FrameException("OPEN on stream zero");
        if (session.Streams.ContainsKey(id) || session.IsTombstoned(id))
            throw new FrameException($"stream id {id} reused");
        if (session.Streams.Count >= _opt.MaxStreamsPerSession)
        {
            EnqueueDown(session, FrameCodec.Encode(FrameType.Close, id));
            return;
        }
        var st = new StreamState(id)
        {
            SendAvail = FrameCodec.InitialStreamWindow,
            RecvAvail = FrameCodec.InitialStreamWindow
        };
        if (!session.Streams.TryAdd(id, st))
            throw new FrameException($"stream id {id} reused");
        Counters.StreamActiveUp();
        st.Pump = Task.Run(() => DialAndPump(session, st));
        _log.LogDebug("event=stream_open id={Id}", id);
    }

    private async Task WriteToBackend(Session session, uint id, byte[] payload)
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
                await st.Connected.Task.WaitAsync(session.Cts.Token);
                if (st.Net == null || st.Closed)
                    return;
            }
            await st.Net.WriteAsync(payload, session.Cts.Token);
            Interlocked.Add(ref st.RecvAvail, payload.Length);
            // Grant credit back once bytes drained to the backend socket.
            EnqueueDown(session, FrameCodec.EncodeWindow(id, (uint)payload.Length));
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
            CloseStreamInternal(session, st, enqueueClose: false);
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
            using var dialCts = CancellationTokenSource.CreateLinkedTokenSource(session.Cts.Token);
            dialCts.CancelAfter(TimeSpan.FromSeconds(5));
            var tcp = new TcpClient();
            st.Client = tcp;
            await tcp.ConnectAsync(_opt.BackendHostName, _opt.BackendPort, dialCts.Token);
            st.Net = tcp.GetStream();
            st.Connected.TrySetResult();
            _log.LogDebug("event=backend_connected id={Id}", st.Id);

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
                EnqueueDown(session, FrameCodec.Encode(FrameType.Data, st.Id, buf.AsSpan(0, n)));
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
            CloseStreamInternal(session, st, enqueueClose: true);
            _log.LogDebug("event=stream_close id={Id} by=backend", st.Id);
        }
    }

    private void CloseStreamInternal(Session session, StreamState st, bool enqueueClose)
    {
        if (!st.MarkClosed())
            return;
        try { st.Client?.Close(); } catch { /* ignore */ }
        session.AddTombstone(st.Id);
        session.Streams.TryRemove(st.Id, out _);
        Counters.StreamActiveDown();
        if (enqueueClose && !session.Dead && !session.Cts.IsCancellationRequested)
            EnqueueDown(session, FrameCodec.Encode(FrameType.Close, st.Id));
    }

    // ---- downlink -----------------------------------------------------------

    private void EnqueueDown(Session session, byte[] frame)
    {
        Interlocked.Add(ref session.PendingBytes, frame.Length);
        Counters.FramesOut(1);
        if (!session.DownQueue.Writer.TryWrite(frame))
        {
            // Bounded queue is momentarily full; retry without blocking the caller.
            _ = Task.Run(async () =>
            {
                try { await session.DownQueue.Writer.WriteAsync(frame); }
                catch (ChannelClosedException) { /* session is gone */ }
            });
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
                    // Client acknowledged the pending batch.
                    session.AckedCursor = cursor;
                    Interlocked.Add(ref session.PendingBytes, -session.PendingBatch.Length);
                    session.PendingBatch = null;
                }
            }
        }
        if (replay != null)
        {
            Counters.DownBatch(replay.Length);
            return DownResult.Batch(replay, session.PendingCursor);
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
            var reader = session.DownQueue.Reader;
            var frames = new List<byte[]>();
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
                    return DownResult.Empty(cursor); // newer poll won
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
                return DownResult.Empty(cursor);

            var body = new byte[bytes];
            var off = 0;
            foreach (var f in frames)
            {
                f.CopyTo(body, off);
                off += f.Length;
            }
            long newCursor;
            lock (session.Sync)
            {
                session.PendingCursor = session.AckedCursor + 1;
                session.PendingBatch = body;
                newCursor = session.PendingCursor;
            }
            Counters.DownBatch(body.Length);
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
        }
    }

    // ---- websocket carrier ----------------------------------------------------

    public async Task RunWebSocket(Session session, WebSocket ws, CancellationToken ct)
    {
        var sendTask = Task.Run(async () =>
        {
            var reader = session.DownQueue.Reader;
            while (!ct.IsCancellationRequested && !session.Dead && ws.State == WebSocketState.Open)
            {
                var frames = new List<byte[]>();
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
                var body = new byte[bytes];
                var off = 0;
                foreach (var f in frames)
                {
                    f.CopyTo(body, off);
                    off += f.Length;
                }
                _log.LogDebug("event=ws_send bytes={Bytes} frames={Frames}", body.Length, frames.Count);
                Counters.DownBatch(body.Length);
                await ws.SendAsync(body, WebSocketMessageType.Binary, true, ct);
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

                var frames = FrameCodec.ParseAll(ms.GetBuffer().AsSpan(0, (int)ms.Length));
                foreach (var f in frames)
                    if (!FrameCodec.IsValidClientFrame(f))
                        goto done;
                _log.LogDebug("event=ws_recv bytes={Bytes} frames={Frames}", ms.Length, frames.Count);
                Counters.FramesIn(frames.Count);
                await ApplyFrames(session, frames);
                Counters.UpBatch((int)ms.Length);
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
