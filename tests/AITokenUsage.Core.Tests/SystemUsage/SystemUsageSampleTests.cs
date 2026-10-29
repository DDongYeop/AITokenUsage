using AITokenUsage.Core.SystemUsage;

namespace AITokenUsage.Core.Tests.SystemUsage;

public class SystemUsageSampleTests
{
    private const long Gib = 1024L * 1024 * 1024;
    private static readonly DateTimeOffset At = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MemoryPercent_IsUsedOverTotal()
    {
        var sample = new SystemUsageSample(10, 8 * Gib, 32 * Gib, At);

        Assert.Equal(25.0, sample.MemoryPercent);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void MemoryPercent_NoTotal_ReturnsNull(long total)
    {
        Assert.Null(new SystemUsageSample(10, 5, total, At).MemoryPercent);
    }

    [Fact]
    public void MemoryPercent_UsedAboveTotal_ClampsToHundred()
    {
        Assert.Equal(100.0, new SystemUsageSample(10, 40 * Gib, 32 * Gib, At).MemoryPercent);
    }
}
