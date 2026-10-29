using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AITokenUsage.Core.Taskbar;
using AITokenUsage.Resources;

namespace AITokenUsage.UI;

/// <summary>
/// The taskbar widget. TaskbarHost owns placement (SetParent/SetWindowPos); this class only draws,
/// animates the 220 ms provider transition and handles click, wheel, hover (detail card) and the context menu.
/// </summary>
public partial class WidgetWindow : Window
{
    public static readonly TimeSpan TransitionDuration = TimeSpan.FromMilliseconds(220);
    public const double SlideOffset = 8;

    private readonly WidgetViewModel _vm;
    private readonly DetailFlyoutController _flyout;
    private readonly MenuItem _pauseItem;
    private ProviderPanel _front;
    private ProviderPanel _back;
    private int _transitionId;
    private bool _transitionRunning;
    private double _widthDip;

    public WidgetWindow(WidgetViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);

        _vm = vm;
        InitializeComponent();
        ApplyWidth();

        _front = PanelA;
        _back = PanelB;
        _flyout = new DetailFlyoutController(vm.Host, vm.Theme, this);
        _flyout.PointerOverChanged += OnFlyoutPointerOverChanged;
        _pauseItem = new MenuItem { Header = UiStrings.TrayMenuPauseRotation, IsCheckable = true };
        Root.ContextMenu = BuildContextMenu();

        Root.MouseLeftButtonUp += OnLeftButtonUp;
        Root.MouseWheel += OnMouseWheel;
        Root.MouseEnter += OnPointerEnter;
        Root.MouseLeave += OnPointerLeave;

        _vm.ProviderSwitched += OnProviderSwitched;
        _vm.Updated += OnUpdated;
        _vm.SystemUsageUpdated += OnSystemUsageUpdated;
        Closed += OnClosed;

