using AITokenUsage.Core.Taskbar;

namespace AITokenUsage.Core.Tests.Taskbar;

public class WidgetMetricsTests
{
    [Fact]
    public void Width_depends_on_reset_column()
    {
        Assert.Equal(176, WidgetMetrics.WidthDip(showResetTime: false));
        Assert.Equal(218, WidgetMetrics.WidthDip(showResetTime: true));
    }

    [Theory]
    [InlineData(false, false, 176)]
    [InlineData(true, false, 218)]
    [InlineData(false, true, 240)]
    [InlineData(true, true, 282)]
    public void Width_combines_reset_column_and_system_column(bool showResetTime, bool systemBeside, double expected)
    {
        Assert.Equal(expected, WidgetMetrics.WidthDip(showResetTime, systemBeside));
    }

    [Fact]
    public void System_column_constants()
    {
        Assert.Equal(8, WidgetMetrics.SystemGapDip);
        Assert.Equal(56, WidgetMetrics.SystemColumnDip);
    }
}
