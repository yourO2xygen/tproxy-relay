namespace TproxyRelay;

public static class Counters
{
    private static long _sessionsCreated, _sessionsActive, _streamsActive, _bootstrapsMinted;
    private static long _upBatches, _downBatches, _upBytes, _downBytes, _framesIn, _framesOut;

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
         """;
}
