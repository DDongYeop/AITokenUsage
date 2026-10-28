using System.Runtime.InteropServices;
using System.Windows.Threading;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Taskbar;

namespace AITokenUsage.Taskbar;

/// <summary>
/// Overlay-mode helper (spec §4.5): fullscreen detection and taskbar auto-hide state, polled every 2 s on the
/// thread pool. Properties change and Changed is raised on the UI thread.
/// </summary>
public sealed class FullscreenWatcher : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly Dispatcher _dispatcher;
    private readonly IAppLog _log;
    private readonly Timer _timer;
    private nint _taskbar;
    private int _polling;
    private int _disposed;

    public FullscreenWatcher(Dispatcher dispatcher, IAppLog log)
    {
        _dispatcher = dispatcher;
        _log = log;
        _timer = new Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsFullscreenActive { get; private set; }

    public bool IsTaskbarAutoHide { get; private set; }

    public event EventHandler? Changed;

    /// <summary>UI thread. Starts polling for the monitor of the given taskbar.</summary>
    public void Start(nint taskbar)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        Volatile.Write(ref _taskbar, taskbar);
        _timer.Change(TimeSpan.Zero, PollInterval);
    }

    /// <summary>UI thread. Stops polling and clears the state.</summary>
    public void Stop()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        Volatile.Write(ref _taskbar, 0);
        IsFullscreenActive = false;
        IsTaskbarAutoHide = false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _timer.Dispose();
    }

    private void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1)
        {
            return;
        }

        try
        {
            nint taskbar = Volatile.Read(ref _taskbar);
            if (taskbar == 0 || Volatile.Read(ref _disposed) == 1 || _dispatcher.HasShutdownStarted)
            {
                return;
            }

            bool fullscreen = IsNotificationStateBusy() || ForegroundCoversMonitor(taskbar);
            bool autoHide = IsAutoHideEnabled();
            if (fullscreen == IsFullscreenActive && autoHide == IsTaskbarAutoHide)
            {
                return;
            }

            _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => Apply(taskbar, fullscreen, autoHide)));
        }
        catch (Exception ex)
        {
            _log.Warn("fullscreen poll failed", ex);
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private void Apply(nint taskbar, bool fullscreen, bool autoHide)
    {
        if (Volatile.Read(ref _disposed) == 1 || Volatile.Read(ref _taskbar) != taskbar)
        {
            return;
        }

        if (fullscreen == IsFullscreenActive && autoHide == IsTaskbarAutoHide)
        {
            return;
        }

        IsFullscreenActive = fullscreen;
        IsTaskbarAutoHide = autoHide;
        _log.Info($"overlay state: fullscreen={fullscreen}, autoHide={autoHide}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Machine-wide QUNS_BUSY / QUNS_RUNNING_D3D_FULL_SCREEN / QUNS_PRESENTATION_MODE.</summary>
    private static bool IsNotificationStateBusy()
    {
        int hr = NativeMethods.SHQueryUserNotificationState(out int state);
        return hr == 0 && state is NativeMethods.QUNS_BUSY or NativeMethods.QUNS_RUNNING_D3D_FULL_SCREEN or NativeMethods.QUNS_PRESENTATION_MODE;
    }

    private static bool IsAutoHideEnabled()
    {
        var data = new NativeMethods.APPBARDATA { cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>() };
        return ((uint)NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETSTATE, ref data) & NativeMethods.ABS_AUTOHIDE) != 0;
    }

    /// <summary>TrafficMonitor-style per-monitor check plus the ManagedShell exclusions that matter on Windows 11.</summary>
    private static bool ForegroundCoversMonitor(nint taskbar)
    {
        nint foreground = NativeMethods.GetForegroundWindow();
        if (foreground == 0 || foreground == NativeMethods.GetDesktopWindow() || foreground == NativeMethods.GetShellWindow())
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(foreground, out uint processId);
        if (processId == (uint)Environment.ProcessId)
        {
            return false;
        }

        string className = NativeMethods.GetClassName(foreground);
        if (className is "WorkerW" or "Progman" or TaskbarLocator.PrimaryClass or TaskbarLocator.SecondaryClass)
        {
            return false;
        }

        if (!NativeMethods.IsWindowVisible(foreground) || NativeMethods.IsIconic(foreground))
        {
            return false;
        }

        if (NativeMethods.DwmGetWindowAttribute(foreground, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return false;
        }

        nint monitor = NativeMethods.MonitorFromWindow(taskbar, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (NativeMethods.MonitorFromWindow(foreground, NativeMethods.MONITOR_DEFAULTTONEAREST) != monitor)
        {
            return false;
        }

        if (NativeMethods.TryGetMonitorDetails(monitor) is not NativeMethods.MonitorDetails details
            || NativeMethods.TryGetWindowRect(foreground) is not PixelRect rect)
        {
            return false;
        }

        PixelRect screen = details.Bounds;
        return rect.Left <= screen.Left && rect.Top <= screen.Top && rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;
    }
}
