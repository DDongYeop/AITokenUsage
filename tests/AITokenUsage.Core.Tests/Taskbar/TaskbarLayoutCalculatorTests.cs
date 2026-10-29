using AITokenUsage.Core.Settings;
using AITokenUsage.Core.Taskbar;
using Xunit;

namespace AITokenUsage.Core.Tests.Taskbar;

public class TaskbarLayoutCalculatorTests
{
    // Ground truth from docs/research/win11-taskbar-embedding.md §0 (25H2 26200, 1920x1080, 100%, centered, Widgets hidden).
    private static readonly TaskbarGeometry GroundTruth = new(
        Taskbar: new PixelRect(0, 1032, 1920, 1080),
        Dpi: 96,
        IsHorizontal: true,
        IsCentered: true,
        Start: new PixelRect(718, 1032, 763, 1080),
        Widgets: null,
        AppButtons: new PixelRect(763, 1032, 1203, 1080),
        Tray: new PixelRect(1630, 1032, 1920, 1080));

    [Theory]
    [InlineData(WidgetPlacement.LeftEdge, 8, 4, 184, 44)]
    [InlineData(WidgetPlacement.BeforeStart, 534, 4, 710, 44)]
    [InlineData(WidgetPlacement.BeforeTray, 1446, 4, 1622, 44)]
    public void GroundTruth_Centered_AllPlacements(WidgetPlacement placement, int left, int top, int right, int bottom)
    {
        Assert.Equal(new PixelRect(left, top, right, bottom), TaskbarLayoutCalculator.Compute(GroundTruth, placement));
    }

    [Theory]
    [InlineData(WidgetPlacement.LeftEdge, 158, 4, 334, 44)]
    [InlineData(WidgetPlacement.BeforeStart, 534, 4, 710, 44)]
    [InlineData(WidgetPlacement.BeforeTray, 1446, 4, 1622, 44)]
    public void Centered_WithWidgetsAtFarLeft_LeftEdgeStartsAfterWidgets(WidgetPlacement placement, int left, int top, int right, int bottom)
    {
        var g = GroundTruth with { Widgets = new PixelRect(0, 1032, 150, 1080) };

        Assert.Equal(new PixelRect(left, top, right, bottom), TaskbarLayoutCalculator.Compute(g, placement));
    }

    [Theory]
    [InlineData(WidgetPlacement.LeftEdge)]
    [InlineData(WidgetPlacement.BeforeStart)]
    public void Centered_WidgetsLeaveTooLittleRoom_ReturnsNull(WidgetPlacement placement)
    {
        var g = GroundTruth with { Widgets = new PixelRect(0, 1032, 560, 1080) };

        Assert.Null(TaskbarLayoutCalculator.Compute(g, placement));
    }

    [Fact]
    public void Centered_GapExactlyWideEnough_Fits_OnePixelLessDoesNot()
    {
        var fits = GroundTruth with { Start = new PixelRect(192, 1032, 237, 1080) };
        var tooSmall = GroundTruth with { Start = new PixelRect(191, 1032, 236, 1080) };

        Assert.Equal(new PixelRect(8, 4, 184, 44), TaskbarLayoutCalculator.Compute(fits, WidgetPlacement.LeftEdge));
        Assert.Null(TaskbarLayoutCalculator.Compute(tooSmall, WidgetPlacement.LeftEdge));
        Assert.Null(TaskbarLayoutCalculator.Compute(tooSmall, WidgetPlacement.BeforeStart));
    }

    [Fact]
    public void Centered_ManyAppsCrowdTheTray_BeforeTrayReturnsNull()
    {
        var g = GroundTruth with { AppButtons = new PixelRect(763, 1032, 1450, 1080) };

        Assert.Null(TaskbarLayoutCalculator.Compute(g, WidgetPlacement.BeforeTray));
    }

    [Fact]
    public void Centered_StartUnknown_FallsBackToBeforeTray()
    {
        var g = GroundTruth with { Start = null };

        Assert.Equal(new PixelRect(1446, 4, 1622, 44), TaskbarLayoutCalculator.Compute(g, WidgetPlacement.LeftEdge));
    }

    [Fact]
    public void BeforeTray_NoAppButtons_UsesStartRight()
    {
        var g = GroundTruth with { AppButtons = null, Tray = new PixelRect(900, 1032, 1920, 1080) };

        // [763 + 8, 900 - 8] = [771, 892]: 121 px < 176 px.
        Assert.Null(TaskbarLayoutCalculator.Compute(g, WidgetPlacement.BeforeTray));
        Assert.Equal(new PixelRect(1446, 4, 1622, 44),
            TaskbarLayoutCalculator.Compute(GroundTruth with { AppButtons = null }, WidgetPlacement.BeforeTray));
    }

