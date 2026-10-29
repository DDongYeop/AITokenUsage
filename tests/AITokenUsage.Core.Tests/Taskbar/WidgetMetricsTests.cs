using AITokenUsage.Core.Taskbar;

namespace AITokenUsage.Core.Tests.Taskbar;

public class WidgetMetricsTests
{
    [Fact]
    public void Width_depends_on_reset_column()
    {
        Assert.Equal(176, WidgetMetrics.WidthDip(showResetTime: false));
        Assert.Equal(210, WidgetMetrics.WidthDip(showResetTime: true));
    }
}
