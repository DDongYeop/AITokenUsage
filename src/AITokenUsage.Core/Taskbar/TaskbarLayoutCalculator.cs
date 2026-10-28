using AITokenUsage.Core.Settings;

namespace AITokenUsage.Core.Taskbar;

/// <summary>
/// Pure placement math for the widget inside the taskbar (spec §4.3). No Win32, no UIA.
/// </summary>
public static class TaskbarLayoutCalculator
{
    public const double DesiredWidthDip = 176, DesiredHeightDip = 40, PaddingDip = 8;

    /// <summary>Width assumed for the clock area when a (secondary) taskbar reports no tray rectangle.</summary>
    public const double TrayFallbackWidthDip = 88;

    /// <summary>작업 표시줄 기준(부모 상대) 물리 px 사각형. 공간 부족·세로 작업 표시줄이면 null.</summary>
    public static PixelRect? Compute(TaskbarGeometry g, WidgetPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(g);
        PixelRect tb = g.Taskbar;
        if (!g.IsHorizontal || tb.IsEmpty)
        {
            return null;
        }

        double scale = g.Dpi == 0 ? 1.0 : g.Dpi / 96.0;
        int width = Px(DesiredWidthDip, scale);
        int height = Math.Min(tb.Height, Px(DesiredHeightDip, scale));
        int pad = Px(PaddingDip, scale);

        // LeftEdge/BeforeStart only exist on a centered taskbar with a known Start button; otherwise use BeforeTray.
        bool useStartGap = g.IsCentered
            && g.Start is not null
            && (placement == WidgetPlacement.LeftEdge || placement == WidgetPlacement.BeforeStart);

        int? screenX = useStartGap
            ? LeftOfStart(g, placement, width, pad)
            : BeforeTray(g, width, pad, scale);
        if (screenX is null)
        {
            return null;
        }

        int left = screenX.Value - tb.Left;
        int top = (tb.Height - height) / 2;
        var rect = new PixelRect(left, top, left + width, top + height);
        return rect.IsInside(new PixelRect(0, 0, tb.Width, tb.Height)) ? rect : null;
    }

    /// <summary>Gap [(Widgets.Right ?? tb.Left) + pad, Start.Left - pad]: LeftEdge hugs its left end, BeforeStart its right end.</summary>
    private static int? LeftOfStart(TaskbarGeometry g, WidgetPlacement placement, int width, int pad)
    {
        PixelRect start = g.Start!.Value;
        int leftAnchor = g.Widgets is { } widgets && widgets.Right <= start.Left ? widgets.Right : g.Taskbar.Left;
        int leftBound = leftAnchor + pad;
        int rightBound = start.Left - pad;
        if (rightBound - leftBound < width)
        {
            return null;
        }

        return placement == WidgetPlacement.LeftEdge ? leftBound : rightBound - width;
    }

    /// <summary>
    /// Gap [(AppButtons.Right ?? Start.Right) + pad, trayLeft - pad], clipped at Widgets.Left - pad when the Widgets
    /// button lies inside it; the widget hugs the right end. trayLeft falls back to tb.Right - 88 DIP.
    /// </summary>
    private static int? BeforeTray(TaskbarGeometry g, int width, int pad, double scale)
    {
        int? anchor = g.AppButtons?.Right ?? g.Start?.Right;
        if (anchor is null)
        {
            return null;
        }

        int leftBound = anchor.Value + pad;
        int trayLeft = g.Tray?.Left ?? g.Taskbar.Right - Px(TrayFallbackWidthDip, scale);
        int rightBound = trayLeft - pad;
        if (g.Widgets is { } widgets && widgets.Right > leftBound && widgets.Left < rightBound)
        {
            rightBound = Math.Min(rightBound, widgets.Left - pad);
        }

        if (rightBound - leftBound < width)
        {
            return null;
        }

        return rightBound - width;
    }

    private static int Px(double dip, double scale) => (int)Math.Round(dip * scale, MidpointRounding.AwayFromZero);
}
