using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Taskbar;
using AITokenUsage.UI;

namespace AITokenUsage.Taskbar;

/// <summary>
/// Hosts the widget inside the Windows 11 taskbar as a WS_CHILD of Shell_TrayWnd / Shell_SecondaryTrayWnd
/// (spec §4.2–4.4), falling back to a taskbar-owned overlay window (§4.5). UI thread only.
/// </summary>
public sealed class TaskbarHost : IDisposable
{
    public static readonly TimeSpan ReconcileInterval = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan RecreateDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan LayoutWaitTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan LayoutPollInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan AttachRetryInterval = TimeSpan.FromSeconds(10);
    public const int MaxReparentFailures = 6;
    public const string ForceOverlayVariable = "AITOKENUSAGE_FORCE_OVERLAY";

    private enum HostMode
    {
        None,
        Child,
        Overlay,
    }

    private readonly Func<WidgetWindow> _createWindow;
    private readonly AppHost _host;
    private readonly ShellEventWindow _shell;
    private readonly IAppLog _log;
    private readonly TaskbarGeometryReader _geometry;
    private readonly FullscreenWatcher _fullscreen;
    private readonly DispatcherTimer _reconcileTimer;
    private readonly DispatcherTimer _retryTimer;
    private readonly DispatcherTimer _selectionTimer;
    private readonly bool _forceOverlay;

    private WidgetWindow? _window;
    private nint _hwnd;
    private HostMode _mode;
    private TaskbarInfo? _taskbar;
    private string? _requestedDevice;
    private uint _baselineDpi;
    private PixelRect? _lastApplied;
    private bool _shown;
    private bool _positionFailureLogged;
    private int _reparentFailures;
    private int _generation;
    private bool _selectionCheckRunning;
    private bool _started;
    private bool _disposed;

    public TaskbarHost(Func<WidgetWindow> createWindow, AppHost host, ShellEventWindow shell)
    {
        ArgumentNullException.ThrowIfNull(createWindow);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(shell);
        _createWindow = createWindow;
        _host = host;
        _shell = shell;
        _log = host.Log;
        _geometry = new TaskbarGeometryReader(_log);
        _fullscreen = new FullscreenWatcher(host.Dispatcher, _log);
        _reconcileTimer = new DispatcherTimer(DispatcherPriority.Background, host.Dispatcher) { Interval = ReconcileInterval };
        _reconcileTimer.Tick += OnReconcileTick;
        _retryTimer = new DispatcherTimer(DispatcherPriority.Background, host.Dispatcher) { Interval = AttachRetryInterval };
        _retryTimer.Tick += OnRetryTick;
        _selectionTimer = new DispatcherTimer(DispatcherPriority.Background, host.Dispatcher) { Interval = AttachRetryInterval };
        _selectionTimer.Tick += OnSelectionTick;
        _forceOverlay = Environment.GetEnvironmentVariable(ForceOverlayVariable) == "1";
    }

    /// <summary>True while the widget is a child window of the taskbar.</summary>
    public bool IsEmbedded => _mode == HostMode.Child;

    /// <summary>True while the widget is a taskbar-owned overlay window.</summary>
    public bool IsOverlay => _mode == HostMode.Overlay;

    public WidgetWindow? CurrentWindow => _window;

    /// <summary>Raised on the UI thread after a new widget window has been attached and shown.</summary>
    public event EventHandler<WidgetWindow>? WindowCreated;

    public void Start()
    {
        _host.Dispatcher.VerifyAccess();
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _shell.TaskbarCreated += OnTaskbarCreated;
        _shell.DisplayChanged += OnDisplayChanged;
        _shell.TaskbarSettingsChanged += OnTaskbarSettingsChanged;
        _host.SettingsChanged += OnSettingsChanged;
        _fullscreen.Changed += OnFullscreenChanged;
        RequestRecreate("startup", TimeSpan.Zero);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _generation++;
        _shell.TaskbarCreated -= OnTaskbarCreated;
        _shell.DisplayChanged -= OnDisplayChanged;
        _shell.TaskbarSettingsChanged -= OnTaskbarSettingsChanged;
        _host.SettingsChanged -= OnSettingsChanged;
        _fullscreen.Changed -= OnFullscreenChanged;
        _reconcileTimer.Tick -= OnReconcileTick;
        _retryTimer.Tick -= OnRetryTick;
        _selectionTimer.Tick -= OnSelectionTick;
        TearDownWindow();
        _geometry.Dispose();
        _fullscreen.Dispose();
    }

