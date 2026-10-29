using AITokenUsage.Core.SystemUsage;

namespace AITokenUsage.Core.Tests.SystemUsage;

public class CpuUsageCalculatorTests
{
    [Fact]
    public void Percent_NormalDelta_ExcludesIdleFromKernelTime()
    {
        // busy = (100 + 100) - 50 = 150, total = 100 + 100 = 200
        double? percent = CpuUsageCalculator.Percent(new CpuTimes(100, 300, 100), new CpuTimes(150, 400, 200));

        Assert.Equal(75.0, percent);
    }

    [Fact]
    public void Percent_ZeroTotalDelta_ReturnsNull()
    {
        var times = new CpuTimes(100, 300, 100);

        Assert.Null(CpuUsageCalculator.Percent(times, times));
    }

    [Theory]
    [InlineData(100UL, 300UL, 100UL, 90UL, 400UL, 200UL)] // idle went backwards
    [InlineData(100UL, 300UL, 100UL, 150UL, 290UL, 200UL)] // kernel went backwards
    [InlineData(100UL, 300UL, 100UL, 150UL, 400UL, 90UL)] // user went backwards
    public void Percent_CounterWentBackwards_ReturnsNull(ulong i0, ulong k0, ulong u0, ulong i1, ulong k1, ulong u1)
    {
        Assert.Null(CpuUsageCalculator.Percent(new CpuTimes(i0, k0, u0), new CpuTimes(i1, k1, u1)));
    }

    [Fact]
    public void Percent_IdleDeltaLargerThanTotal_ClampsToZero()
    {
        // idle +300 but kernel+user only +200: busy would be negative.
        double? percent = CpuUsageCalculator.Percent(new CpuTimes(0, 0, 0), new CpuTimes(300, 100, 100));

        Assert.Equal(0.0, percent);
    }

    [Fact]
    public void Percent_FullyBusy_IsOneHundred()
    {
        // busy = (100 + 100) - 0 = 200 against total 200.
        Assert.Equal(100.0, CpuUsageCalculator.Percent(new CpuTimes(100, 100, 0), new CpuTimes(100, 200, 100)));
    }
}
