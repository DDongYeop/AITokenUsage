using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

    public WidgetWindow(WidgetViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);

        _vm = vm;
        InitializeComponent();

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
        Closed += OnClosed;

        ResetPanels();
        _front.Render(_vm.CurrentModel, _vm.Palette);
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

    private void OnUpdated(object? sender, EventArgs e) => _front.Render(_vm.CurrentModel, _vm.Palette);

    private void OnProviderSwitched(object? sender, int direction)
    {
        CompleteTransition();

        if (direction == 0)
        {
            _front.Render(_vm.CurrentModel, _vm.Palette);
            return;
        }

        ProviderPanel outgoing = _front;
        ProviderPanel incoming = _back;
        incoming.Render(_vm.CurrentModel, _vm.Palette);
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
        _flyout.PointerOverChanged -= OnFlyoutPointerOverChanged;
        _flyout.Dispose();
        _vm.Dispose();
    }
}