    [Fact]
    public void BeforeTray_NoAnchorAtAll_ReturnsNull()
    {
        var g = GroundTruth with { Start = null, AppButtons = null };

        Assert.Null(TaskbarLayoutCalculator.Compute(g, WidgetPlacement.BeforeTray));
    }

    private static readonly TaskbarGeometry LeftAligned = new(
        Taskbar: new PixelRect(0, 1032, 1920, 1080),
        Dpi: 96,
        IsHorizontal: true,
        IsCentered: false,
        Start: new PixelRect(12, 1032, 57, 1080),
        Widgets: new PixelRect(1460, 1032, 1620, 1080),
        AppButtons: new PixelRect(57, 1032, 497, 1080),
        Tray: new PixelRect(1630, 1032, 1920, 1080));

    [Theory]
    [InlineData(WidgetPlacement.LeftEdge)]
    [InlineData(WidgetPlacement.BeforeStart)]
    [InlineData(WidgetPlacement.BeforeTray)]
    public void LeftAligned_AllPlacementsUseBeforeTray_ClippedAtWidgets(WidgetPlacement placement)
    {
        // Right bound min(1630 - 8, 1460 - 8) = 1452.
        Assert.Equal(new PixelRect(1276, 4, 1452, 44), TaskbarLayoutCalculator.Compute(LeftAligned, placement));
    }

    [Fact]
    public void LeftAligned_WithoutWidgets_HugsTray()
    {
        var g = LeftAligned with { Widgets = null };

        Assert.Equal(new PixelRect(1446, 4, 1622, 44), TaskbarLayoutCalculator.Compute(g, WidgetPlacement.LeftEdge));
    }

    [Fact]
    public void LeftAligned_WidgetsLeaveTooLittleRoom_ReturnsNull()
    {
        var g = LeftAligned with { AppButtons = new PixelRect(57, 1032, 1200, 1080) };

        // [1208, 1452]: 244 px fits; push the apps further and it no longer does.
        Assert.NotNull(TaskbarLayoutCalculator.Compute(g, WidgetPlacement.BeforeTray));
        Assert.Null(TaskbarLayoutCalculator.Compute(g with { AppButtons = new PixelRect(57, 1032, 1300, 1080) }, WidgetPlacement.BeforeTray));
    }

    [Fact]
    public void VerticalTaskbar_ReturnsNull()
    {
        var g = GroundTruth with { Taskbar = new PixelRect(0, 0, 48, 1080), IsHorizontal = false };

        Assert.Null(TaskbarLayoutCalculator.Compute(g, WidgetPlacement.LeftEdge));
        Assert.Null(TaskbarLayoutCalculator.Compute(g, WidgetPlacement.BeforeTray));
    }

    [Fact]
    public void EmptyTaskbarRect_ReturnsNull()
    {
        var g = GroundTruth with { Taskbar = new PixelRect(0, 1080, 1920, 1080) };

        Assert.Null(TaskbarLayoutCalculator.Compute(g, WidgetPlacement.LeftEdge));
    }

    // Secondary taskbar on a monitor left of the primary (ground truth: Shell_SecondaryTrayWnd at x = -1920, no TrayNotifyWnd).
    private static readonly TaskbarGeometry Secondary = new(
        Taskbar: new PixelRect(-1920, 1032, 0, 1080),
        Dpi: 96,
        IsHorizontal: true,
        IsCentered: true,
        Start: new PixelRect(-1202, 1032, -1157, 1080),
        Widgets: null,
        AppButtons: new PixelRect(-1157, 1032, -761, 1080),
        Tray: null);

    [Theory]
    [InlineData(WidgetPlacement.LeftEdge, 8, 4, 184, 44)]
    [InlineData(WidgetPlacement.BeforeStart, 534, 4, 710, 44)]
    [InlineData(WidgetPlacement.BeforeTray, 1648, 4, 1824, 44)]
    public void Secondary_NegativeCoordinates_ResultIsParentRelative(WidgetPlacement placement, int left, int top, int right, int bottom)
    {
        // BeforeTray: no tray → trayLeft = 0 - 88 = -88, right bound -96, x = -272 → -272 - (-1920) = 1648.
        Assert.Equal(new PixelRect(left, top, right, bottom), TaskbarLayoutCalculator.Compute(Secondary, placement));
    }

    [Fact]
    public void Secondary_WithClockRect_UsesClockLeft()
    {
        var g = Secondary with { Tray = new PixelRect(-78, 1032, -8, 1080) };

        Assert.Equal(new PixelRect(1658, 4, 1834, 44), TaskbarLayoutCalculator.Compute(g, WidgetPlacement.BeforeTray));
    }

    // 125% (DPI 120): width 220, height min(60, 50) = 50, padding 10, y = 5.
    private static readonly TaskbarGeometry Scaled125 = new(
        Taskbar: new PixelRect(0, 1020, 1920, 1080),
        Dpi: 120,
        IsHorizontal: true,
        IsCentered: true,
        Start: new PixelRect(870, 1020, 926, 1080),
        Widgets: new PixelRect(0, 1020, 200, 1080),
        AppButtons: new PixelRect(926, 1020, 1200, 1080),
        Tray: new PixelRect(1550, 1020, 1920, 1080));

