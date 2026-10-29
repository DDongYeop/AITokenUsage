using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.Taskbar;

namespace AITokenUsage.Core.Tests.Presentation;

public class FlyoutHoverTests
{
    // Widget at (8,1036)-(218,1076); the card window (shadow border included) ends 4 px inside the widget's top edge.
    private static readonly PixelRect Widget = new(8, 1036, 218, 1076);
    private static readonly PixelRect Card = new(-12, 790, 362, 1040);

    [Fact]
    public void CursorOnTheWidget_IsOverAnchor()
    {
        FlyoutHoverState state = FlyoutHover.Locate(new PixelPoint(105, 1056), Widget, Card);

        Assert.True(state.OverAnchor);
        Assert.False(state.OverCard);
        Assert.True(state.IsOver);
    }

    [Fact]
    public void CursorOnTheCard_IsOverCard()
    {
        FlyoutHoverState state = FlyoutHover.Locate(new PixelPoint(100, 900), Widget, Card);

        Assert.False(state.OverAnchor);
        Assert.True(state.OverCard);
        Assert.True(state.IsOver);
    }

    [Fact]
    public void CursorWhereCardAndWidgetOverlap_IsOverBoth()
    {
        FlyoutHoverState state = FlyoutHover.Locate(new PixelPoint(100, 1037), Widget, Card);

        Assert.True(state.OverAnchor);
        Assert.True(state.OverCard);
    }

    [Theory]
    [InlineData(500, 1056)] // further along the taskbar
    [InlineData(100, 1076)] // just below the widget (bottom edge is exclusive)
    [InlineData(218, 1056)] // just right of the widget (right edge is exclusive)
    [InlineData(100, 789)] // just above the card
    [InlineData(-1691, 434)] // another monitor
    public void CursorOutsideBoth_IsNotOver(int x, int y)
    {
        FlyoutHoverState state = FlyoutHover.Locate(new PixelPoint(x, y), Widget, Card);

        Assert.False(state.IsOver);
    }

    [Fact]
    public void MissingOrEmptyWindows_NeverContainTheCursor()
    {
        var cursor = new PixelPoint(100, 1056);

        Assert.False(FlyoutHover.Locate(cursor, null, null).IsOver);
        Assert.False(FlyoutHover.Locate(cursor, new PixelRect(8, 1036, 8, 1076), null).IsOver);
        Assert.True(FlyoutHover.Locate(new PixelPoint(100, 900), null, Card).OverCard);
    }
}
