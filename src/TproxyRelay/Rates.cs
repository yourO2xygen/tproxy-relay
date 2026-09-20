namespace TproxyRelay;

/// <summary>
/// Process-wide token bucket (rate + burst) for admission control.
/// Refills continuously at perMinute; a take never borrows beyond the burst cap.
/// </summary>
public sealed class RateBucket(double perMinute, int burst)
{
    private readonly double _perTick = perMinute / 60000.0; // tokens per ms
    private readonly int _burst = Math.Max(1, burst);
    private double _tokens = Math.Max(1, burst);
    private long _lastTicks = DateTime.UtcNow.Ticks;

    public bool TryTake(int n = 1)
    {
        if (n > _burst)
            return false; // a request larger than the burst can never succeed
        lock (this)
        {
            var now = DateTime.UtcNow.Ticks;
            _tokens = Math.Min(_burst, _tokens + (now - _lastTicks) / TimeSpan.TicksPerMillisecond * _perTick);
            _lastTicks = now;
            if (_tokens < n)
                return false;
            _tokens -= n;
            return true;
        }
    }
}
