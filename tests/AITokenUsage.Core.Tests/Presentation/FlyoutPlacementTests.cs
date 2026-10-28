using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.Taskbar;

namespace AITokenUsage.Core.Tests.Presentation;

public class FlyoutPlacementTests
{
    private static readonly PixelRect PrimaryWork = new(0, 0, 1920, 1040);

    [Fact]
    public void CentresAboveTheAnchor_WithVisibleCardGapAboveTheWidget()
    {
        var (x, y) = FlyoutPlacement.Compute(new PixelRect(100, 1040, 276, 1080), PrimaryWork, 344, 224, 12, 8);

        Assert.Equal(16, x);
        Assert.Equal(820, y);
        Assert.Equal(1040 - 8, y + 224 - 12);
    }

    [Fact]
    public void ClampsAtTheLeftEdge_AllowingOnlyTheTransparentMargin()
    {
        var (x, y) = FlyoutPlacement.Compute(new PixelRect(0, 1040, 176, 1080), PrimaryWork, 344, 224, 12, 8);

        Assert.Equal(-12, x);
        Assert.Equal(820, y);
    }

    [Fact]
    public void ClampsAtTheRightEdge()
    {
        var (x, _) = FlyoutPlacement.Compute(new PixelRect(1744, 1040, 1920, 1080), PrimaryWork, 344, 224, 12, 8);

        Assert.Equal(1920 + 12 - 344, x);
    }

    [Fact]
    public void SecondaryMonitorWithNegativeCoordinates()
    {
        var work = new PixelRect(-1920, 0, 0, 1040);

        var (x, y) = FlyoutPlacement.Compute(new PixelRect(-1820, 1040, -1644, 1080), work, 344, 224, 12, 8);

        Assert.Equal(-1904, x);
        Assert.Equal(820, y);
    }

    [Fact]
    public void AnchorAtTheTopOfTheScreen_KeepsCardInsideWorkArea()
    {
        var work = new PixelRect(0, 40, 1920, 1080);

        var (_, y) = FlyoutPlacement.Compute(new PixelRect(100, 0, 276, 40), work, 344, 224, 12, 8);

        Assert.Equal(40 - 12, y);
    }

    [Fact]
    public void Scale125_UsesScaledMarginAndGap()
    {
        var work = new PixelRect(0, 0, 2400, 1300);

        var (x, y) = FlyoutPlacement.Compute(new PixelRect(100, 1300, 320, 1350), work, 430, 280, 15, 10);

        Assert.Equal(-5, x);
        Assert.Equal(1025, y);
    }
}
