using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.Rotation;
using AITokenUsage.Core.Taskbar;
using AITokenUsage.Taskbar;

namespace AITokenUsage.UI;

/// <summary>Hover logic for the detail card: open 400 ms after entering the widget, close 200 ms after leaving widget and card.</summary>
public sealed class DetailFlyoutController : IDisposable
{
    public static readonly TimeSpan OpenDelay = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan ClockRefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>How often the open card compares the real cursor position with the widget and card windows.</summary>
    public static readonly TimeSpan HoverCheckInterval = TimeSpan.FromMilliseconds(100);

    private readonly AppHost _host;
    private readonly ThemeWatcher _theme;
    private readonly Window _anchor;
    private readonly DispatcherTimer _openTimer;
    private readonly DispatcherTimer _closeTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _hoverCheckTimer;
    private DetailFlyout? _flyout;
    private bool _overAnchor;
    private bool _overFlyout;
    private bool _suppressed;
    private bool _disposed;

    public DetailFlyoutController(AppHost host, ThemeWatcher theme, Window anchor)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(anchor);

        _host = host;
        _theme = theme;
        _anchor = anchor;
        _openTimer = CreateTimer(OpenDelay, OnOpenTimer);
        _closeTimer = CreateTimer(CloseDelay, OnCloseTimer);
        _clockTimer = CreateTimer(ClockRefreshInterval, (_, _) => RefreshIfOpen());
        _hoverCheckTimer = CreateTimer(HoverCheckInterval, OnHoverCheck);