    [Theory]
    [InlineData(WidgetPlacement.LeftEdge, 210, 5, 430, 55)]
    [InlineData(WidgetPlacement.BeforeStart, 640, 5, 860, 55)]
    [InlineData(WidgetPlacement.BeforeTray, 1320, 5, 1540, 55)]
    public void Dpi125_ScalesWidthHeightAndPadding(WidgetPlacement placement, int left, int top, int right, int bottom)
    {
        Assert.Equal(new PixelRect(left, top, right, bottom), TaskbarLayoutCalculator.Compute(Scaled125, placement));
    }

    [Fact]
    public void Dpi125_Secondary_TrayFallbackIsScaled()
    {
        var g = Scaled125 with
        {
            Taskbar = new PixelRect(-1920, 1020, 0, 1080),
            Start = new PixelRect(-1050, 1020, -994, 1080),
            Widgets = null,
            AppButtons = new PixelRect(-994, 1020, -720, 1080),
            Tray = null,
        };

        // trayLeft = 0 - 110, right bound -120, x = -340 → 1580 relative.
        Assert.Equal(new PixelRect(1580, 5, 1800, 55), TaskbarLayoutCalculator.Compute(g, WidgetPlacement.BeforeTray));
    }

    [Theory]
    [InlineData(96u, 48, 8, 4, 184, 44)]
    [InlineData(120u, 60, 10, 5, 230, 55)]
    [InlineData(144u, 72, 12, 6, 276, 66)]
    [InlineData(168u, 84, 14, 7, 322, 77)]
    public void LeftEdge_ScalesWithDpi(uint dpi, int taskbarHeight, int left, int top, int right, int bottom)
    {
        var g = new TaskbarGeometry(new PixelRect(0, 2160 - taskbarHeight, 3840, 2160), dpi, true, true,
            Start: new PixelRect(1800, 2160 - taskbarHeight, 1860, 2160), Widgets: null, AppButtons: null, Tray: null);

        Assert.Equal(new PixelRect(left, top, right, bottom), TaskbarLayoutCalculator.Compute(g, WidgetPlacement.LeftEdge));
    }

    [Fact]
    public void ShortTaskbar_HeightIsClampedToTaskbar()
    {
        var g = GroundTruth with { Taskbar = new PixelRect(0, 1048, 1920, 1080) };

        Assert.Equal(new PixelRect(8, 0, 184, 32), TaskbarLayoutCalculator.Compute(g, WidgetPlacement.LeftEdge));
    }

    [Fact]
    public void OddHeightDifference_RoundsTopDown()
    {
        var g = GroundTruth with { Taskbar = new PixelRect(0, 1031, 1920, 1080) };

        Assert.Equal(new PixelRect(8, 4, 184, 44), TaskbarLayoutCalculator.Compute(g, WidgetPlacement.LeftEdge));
    }

    [Fact]
    public void ZeroDpi_IsTreatedAs96()
    {
        Assert.Equal(new PixelRect(8, 4, 184, 44), TaskbarLayoutCalculator.Compute(GroundTruth with { Dpi = 0 }, WidgetPlacement.LeftEdge));
    }

    [Fact]
    public void TrayOutsideTaskbar_ResultOutsideParent_ReturnsNull()
    {
        var g = GroundTruth with { Tray = new PixelRect(2500, 1032, 2600, 1080) };

        Assert.Null(TaskbarLayoutCalculator.Compute(g, WidgetPlacement.BeforeTray));
    }

    [Fact]
    public void WiderWidget_ScalesWidthByDpi()
    {
        var g = GroundTruth with { Dpi = 144 };

        // 210 DIP at 150% = 315 px; BeforeTray: right bound 1630 - 12 = 1618, height min(48, 60) = 48.
        Assert.Equal(new PixelRect(1303, 0, 1618, 48), TaskbarLayoutCalculator.Compute(g, WidgetPlacement.BeforeTray, widthDip: 210));
    }

    [Fact]
    public void WiderWidget_DoesNotFitGapThatNarrowOneFits()
    {
        // Gap [8, 184] = 176 px: fits the narrow widget, not the 210 DIP one.
        var g = GroundTruth with { Start = new PixelRect(192, 1032, 237, 1080) };

        Assert.NotNull(TaskbarLayoutCalculator.Compute(g, WidgetPlacement.LeftEdge, WidgetMetrics.WidthDip(showResetTime: false)));
        Assert.Null(TaskbarLayoutCalculator.Compute(g, WidgetPlacement.LeftEdge, WidgetMetrics.WidthDip(showResetTime: true)));
    }
}