        ResetPanels();
        _front.Render(_vm.CurrentModel, _vm.Palette, _vm.ShowResetTime);
        RenderSystemColumn();
    }

    /// <summary>The DIP-sized content root; TaskbarHost sizes the HWND from it.</summary>
    public FrameworkElement RootElement => Root;

    private ContextMenu BuildContextMenu()
    {
        var settings = new MenuItem { Header = UiStrings.TrayMenuSettings };
        settings.Click += (_, _) => _vm.OpenSettings();

        var refresh = new MenuItem { Header = UiStrings.TrayMenuRefreshNow };
        refresh.Click += (_, _) => _vm.RefreshNow();

        _pauseItem.Click += (_, _) => _vm.SetUserPaused(_pauseItem.IsChecked);

        var exit = new MenuItem { Header = UiStrings.TrayMenuExit };
        exit.Click += (_, _) => _vm.Exit();

        var menu = new ContextMenu();
        menu.Items.Add(settings);
        menu.Items.Add(refresh);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        menu.Opened += (_, _) =>
        {
            _pauseItem.IsChecked = _vm.IsUserPaused;
            _flyout.SetSuppressed(true);
        };
        menu.Closed += (_, _) => _flyout.SetSuppressed(false);
        return menu;
    }

    private void OnLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _vm.Next();
        e.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta < 0)
        {
            _vm.Next();
        }
        else if (e.Delta > 0)
        {
            _vm.Previous();
        }

        e.Handled = true;
    }

    private void OnPointerEnter(object sender, MouseEventArgs e) => _flyout.AnchorEnter();

    private void OnPointerLeave(object sender, MouseEventArgs e) => _flyout.AnchorLeave();

    private void OnFlyoutPointerOverChanged(object? sender, EventArgs e) => _vm.SetPointerOver(_flyout.IsPointerOver);

    private void OnUpdated(object? sender, EventArgs e)
    {
        ApplyWidth();
        _front.Render(_vm.CurrentModel, _vm.Palette, _vm.ShowResetTime);
        RenderSystemColumn();
    }

    /// <summary>A new PC sample in AlwaysBeside mode: only the fixed column redraws; the AI panel and its transition are untouched.</summary>
    private void OnSystemUsageUpdated(object? sender, EventArgs e) => RenderSystemColumn();

    /// <summary>Shows the PC column in AlwaysBeside mode (and draws the latest sample), hides it otherwise.</summary>
    private void RenderSystemColumn()
    {
        if (!_vm.SystemBeside)
        {
            SystemColumn.Visibility = Visibility.Collapsed;
            return;
        }

        SystemColumn.Visibility = Visibility.Visible;
        SystemColumn.Render(_vm.SystemUsage, _vm.Palette);
    }

    /// <summary>
    /// Keeps the WPF content width in step with the ShowResetTime and PC-column settings (176 to 282 DIP). TaskbarHost sizes
    /// the HWND from the same WidgetMetrics value on its next layout, so the two agree. Root.Width is only seeded here before
    /// that first layout (it is still NaN); afterwards TaskbarHost owns it (pixel width / DPI scale), and every setting
    /// flip reaches it through TaskbarHost's own SettingsChanged layout, so writing the round DIP value here would only
    /// reintroduce a 1 px drift at fractional DPI.
    /// </summary>
    private void ApplyWidth()
    {
        double width = WidgetMetrics.WidthDip(_vm.ShowResetTime, _vm.SystemBeside);
        if (width == _widthDip)
        {
            return;
        }

        _widthDip = width;
        Width = width;
        if (double.IsNaN(Root.Width))
        {
            Root.Width = width;
        }
    }

    private void OnProviderSwitched(object? sender, int direction)
    {
        CompleteTransition();

        if (direction == 0)
        {
            _front.Render(_vm.CurrentModel, _vm.Palette, _vm.ShowResetTime);
            return;
        }

        ProviderPanel outgoing = _front;
        ProviderPanel incoming = _back;
        incoming.Render(_vm.CurrentModel, _vm.Palette, _vm.ShowResetTime);
        incoming.Visibility = Visibility.Visible;
        _front = incoming;
        _back = outgoing;

        // Next: the old panel leaves upward and the new one rises from below. Previous: the reverse.
        double sign = direction > 0 ? 1 : -1;
        int id = ++_transitionId;
        _transitionRunning = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        Animate(outgoing, 1, 0, 0, -SlideOffset * sign, ease, null);
        Animate(incoming, 0, 1, SlideOffset * sign, 0, ease, (_, _) =>
        {
            if (id == _transitionId)
            {
                CompleteTransition();
            }
        });
    }

    private void CompleteTransition()
    {
        if (!_transitionRunning)
        {
            return;
        }

        _transitionRunning = false;
        _transitionId++;
        StopAnimations(_front);
        StopAnimations(_back);
        ResetPanels();
    }

    private void ResetPanels()
    {
        _front.Opacity = 1;
        _front.Slide.Y = 0;
        _front.Visibility = Visibility.Visible;
        _back.Opacity = 0;
        _back.Slide.Y = 0;
        _back.Visibility = Visibility.Hidden;
    }

    private static void StopAnimations(ProviderPanel panel)
    {
        panel.BeginAnimation(UIElement.OpacityProperty, null);
        panel.Slide.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private static void Animate(
        ProviderPanel panel,
        double fromOpacity,
        double toOpacity,
        double fromY,
        double toY,
        IEasingFunction ease,
        EventHandler? completed)
    {
        var duration = new Duration(TransitionDuration);
        var fade = new DoubleAnimation(fromOpacity, toOpacity, duration) { EasingFunction = ease };
        var slide = new DoubleAnimation(fromY, toY, duration) { EasingFunction = ease };
        if (completed is not null)
        {
            slide.Completed += completed;
        }

        panel.BeginAnimation(UIElement.OpacityProperty, fade);
        panel.Slide.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _vm.ProviderSwitched -= OnProviderSwitched;
        _vm.Updated -= OnUpdated;
        _vm.SystemUsageUpdated -= OnSystemUsageUpdated;
        _flyout.PointerOverChanged -= OnFlyoutPointerOverChanged;
        _flyout.Dispose();
        _vm.Dispose();
    }
}
