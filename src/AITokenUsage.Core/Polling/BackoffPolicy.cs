using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Polling;

/// <summary>
/// Decides how long to wait before the next automatic fetch of one provider.
/// </summary>
public sealed class BackoffPolicy
{
    public static readonly TimeSpan RateLimitFloor = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RateLimitCap = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan ErrorFloor = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ErrorCap = TimeSpan.FromMinutes(30);
    public const double JitterFraction = 0.10;

    // 2^16 already exceeds both caps by far; clamping the exponent keeps TimeSpan math from overflowing.
    private const int MaxExponent = 16;

    /// <param name="result">The snapshot that was just fetched.</param>
    /// <param name="baseInterval">max(configured refresh interval, provider.MinInterval).</param>
    /// <param name="consecutiveFailures">Number of consecutive RateLimited/Error results including this one (values below 1 count as 1).</param>
    public TimeSpan NextDelay(UsageSnapshot result, TimeSpan baseInterval, int consecutiveFailures)
    {
        ArgumentNullException.ThrowIfNull(result);
        int n = Math.Max(1, consecutiveFailures);

        return result.Status switch
        {
            ProviderStatus.RateLimited => Exponential(Max(result.RetryAfter ?? RateLimitFloor, RateLimitFloor), n, RateLimitCap),
            ProviderStatus.Error => Exponential(Max(baseInterval, ErrorFloor), n, ErrorCap),
            _ => baseInterval,
        };
    }

    /// <summary>Scales <paramref name="delay"/> into [0.9x, 1.1x]. unit01 = 0 → 0.9x, 0.5 → 1.0x, 1 → 1.1x.</summary>
    public static TimeSpan Jitter(TimeSpan delay, double unit01)
    {
        double u = double.IsNaN(unit01) ? 0.5 : Math.Clamp(unit01, 0.0, 1.0);
        double factor = 1.0 + ((u * 2.0) - 1.0) * JitterFraction;
        return TimeSpan.FromTicks((long)Math.Round(delay.Ticks * factor, MidpointRounding.AwayFromZero));
    }

    private static TimeSpan Exponential(TimeSpan start, int n, TimeSpan cap)
    {
        double factor = Math.Pow(2, Math.Min(n - 1, MaxExponent));
        double ticks = Math.Min(start.Ticks * factor, cap.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;
}
