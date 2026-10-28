using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.Taskbar;

namespace AITokenUsage.UI;

/// <summary>The hover card: a non-activating topmost tool window drawn above the widget.</summary>
public partial class DetailFlyout : Window
{
    /// <summary>Transparent border around the card that holds the drop shadow (matches the XAML Grid margin).</summary>
    public const double ShadowMargin = 12;
    public const double GapAboveAnchor = 8;
    public const double BarWidth = 160;

    private const double StaleOpacity = 0.55;

    private static readonly SolidColorBrush DarkBackground = Frozen(Color.FromRgb(0x2B, 0x2B, 0x2B));
    private static readonly SolidColorBrush LightBackground = Frozen(Color.FromRgb(0xF9, 0xF9, 0xF9));
    private static readonly SolidColorBrush DarkBorder = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush LightBorder = Frozen(Color.FromArgb(0x1F, 0x00, 0x00, 0x00));

    private nint _anchor;

    public DetailFlyout()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Reposition();
        DpiChanged += (_, _) => Reposition();
    }

    /// <param name="taskbarIsLight">System (taskbar) theme, not the app theme (spec 2.3, 4.6).</param>
    public void Render(DetailCardModel model, bool taskbarIsLight)
    {
        ArgumentNullException.ThrowIfNull(model);

        WidgetPalette palette = taskbarIsLight ? WidgetPalette.DarkText : WidgetPalette.LightText;
        Brush background = taskbarIsLight ? LightBackground : DarkBackground;
        Card.Background = background;
        Card.BorderBrush = taskbarIsLight ? LightBorder : DarkBorder;
        ShadowLayer.Background = background;
        Divider.Background = palette.Track;

        GlyphHost.Content = ProviderGlyphs.CreateIcon(model.ProviderId, 16, palette.Text);
        TitleText.Text = model.DisplayName;
        TitleText.Foreground = palette.Text;
        PlanText.Text = model.Plan ?? "";
        PlanText.Foreground = palette.Muted;
        PlanText.Visibility = string.IsNullOrEmpty(model.Plan) ? Visibility.Collapsed : Visibility.Visible;

        RowsPanel.Children.Clear();
        foreach (CardRow row in model.Rows)
        {
            RowsPanel.Children.Add(BuildRow(row, palette, model.ProviderId));
        }

        ExtraText.Text = model.ExtraLine ?? "";
        ExtraText.Foreground = palette.Text;
        ExtraText.Opacity = model.IsStale ? StaleOpacity : 1;
        ExtraText.Visibility = model.ExtraLine is null ? Visibility.Collapsed : Visibility.Visible;

        Divider.Visibility = model.Rows.Count > 0 || model.ExtraLine is not null ? Visibility.Visible : Visibility.Collapsed;

        StatusPanel.Children.Clear();
        foreach (string line in model.StatusLines)
        {
            StatusPanel.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 11,
                Foreground = palette.Muted,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 360,
                Margin = new Thickness(0, 0, 0, 2),
            });
        }
    }

    /// <summary>Shows the card above <paramref name="anchor"/> (the widget HWND) without activating it.</summary>
    public void ShowAbove(nint anchor)
    {
        _anchor = anchor;

        if (!IsVisible)
        {
            // Show transparent first so the window never flashes at its default position.
            Opacity = 0;
            Show();
            UpdateLayout();
        }

        Reposition();
        Opacity = 1;
    }

    /// <summary>Re-centres the card above the anchor using the card's own DPI and the anchor monitor's work area.</summary>
    public void Reposition()
    {
        nint self = new WindowInteropHelper(this).Handle;
        if (self == 0 || _anchor == 0 || !IsVisible)
        {
            return;
        }

        if (!UiNative.GetWindowRect(_anchor, out UiNative.RECT anchor) || !UiNative.GetWindowRect(self, out UiNative.RECT own))
        {
            return;
        }

        nint monitor = UiNative.MonitorFromRect(ref anchor, UiNative.MONITOR_DEFAULTTONEAREST);
        var info = new UiNative.MONITORINFO { cbSize = (uint)Marshal.SizeOf<UiNative.MONITORINFO>() };
        if (monitor == 0 || !UiNative.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        uint dpi = UiNative.GetDpiForWindow(self);
        double scale = dpi == 0 ? 1.0 : dpi / 96.0;
        int margin = (int)Math.Round(ShadowMargin * scale);
        int gap = (int)Math.Round(GapAboveAnchor * scale);

        (int x, int y) = FlyoutPlacement.Compute(
            new PixelRect(anchor.Left, anchor.Top, anchor.Right, anchor.Bottom),
            new PixelRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom),
            own.Right - own.Left,
            own.Bottom - own.Top,
            margin,
            gap);

        if (x == own.Left && y == own.Top)
        {
            return;
        }

        UiNative.SetWindowPos(self, UiNative.HWND_TOPMOST, x, y, 0, 0,
            UiNative.SWP_NOSIZE | UiNative.SWP_NOACTIVATE | UiNative.SWP_NOOWNERZORDER);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        nint hwnd = new WindowInteropHelper(this).Handle;
        nint exStyle = UiNative.GetWindowLongPtr(hwnd, UiNative.GWL_EXSTYLE);
        UiNative.SetWindowLongPtr(hwnd, UiNative.GWL_EXSTYLE, exStyle | UiNative.WS_EX_TOOLWINDOW | UiNative.WS_EX_NOACTIVATE);
    }

    private static FrameworkElement BuildRow(CardRow row, WidgetPalette palette, string providerId)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10), Opacity = row.Dimmed ? StaleOpacity : 1 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BarWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };
        var value = new TextBlock
        {
            Text = row.Value,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = palette.Text,
            Margin = new Thickness(12, 0, 0, 0),
        };
        DockPanel.SetDock(value, Dock.Right);
        header.Children.Add(value);
        header.Children.Add(new TextBlock
        {
            Text = row.Label,
            FontSize = 12,
            Foreground = palette.Text,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumnSpan(header, 2);
        grid.Children.Add(header);

        var reset = new TextBlock
        {
            Text = row.ResetText,
            FontSize = 11,
            Foreground = palette.Muted,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(reset, 1);

        if (row.IsText)
        {
            Grid.SetColumnSpan(reset, 2);
        }
        else
        {
            var track = new Border
            {
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = palette.Track,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Border
                {
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = Math.Round(BarWidth * Math.Clamp(row.Fraction ?? 0, 0, 1)),
                    Background = palette.BarBrush(providerId, row.Severity),
                },
            };
            Grid.SetRow(track, 1);
            grid.Children.Add(track);

            Grid.SetColumn(reset, 1);
            reset.Margin = new Thickness(12, 0, 0, 0);
        }

        if (row.ResetText.Length > 0)
        {
            grid.Children.Add(reset);
        }

        return grid;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