        _host.ViewChanged += OnViewChanged;
        _host.SettingsChanged += OnSettingsChanged;
        _host.Rotation.CurrentChanged += OnCurrentChanged;
        _theme.Changed += OnThemeChanged;
    }

    public bool IsOpen => _flyout is { IsVisible: true };

    /// <summary>True while the pointer is over the widget or over the open card.</summary>
    public bool IsPointerOver => _overAnchor || _overFlyout;

    public event EventHandler? PointerOverChanged;

    public void AnchorEnter()
    {
        if (_disposed)
        {
            return;
        }

        _overAnchor = true;
        _closeTimer.Stop();
        if (!IsOpen && CanOpen)
        {
            Restart(_openTimer);
        }

        PointerOverChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AnchorLeave()
    {
        if (_disposed)
        {
            return;
        }

        _overAnchor = false;
        _openTimer.Stop();
        if (IsOpen && !_overFlyout)
        {
            Restart(_closeTimer);
        }

        PointerOverChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>While suppressed (context menu open) the card closes and does not open.</summary>
    public void SetSuppressed(bool suppressed)
    {
        _suppressed = suppressed;
        if (suppressed)
        {
            CloseNow();
        }
    }

    public void CloseNow()
    {
        _openTimer.Stop();
        _closeTimer.Stop();
        _clockTimer.Stop();
        _hoverCheckTimer.Stop();
        _flyout?.Hide();

        if (_overFlyout)
        {
            _overFlyout = false;
            PointerOverChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.ViewChanged -= OnViewChanged;
        _host.SettingsChanged -= OnSettingsChanged;
        _host.Rotation.CurrentChanged -= OnCurrentChanged;
        _theme.Changed -= OnThemeChanged;
        _openTimer.Stop();
        _closeTimer.Stop();
        _clockTimer.Stop();
        _hoverCheckTimer.Stop();
        _flyout?.Close();
        _flyout = null;
    }

    private DispatcherTimer CreateTimer(TimeSpan interval, EventHandler tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, _anchor.Dispatcher) { Interval = interval };
        timer.Tick += tick;
        return timer;
    }

    private static void Restart(DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }

    private void OnOpenTimer(object? sender, EventArgs e)
    {
        _openTimer.Stop();
        if (!_disposed && _overAnchor && CanOpen)
        {
            Open();
        }
    }

    /// <summary>Not while the context menu is open, and only when the user keeps the hover card enabled.</summary>
    private bool CanOpen => !_suppressed && _host.Settings.ShowDetailCard;

    /// <summary>
    /// The close decision trusts the real cursor, not the last WPF MouseEnter/MouseLeave. Those only say which window
    /// gets mouse messages, so a window moving or resizing under a resting cursor (the card re-rendering for the next
    /// AI) or capture taken by another window reports a leave while the cursor is still over the widget or the card.
    /// In that case the card stays open and the hover check keeps watching.
    /// </summary>
    private void OnCloseTimer(object? sender, EventArgs e)
    {
        _closeTimer.Stop();
        if (_disposed)
        {
            return;
        }

        if (SampleCursor() is FlyoutHoverState state)
        {
            ApplyPointer(state);
            if (state.IsOver)
            {
                return;
            }
        }
        else if (IsPointerOver)
        {
            return; // cursor unreadable (e.g. secure desktop): fall back to the WPF events
        }

        CloseNow();
    }

    /// <summary>
    /// Runs while the card is open: keeps the card anchored if the widget moved, re-syncs the hover flags to the real
    /// cursor and starts the 200 ms close countdown when the cursor has left both windows, even if WPF raised no
    /// MouseLeave for it (it may already have reported a spurious one).
    /// </summary>
    private void OnHoverCheck(object? sender, EventArgs e)
    {
        if (_disposed || _flyout is null || !IsOpen)
        {
            _hoverCheckTimer.Stop();
            return;
        }

        _flyout.Reposition();
        if (SampleCursor() is not FlyoutHoverState state)
        {
            return;
        }

        ApplyPointer(state);
        if (state.IsOver)
        {
            _closeTimer.Stop();
        }
        else if (!_closeTimer.IsEnabled)
        {
            _closeTimer.Start();
        }
    }

    /// <summary>Where the cursor really is; null when it cannot be read.</summary>
    private FlyoutHoverState? SampleCursor()
    {
        if (NativeMethods.TryGetCursorPos() is not PixelPoint cursor)
        {
            return null;
        }

        nint anchor = new WindowInteropHelper(_anchor).Handle;
        PixelRect? anchorRect = anchor != 0 && NativeMethods.IsWindowVisible(anchor) ? NativeMethods.TryGetWindowRect(anchor) : null;
        PixelRect? cardRect = _flyout is not null && IsOpen ? NativeMethods.TryGetWindowRect(new WindowInteropHelper(_flyout).Handle) : null;
        return FlyoutHover.Locate(cursor, anchorRect, cardRect);
    }

    private void ApplyPointer(FlyoutHoverState state)
    {
        if (state.OverAnchor == _overAnchor && state.OverCard == _overFlyout)
        {
            return;
        }

        _overAnchor = state.OverAnchor;
        _overFlyout = state.OverCard;
        PointerOverChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Open()
    {
        DetailCardModel? model = BuildModel();
        nint anchorHandle = new WindowInteropHelper(_anchor).Handle;
        if (model is null || anchorHandle == 0)
        {
            return;
        }

        DetailFlyout flyout = EnsureFlyout();
        flyout.Render(model, _theme.IsTaskbarLight);
        flyout.ShowAbove(anchorHandle);
        _clockTimer.Start();
        _hoverCheckTimer.Start();
    }

    private DetailFlyout EnsureFlyout()
    {
        if (_flyout is not null)
        {
            return _flyout;
        }

        var flyout = new DetailFlyout();
        flyout.MouseEnter += (_, _) =>
        {
            _overFlyout = true;
            _closeTimer.Stop();
            PointerOverChanged?.Invoke(this, EventArgs.Empty);
        };
        flyout.MouseLeave += (_, _) =>
        {
            _overFlyout = false;
            if (!_overAnchor)
            {
                Restart(_closeTimer);
            }

            PointerOverChanged?.Invoke(this, EventArgs.Empty);
        };

        _flyout = flyout;
        return flyout;
    }

    private DetailCardModel? BuildModel()
    {
        string? id = _host.Rotation.CurrentId;
        ProviderView? view = id is null ? null : _host.Usage.GetView(id);
        return view is null
            ? null
            : DetailCardBuilder.Build(view, _host.Settings.ValueMode, _host.ProviderContext.Time.GetUtcNow(), TimeZoneInfo.Local);
    }

    private void RefreshIfOpen()
    {
        if (_disposed || _flyout is null || !IsOpen)
        {
            return;
        }

        DetailCardModel? model = BuildModel();
        if (model is null)
        {
            CloseNow();
            return;
        }

        _flyout.Render(model, _theme.IsTaskbarLight);
    }

    private void OnViewChanged(object? sender, ProviderView view)
    {
        if (view.ProviderId == _host.Rotation.CurrentId)
        {
            RefreshIfOpen();
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (!_host.Settings.ShowDetailCard)
        {
            CloseNow();
            return;
        }

        RefreshIfOpen();
    }

    private void OnCurrentChanged(object? sender, RotationChangedEventArgs e) => RefreshIfOpen();

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (_anchor.Dispatcher.CheckAccess())
        {
            RefreshIfOpen();
        }
        else
        {
            _anchor.Dispatcher.InvokeAsync(RefreshIfOpen);
        }
    }
}
