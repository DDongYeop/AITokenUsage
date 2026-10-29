namespace AITokenUsage.Core.SystemUsage;

/// <summary>Cumulative CPU times of the whole machine (100 ns ticks, as GetSystemTimes reports them). Kernel time includes idle time.</summary>
public sealed record CpuTimes(ulong Idle, ulong Kernel, ulong User);

/// <summary>One PC measurement. CpuPercent is null until two CPU readings exist (or when they cannot be compared).</summary>
public sealed record SystemUsageSample(double? CpuPercent, long MemoryUsedBytes, long MemoryTotalBytes, DateTimeOffset At)
{
    /// <summary>Used / total x 100 clamped to 0..100. Null when the total is unknown (zero or less).</summary>
    public double? MemoryPercent => MemoryTotalBytes <= 0
        ? null
        : Math.Clamp((double)MemoryUsedBytes / MemoryTotalBytes * 100, 0, 100);
}