    private void RequestRecreate(string reason, TimeSpan delay)
    {
        if (_disposed)
        {
            return;
        }

        _log.Info($"taskbar host: (re)creating the widget in {delay.TotalSeconds:0.#} s ({reason})");
        _ = RecreateAsync(++_generation, delay);
    }

    private async Task RecreateAsync(int generation, TimeSpan delay)
    {
        try
        {
            TearDownWindow();
            _taskbar = null;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
            }

            if (generation != _generation || _disposed)
            {
                return;
            }

            string? device = _host.Settings.MonitorDeviceName;
            _requestedDevice = device;
            (TaskbarInfo? taskbar, string? fallback) = await Task.Run(() => LocateTaskbar(device));
            if (generation != _generation || _disposed)
            {
                return;
            }

            if (taskbar is null)
            {
                _log.Warn("taskbar host: no explorer-owned taskbar found");
                RequestRecreate("waiting for the taskbar", AttachRetryInterval);
                return;
            }

            if (fallback is not null)
            {
                // Logged once per (re)creation; the periodic selection re-check stays quiet.
                _log.Warn($"taskbar host: {fallback}; re-checking every {AttachRetryInterval.TotalSeconds:0} s");
            }

            // Boot race (TrafficMonitor #2098/#2194): wait until UIA sees the Start button or app buttons (15 s cap).
            _geometry.SetTaskbar(taskbar.Handle);
            var wait = Stopwatch.StartNew();
            while (!_geometry.IsLayoutReady && wait.Elapsed < LayoutWaitTimeout)
            {
                _geometry.RequestRefresh();
                await Task.Delay(LayoutPollInterval);
                if (generation != _generation || _disposed)
                {
                    return;
                }
            }

            if (!_geometry.IsLayoutReady)
            {
                _log.Warn("taskbar host: UIA did not report the Start button within 15 s; attaching anyway");
            }

            if (!NativeMethods.IsWindow(taskbar.Handle))
            {
                RequestRecreate("taskbar disappeared while waiting", RecreateDelay);
                return;
            }

            _taskbar = taskbar;
            _baselineDpi = NativeMethods.GetDpiForWindow(taskbar.Handle);
            AttachOrFallback(taskbar);
        }
        catch (Exception ex)
        {
            _log.Error("taskbar host: failed to create the widget window", ex);
            if (generation == _generation && !_disposed)
            {
                RequestRecreate("previous attempt failed", AttachRetryInterval);
            }
        }
    }

    /// <summary>
    /// Background thread; does not log. TaskbarLocator.Find plus a visibility check: a hidden secondary taskbar ("show
    /// taskbar on all displays" off) falls back to the primary taskbar. Fallback describes why the requested monitor's
    /// taskbar is not the one returned (null when it is, or when no monitor was requested).
    /// </summary>
    private static (TaskbarInfo? Taskbar, string? Fallback) LocateTaskbar(string? device)
    {
        TaskbarInfo? found = TaskbarLocator.Find(device);
        if (found is not null && !found.IsPrimary && !NativeMethods.IsWindowVisible(found.Handle))
        {
            TaskbarInfo? primary = TaskbarLocator.Find(null);
            if (primary is not null && primary.IsPrimary)
            {
                return (primary, $"the taskbar on {device ?? found.MonitorDeviceName ?? "?"} is hidden; using the primary taskbar");
            }
        }

        if (found is not null
            && !string.IsNullOrWhiteSpace(device)
            && !string.Equals(found.MonitorDeviceName, device, StringComparison.OrdinalIgnoreCase))
        {
            return (found, $"no taskbar on {device}; using the one on {found.MonitorDeviceName ?? "?"}");
        }

        return (found, null);
    }

    /// <summary>True while a monitor is selected but the widget sits on another (fallback) taskbar.</summary>
    private bool IsSelectionFallback =>
        _taskbar is TaskbarInfo current
        && _requestedDevice is not null
        && !string.Equals(current.MonitorDeviceName, _requestedDevice, StringComparison.OrdinalIgnoreCase);

    private void AttachOrFallback(TaskbarInfo taskbar)
    {
        if (_forceOverlay)
        {
            _log.Info($"taskbar host: overlay forced by {ForceOverlayVariable}=1");
        }
        else if (!taskbar.IsWin11)
        {
            _log.Info("taskbar host: not the Windows 11 XAML taskbar (no DesktopWindowContentBridge); using overlay mode");
        }
        else
        {
            if (TryCreateChild(taskbar) is WidgetWindow candidate)
            {
                Adopt(candidate, HostMode.Child, taskbar);
                return;
            }

            _log.Warn("taskbar host: child attach failed; using overlay mode and retrying every 10 s");
        }

        StartOverlay(taskbar);
    }

    /// <summary>Creates and attaches a child window. Any failure (including exceptions) closes the candidate.</summary>
    private WidgetWindow? TryCreateChild(TaskbarInfo taskbar)
    {
        WidgetWindow? candidate = null;
        try
        {
            candidate = CreateWindowFor(taskbar);
            if (TryAttachChild(candidate, taskbar.Handle))
            {
                return candidate;
            }
        }
        catch (Exception ex)
        {
            _log.Error("taskbar host: child attach threw", ex);
        }

        if (candidate is not null)
        {
            CloseQuietly(candidate);
        }

        return null;
    }

    /// <summary>Pre-positions the (not yet created) window on the taskbar's monitor so WPF picks that monitor's DPI.</summary>
    private WidgetWindow CreateWindowFor(TaskbarInfo taskbar)
    {
        WidgetWindow window = _createWindow();
        try
        {
            double systemScale = Math.Max(1, NativeMethods.GetDpiForSystem()) / 96.0;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = (taskbar.Bounds.Left + 8) / systemScale;
            window.Top = (taskbar.Bounds.Top + 2) / systemScale;
            return window;
        }
        catch
        {
            CloseQuietly(window);
            throw;
        }
    }

    /// <summary>Spec §4.2 attach sequence. On success the window is shown (collapsed until the first layout).</summary>
    private bool TryAttachChild(WidgetWindow window, nint taskbar)
    {
        nint hwnd = new WindowInteropHelper(window).EnsureHandle();

        uint style = NativeMethods.GetStyle(hwnd);
        style &= ~(NativeMethods.WS_POPUP | NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME
                   | NativeMethods.WS_SYSMENU | NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_MAXIMIZEBOX);
        style |= NativeMethods.WS_CHILD | NativeMethods.WS_CLIPSIBLINGS;
        NativeMethods.SetStyle(hwnd, style);
        ApplyToolWindowExStyle(hwnd);
        NativeMethods.SetRectRegion(hwnd, 0, 0);

        nint previous = NativeMethods.SetParent(hwnd, taskbar);
        int error = previous == 0 ? Marshal.GetLastPInvokeError() : 0;
        if (NativeMethods.GetAncestor(hwnd, NativeMethods.GA_PARENT) != taskbar)
        {
            _log.Warn($"taskbar host: SetParent did not take effect (error {error})");
            return false;
        }

        NativeMethods.SetWindowPos(hwnd, 0, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER
            | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
        window.Show();
        return true;
    }

    private void StartOverlay(TaskbarInfo taskbar)
    {
        WidgetWindow window = CreateWindowFor(taskbar);
        try
        {
            // Owner BEFORE Show(): WPF passes it to CreateWindowEx, so the window stays above the (topmost) taskbar.
            var helper = new WindowInteropHelper(window) { Owner = taskbar.Handle };
            nint hwnd = helper.EnsureHandle();
            ApplyToolWindowExStyle(hwnd);
            NativeMethods.SetRectRegion(hwnd, 0, 0);
            window.Show();
        }
        catch
        {
            CloseQuietly(window);
            throw;
        }

        if (Adopt(window, HostMode.Overlay, taskbar) && !_forceOverlay)
        {
            _retryTimer.Start();
        }
    }

    private static void ApplyToolWindowExStyle(nint hwnd)
    {
        // WS_EX_LAYERED is owned by WPF (AllowsTransparency): never touch it.
        uint exStyle = NativeMethods.GetExStyle(hwnd);
        exStyle = (exStyle | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE) & ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetExStyle(hwnd, exStyle);
    }

    /// <summary>
    /// Makes the window current, lays it out once and raises WindowCreated. False when that first Reconcile already
    /// replaced the window (e.g. requested a recreate because the taskbar DPI changed): no timer, no event.
    /// </summary>
    private bool Adopt(WidgetWindow window, HostMode mode, TaskbarInfo taskbar)
    {
        _window = window;
        _hwnd = new WindowInteropHelper(window).Handle;
        _mode = mode;
        _shown = false;
        _lastApplied = null;
        _reparentFailures = 0;
        _positionFailureLogged = false;
        window.Closed += OnWindowClosed;
        window.DpiChanged += OnWindowDpiChanged;

        if (mode == HostMode.Overlay)
        {
            _fullscreen.Start(taskbar.Handle);
        }
        else
        {
            _fullscreen.Stop();
            _retryTimer.Stop();
        }

        _log.Info($"taskbar host: widget attached in {mode} mode (hwnd=0x{(long)_hwnd:X}, taskbar=0x{(long)taskbar.Handle:X}, dpi={_baselineDpi}, monitor={taskbar.MonitorDeviceName ?? "?"})");
        Reconcile();
        if (!ReferenceEquals(_window, window))
        {
            // The first Reconcile requested a recreate (or switched mode) and this window is already closed.
            return false;
        }

        double wpfDpi = VisualTreeHelper.GetDpi(window).PixelsPerInchX;
        if (Math.Abs(wpfDpi - _baselineDpi) > 0.5)
        {
            _log.Warn($"taskbar host: widget renders at {wpfDpi} DPI but the taskbar is at {_baselineDpi} DPI");
        }

        _reconcileTimer.Start();
        if (IsSelectionFallback)
        {
            _selectionTimer.Start();
        }
        else
        {
            _selectionTimer.Stop();
        }

        RaiseWindowCreated(window);
        return true;
    }

    /// <summary>Each subscriber runs in isolation: a failing subscriber never tears down a healthy window.</summary>
    private void RaiseWindowCreated(WidgetWindow window)
    {
        if (WindowCreated is not EventHandler<WidgetWindow> handlers)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<WidgetWindow>)handler)(this, window);
            }
            catch (Exception ex)
            {
                _log.Error("taskbar host: a WindowCreated subscriber failed", ex);
            }
        }
    }

    private void TearDownWindow()
    {
        _reconcileTimer.Stop();
        _retryTimer.Stop();
        _selectionTimer.Stop();
        _fullscreen.Stop();

        WidgetWindow? window = _window;
        _window = null;
        _hwnd = 0;
        _mode = HostMode.None;
        _shown = false;
        _lastApplied = null;
        _reparentFailures = 0;
        if (window is null)
        {
            return;
        }

        window.Closed -= OnWindowClosed;
        window.DpiChanged -= OnWindowDpiChanged;
        CloseQuietly(window);
    }

    private void CloseQuietly(WidgetWindow window)
    {
        try
        {
            window.Close();
        }
        catch (Exception ex)
        {
            // Also used on failure paths where the window may be half-initialised: never let Close() escalate.
            _log.Warn("taskbar host: closing the widget window failed", ex);
        }
    }

    private void Reconcile()
    {
        if (_disposed || _window is null || _taskbar is null || _hwnd == 0)
        {
            return;
        }

        TaskbarInfo taskbar = _taskbar;
        nint tb = taskbar.Handle;
        if (!NativeMethods.IsWindow(tb))
        {
            RequestRecreate("taskbar window no longer exists", RecreateDelay);
            return;
        }

        if (!NativeMethods.IsWindow(_hwnd))
        {
            RequestRecreate("widget window was destroyed", RecreateDelay);
            return;
        }

        if (_mode == HostMode.Child)
        {
            uint dpi = NativeMethods.GetDpiForWindow(tb);
            if (dpi != _baselineDpi)
            {
                NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
                _shown = false;
                RequestRecreate($"taskbar DPI changed {_baselineDpi} -> {dpi}", RecreateDelay);
                return;
            }

            if (!EnsureParent(tb))
            {
                return;
            }
        }

        TaskbarGeometry? geometry = _geometry.GetGeometry();
        if (geometry is null)
        {
            Hide("taskbar geometry unavailable");
            return;
        }

        if (TaskbarLayoutCalculator.Compute(geometry, _host.Settings.Placement) is not PixelRect relative)
        {
            Hide(geometry.IsHorizontal ? "not enough room" : "vertical taskbar");
            return;
        }

        if (ShouldHideOverlay(geometry, taskbar))
        {
            Hide("fullscreen app or auto-hidden taskbar");
            return;
        }

        PixelRect screen = relative.Offset(geometry.Taskbar.Left, geometry.Taskbar.Top);
        PixelRect target = _mode == HostMode.Child ? relative : screen;
        PixelRect? actual = NativeMethods.TryGetWindowRect(_hwnd);
        if (_shown && _lastApplied == target && actual == screen)
        {
            return;
        }

        ApplyLayout(target);
    }

    /// <summary>The shell may reparent hosted windows (Start/Search). Re-attach; give up to overlay after 6 failures.</summary>
    private bool EnsureParent(nint taskbar)
    {
        if (NativeMethods.GetAncestor(_hwnd, NativeMethods.GA_PARENT) == taskbar)
        {
            _reparentFailures = 0;
            return true;
        }

        _reparentFailures++;
        _log.Warn($"taskbar host: widget was reparented; calling SetParent again ({_reparentFailures}/{MaxReparentFailures})");
        NativeMethods.SetParent(_hwnd, taskbar);
        if (NativeMethods.GetAncestor(_hwnd, NativeMethods.GA_PARENT) == taskbar)
        {
            _lastApplied = null;
            return true;
        }

        if (_reparentFailures >= MaxReparentFailures)
        {
            SwitchToOverlay("re-parenting keeps failing");
        }

        return false;
    }

    private bool ShouldHideOverlay(TaskbarGeometry geometry, TaskbarInfo taskbar)
    {
        if (_mode != HostMode.Overlay)
        {
            return false;
        }

        if (_fullscreen.IsFullscreenActive)
        {
            return true;
        }

        return _fullscreen.IsTaskbarAutoHide && !geometry.Taskbar.IsInside(taskbar.MonitorBounds);
    }

    private void ApplyLayout(PixelRect target)
    {
        WidgetWindow window = _window!;

        // Size the content in DIPs with the DPI WPF actually renders at; never touch Window.Width/Height after attach.
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        if (scale <= 0)
        {
            scale = Math.Max(1, _baselineDpi) / 96.0;
        }

        window.RootElement.Width = target.Width / scale;
        window.RootElement.Height = target.Height / scale;

        uint flags = NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW;
        if (_mode == HostMode.Overlay)
        {
            flags |= NativeMethods.SWP_NOZORDER; // an owned window always stays above its owner
        }

        if (!NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOP, target.Left, target.Top, target.Width, target.Height, flags))
        {
            if (!_positionFailureLogged)
            {
                _positionFailureLogged = true;
                _log.Warn($"taskbar host: SetWindowPos failed (error {Marshal.GetLastPInvokeError()})");
            }

            return;
        }

        NativeMethods.SetRectRegion(_hwnd, target.Width, target.Height);
        if (!_shown)
        {
            _log.Info($"taskbar host: widget shown at ({target.Left},{target.Top},{target.Right},{target.Bottom}) [{_mode}]");
        }

        _shown = true;
        _lastApplied = target;
    }

    private void Hide(string reason)
    {
        if (!_shown)
        {
            return;
        }

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        _shown = false;
        _lastApplied = null;
        _log.Info($"taskbar host: widget hidden ({reason})");
    }

    private void SwitchToOverlay(string reason)
    {
        TaskbarInfo? taskbar = _taskbar;
        if (taskbar is null)
        {
            return;
        }

        _log.Warn($"taskbar host: switching to overlay mode ({reason})");
        TearDownWindow();
        try
        {
            StartOverlay(taskbar);
        }
        catch (Exception ex)
        {
            // Called from the reconcile timer: never let this escape to the Dispatcher.
            _log.Error("taskbar host: starting the overlay window failed", ex);
            RequestRecreate("overlay start failed", AttachRetryInterval);
        }
    }

    /// <summary>Every 10 s in overlay mode: try a fresh child window; the overlay is closed only after success.</summary>
    private void RetryChildAttach()
    {
        if (_disposed || _mode != HostMode.Overlay || _taskbar is null || _forceOverlay)
        {
            _retryTimer.Stop();
            return;
        }

        TaskbarInfo taskbar = _taskbar;
        if (!NativeMethods.IsWindow(taskbar.Handle) || !TaskbarLocator.IsWin11Taskbar(taskbar.Handle))
        {
            return;
        }

        // Overlay mode never checks the taskbar DPI, so the baseline may be stale; Adopt's first Reconcile compares
        // against it and would otherwise recreate (and close) the window it has just adopted.
        _baselineDpi = NativeMethods.GetDpiForWindow(taskbar.Handle);
        if (TryCreateChild(taskbar) is not WidgetWindow candidate)
        {
            return;
        }

        _log.Info("taskbar host: child attach succeeded on retry; leaving overlay mode");
        TearDownWindow();
        Adopt(candidate, HostMode.Child, taskbar);
    }

    private async Task VerifyTaskbarSelectionAsync(string trigger)
    {
        int generation = _generation;
        TaskbarInfo? current = _taskbar;
        if (current is null || _selectionCheckRunning)
        {
            return; // a (re)creation or another check is already in progress
        }

        string? device = _requestedDevice;
        _selectionCheckRunning = true;
        try
        {
            (TaskbarInfo? found, _) = await Task.Run(() => LocateTaskbar(device));
            if (generation != _generation || _disposed || !ReferenceEquals(current, _taskbar))
            {
                return;
            }

            if (found is null || found.Handle != current.Handle)
            {
                RequestRecreate($"taskbar selection changed ({trigger})", RecreateDelay);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"taskbar host: taskbar selection check failed ({trigger})", ex);
        }
        finally
        {
            _selectionCheckRunning = false;
        }
    }

    private void ForceLayout()
    {
        _lastApplied = null;
        Reconcile();
    }

    private void OnReconcileTick(object? sender, EventArgs e) => Reconcile();

    private void OnRetryTick(object? sender, EventArgs e)
    {
        try
        {
            RetryChildAttach();
        }
        catch (Exception ex)
        {
            // DispatcherTimer tick: an escaping exception would be an unhandled Dispatcher exception.
            _log.Error("taskbar host: child attach retry failed", ex);
        }
    }

    /// <summary>Every 10 s while on a fallback taskbar: switch back once the selected monitor's taskbar is usable.</summary>
    private void OnSelectionTick(object? sender, EventArgs e)
    {
        if (!IsSelectionFallback)
        {
            _selectionTimer.Stop();
            return;
        }

        _ = VerifyTaskbarSelectionAsync("periodic fallback re-check");
    }

    private void OnTaskbarCreated(object? sender, EventArgs e) => RequestRecreate("TaskbarCreated", RecreateDelay);

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        _geometry.RequestRefresh();
        ForceLayout();
        _ = VerifyTaskbarSelectionAsync("display change");
    }

    private void OnTaskbarSettingsChanged(object? sender, EventArgs e)
    {
        _geometry.RequestRefresh();
        ForceLayout();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (!string.Equals(_host.Settings.MonitorDeviceName, _requestedDevice, StringComparison.OrdinalIgnoreCase))
        {
            RequestRecreate("monitor setting changed", TimeSpan.Zero);
            return;
        }

        ForceLayout();
    }

    private void OnFullscreenChanged(object? sender, EventArgs e) => Reconcile();

    private void OnWindowDpiChanged(object? sender, DpiChangedEventArgs e) => ForceLayout();

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not WidgetWindow window || !ReferenceEquals(window, _window))
        {
            return;
        }

        // Explorer destroyed our HWND (restart) or the owner went away: forget it without closing again.
        window.Closed -= OnWindowClosed;
        window.DpiChanged -= OnWindowDpiChanged;
        _window = null;
        _hwnd = 0;
        _mode = HostMode.None;
        _shown = false;
        _log.Warn("taskbar host: widget window was destroyed externally");
        RequestRecreate("widget window closed", RecreateDelay);
    }
}
