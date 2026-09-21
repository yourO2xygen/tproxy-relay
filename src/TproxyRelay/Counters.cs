namespace TproxyRelay;

/// <summary>Point-in-time copy of all counters (ARCH-003: management code
/// reads this instead of parsing its own Prometheus text).</summary>
public sealed record StatsSnapshot(
    long SessionsCreated, long SessionsActive, long StreamsActive, long BootstrapsMinted,
    long UpBatches, long DownBatches, long UpBytes, long DownBytes,
    long FramesIn, long FramesOut, long LimitHits, long PendingBytes, long PendingItems,
    long DialsInFlight);

public static class Counters
{
    private static long _sessionsCreated, _sessionsActive, _streamsActive, _bootstrapsMinted;
    private static long _upBatches, _downBatches, _upBytes, _downBytes, _framesIn, _framesOut;
    private static long _limitHits, _pendingBytes, _pendingItems, _dialsInFlight;

    public static long PendingBytesGauge => Volatile.Read(ref _pendingBytes);
    public static long PendingItemsGauge => Volatile.Read(ref _pendingItems);

    public static StatsSnapshot Snapshot() => new(
        Volatile.Read(ref _sessionsCreated), Volatile.Read(ref _sessionsActive),
        Volatile.Read(ref _streamsActive), Volatile.Read(ref _bootstrapsMinted),
        Volatile.Read(ref _upBatches), Volatile.Read(ref _downBatches),
        Volatile.Read(ref _upBytes), Volatile.Read(ref _downBytes),
        Volatile.Read(ref _framesIn), Volatile.Read(ref _framesOut),
        Volatile.Read(ref _limitHits), Volatile.Read(ref _pendingBytes),
        Volatile.Read(ref _pendingItems), Volatile.Read(ref _dialsInFlight));

    public static void SessionCreated() => Interlocked.Increment(ref _sessionsCreated);
    public static void SessionActiveUp() => Interlocked.Increment(ref _sessionsActive);
    public static void SessionActiveDown() => Interlocked.Decrement(ref _sessionsActive);
    public static void StreamActiveUp() => Interlocked.Increment(ref _streamsActive);
    public static void StreamActiveDown() => Interlocked.Decrement(ref _streamsActive);
    public static void BootstrapMinted() => Interlocked.Increment(ref _bootstrapsMinted);
    public static void UpBatch(long bytes) { Interlocked.Increment(ref _upBatches); Interlocked.Add(ref _upBytes, bytes); }
    public static void DownBatch(long bytes) { Interlocked.Increment(ref _downBatches); Interlocked.Add(ref _downBytes, bytes); }
    public static void FramesIn(int n) => Interlocked.Add(ref _framesIn, n);
    public static void FramesOut(int n) => Interlocked.Add(ref _framesOut, n);
    public static void LimitHit() => Interlocked.Increment(ref _limitHits);
    public static void PendingBytes(long delta) => Interlocked.Add(ref _pendingBytes, delta);
    public static void PendingItems(long delta) => Interlocked.Add(ref _pendingItems, delta);
    public static void DialInFlightSet(long value) => Interlocked.Exchange(ref _dialsInFlight, value);

    public static string Render() =>
        $"""
         # TYPE tproxy_sessions_created counter
         tproxy_sessions_created {Volatile.Read(ref _sessionsCreated)}
         # TYPE tproxy_sessions_active gauge
         tproxy_sessions_active {Volatile.Read(ref _sessionsActive)}
         # TYPE tproxy_streams_active gauge
         tproxy_streams_active {Volatile.Read(ref _streamsActive)}
         # TYPE tproxy_bootstraps_minted counter
         tproxy_bootstraps_minted {Volatile.Read(ref _bootstrapsMinted)}
         # TYPE tproxy_up_batches counter
         tproxy_up_batches {Volatile.Read(ref _upBatches)}
         # TYPE tproxy_down_batches counter
         tproxy_down_batches {Volatile.Read(ref _downBatches)}
         # TYPE tproxy_up_bytes_total counter
         tproxy_up_bytes_total {Volatile.Read(ref _upBytes)}
         # TYPE tproxy_down_bytes_total counter
         tproxy_down_bytes_total {Volatile.Read(ref _downBytes)}
         # TYPE tproxy_frames_in_total counter
         tproxy_frames_in_total {Volatile.Read(ref _framesIn)}
         # TYPE tproxy_frames_out_total counter
         tproxy_frames_out_total {Volatile.Read(ref _framesOut)}
         # TYPE tproxy_limit_hits_total counter
         tproxy_limit_hits_total {Volatile.Read(ref _limitHits)}
         # TYPE tproxy_pending_bytes gauge
         tproxy_pending_bytes {Volatile.Read(ref _pendingBytes)}
         # TYPE tproxy_pending_items gauge
         tproxy_pending_items {Volatile.Read(ref _pendingItems)}
         # TYPE tproxy_backend_dials_in_flight gauge
         tproxy_backend_dials_in_flight {Volatile.Read(ref _dialsInFlight)}
         """;
}
