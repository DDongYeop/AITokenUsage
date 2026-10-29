namespace AITokenUsage.Core.SystemUsage;

/// <summary>Turns two cumulative CPU time readings into a busy percentage.</summary>
public static class CpuUsageCalculator
{
    /// <summary>
    /// busy = (Δkernel + Δuser) − Δidle over total = Δkernel + Δuser (Windows kernel time includes idle time),
    /// clamped to 0..100. Null when no time passed or a counter went backwards.
    /// </summary>
    public static double? Percent(CpuTimes previous, CpuTimes current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        if (current.Idle < previous.Idle || current.Kernel < previous.Kernel || current.User < previous.User)
        {
            return null;
        }

        double idle = current.Idle - previous.Idle;
        double total = (double)(current.Kernel - previous.Kernel) + (current.User - previous.User);
        if (total <= 0)
        {
            return null;
        }

        return Math.Clamp((total - idle) / total * 100, 0, 100);
    }
}
