using AITokenUsage.Core.Taskbar;
using Xunit;

namespace AITokenUsage.Core.Tests.Taskbar;

public class TaskbarButtonGroupingTests
{
    private const int Gap96 = TaskbarButtonGrouping.MaxGapAt96Dpi;

    private static PixelRect Button(int left, int width = 44) => new(left, 1032, left + width, 1080);

    [Fact]
    public void ContiguousButtons_InAnyOrder_AreUnionedFromStartRight()
    {
        PixelRect[] buttons = [Button(851), Button(763), Button(895), Button(807)];

        Assert.Equal(new PixelRect(763, 1032, 939, 1080), TaskbarButtonGrouping.GroupContiguous(buttons, 763, Gap96));
    }

    [Fact]
    public void IsolatedOverflowButtonFarRight_IsExcluded()
    {
        PixelRect[] buttons = [Button(763), Button(807), Button(851), Button(1580)];

        Assert.Equal(new PixelRect(763, 1032, 895, 1080), TaskbarButtonGrouping.GroupContiguous(buttons, 763, Gap96));
    }

    [Fact]
    public void GapOfMaxGap_Joins_LargerGap_Stops()
    {
        // 763..807, gap 16 → 823..867 joins; gap 17 → 884..928 does not.
        PixelRect[] buttons = [Button(763), Button(823), Button(884)];

        Assert.Equal(new PixelRect(763, 1032, 867, 1080), TaskbarButtonGrouping.GroupContiguous(buttons, 763, Gap96));
    }

    [Fact]
    public void ButtonsLeftOfStartRight_AreSkipped()
    {
        PixelRect[] buttons = [Button(700), Button(763), Button(807)];

        Assert.Equal(new PixelRect(763, 1032, 851, 1080), TaskbarButtonGrouping.GroupContiguous(buttons, 763, Gap96));
    }

    [Fact]
    public void NullStartRight_StartsFromTheLeftmostButton()
    {
        PixelRect[] buttons = [Button(101), Button(900), Button(57)];

        Assert.Equal(new PixelRect(57, 1032, 145, 1080), TaskbarButtonGrouping.GroupContiguous(buttons, null, Gap96));
    }

    [Fact]
    public void NoButtonAtOrAfterStartRight_ReturnsNull()
    {
        Assert.Null(TaskbarButtonGrouping.GroupContiguous([Button(57), Button(101)], 763, Gap96));
    }

    [Fact]
    public void EmptyList_ReturnsNull()
    {
        Assert.Null(TaskbarButtonGrouping.GroupContiguous([], 763, Gap96));
        Assert.Null(TaskbarButtonGrouping.GroupContiguous([], null, Gap96));
    }
}
