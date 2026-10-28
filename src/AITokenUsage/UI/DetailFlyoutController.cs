using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.Rotation;
using AITokenUsage.Taskbar;

namespace AITokenUsage.UI;

/// <summary>Hover logic for the detail card: open 400 ms after entering the widget, close 200 ms after leaving widget and card.</summary>
public sealed class DetailFlyoutController : IDisposable
{
    public static readonly TimeSpan OpenDelay = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan ClockRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly AppHost _host;
    private readonly ThemeWatcher _theme;
    private readonly Window _anchor;
    private readonly DispatcherTimer _openTimer;
    private readonly DispatcherTimer _closeTimer;
    private readonly DispatcherTimer _clockTimer;
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
        if (!IsOpen && !_suppressed)
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
        if (!_disposed && _overAnchor && !_suppressed)
        {
            Open();
        }
    }

    private void OnCloseTimer(object? sender, EventArgs e)
    {
        _closeTimer.Stop();
        if (!IsPointerOver)
        {
            CloseNow();
        }
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
        flyout.Render(model, _theme.IsAppLight);
        flyout.ShowAbove(anchorHandle);
        _clockTimer.Start();
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

        _flyout.Render(model, _theme.IsAppLight);
    }

    private void OnViewChanged(object? sender, ProviderView view)
    {
        if (view.ProviderId == _host.Rotation.CurrentId)
        {
            RefreshIfOpen();
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => RefreshIfOpen();

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
