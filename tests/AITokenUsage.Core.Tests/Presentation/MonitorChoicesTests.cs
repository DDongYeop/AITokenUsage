using AITokenUsage.Core.Presentation;

namespace AITokenUsage.Core.Tests.Presentation;

public class MonitorChoicesTests
{
    private static readonly MonitorDescriptor Primary = new(@"\\.\DISPLAY1", 0, 0, 1920, 1080, true);

    [Fact]
    public void TwoMonitors_PrimaryEntryFirstThenLeftToRight()
    {
        var left = new MonitorDescriptor(@"\\.\DISPLAY2", -1920, 0, 1920, 1080, false);

        IReadOnlyList<MonitorChoice> choices = MonitorChoices.Build([Primary, left], null);

        Assert.Equal(3, choices.Count);
        Assert.Equal(new MonitorChoice(null, "주 모니터"), choices[0]);
        Assert.Equal(new MonitorChoice(@"\\.\DISPLAY2", "모니터 1 (1920×1080, 왼쪽)"), choices[1]);
        Assert.Equal(new MonitorChoice(@"\\.\DISPLAY1", "모니터 2 (1920×1080, 주)"), choices[2]);
    }

    [Fact]
    public void DescribesRightAboveAndBelowRelativeToPrimary()
    {
        var right = new MonitorDescriptor(@"\\.\DISPLAY2", 1920, 0, 2560, 1440, false);
        var above = new MonitorDescriptor(@"\\.\DISPLAY3", 0, -1080, 1920, 1080, false);
        var below = new MonitorDescriptor(@"\\.\DISPLAY4", 0, 1080, 1920, 1080, false);

        string[] labels = MonitorChoices.Build([Primary, right, above, below], null).Select(c => c.Label).ToArray();

        Assert.Equal(
            new[]
            {
                "주 모니터",
                "모니터 1 (1920×1080, 위)",
                "모니터 2 (1920×1080, 주)",
                "모니터 3 (1920×1080, 아래)",
                "모니터 4 (2560×1440, 오른쪽)",
            },
            labels);
    }

    [Fact]
    public void DisconnectedSelection_IsKeptAtTheEnd()
    {
        IReadOnlyList<MonitorChoice> choices = MonitorChoices.Build([Primary], @"\\.\DISPLAY3");

        Assert.Equal(3, choices.Count);
        Assert.Equal(new MonitorChoice(@"\\.\DISPLAY3", @"\\.\DISPLAY3 (연결 안 됨)"), choices[^1]);
    }

    [Fact]
    public void IndexOf_MatchesNullAndDeviceNamesIgnoringCase()
    {
        IReadOnlyList<MonitorChoice> choices = MonitorChoices.Build([Primary], null);

        Assert.Equal(0, MonitorChoices.IndexOf(choices, null));
        Assert.Equal(1, MonitorChoices.IndexOf(choices, @"\\.\display1"));
        Assert.Equal(0, MonitorChoices.IndexOf(choices, @"\\.\DISPLAY9"));
    }
}
