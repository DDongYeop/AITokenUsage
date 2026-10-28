using System.Security;
using System.Windows.Automation;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Taskbar;
using Microsoft.Win32;

namespace AITokenUsage.Taskbar;

/// <summary>
/// Taskbar geometry for TaskbarLayoutCalculator (spec §4.3). UI Automation runs only on a dedicated background
/// MTA thread (every 3 s or on request); GetGeometry() combines that cache with cheap HWND rects on the UI thread.
/// </summary>
public sealed class TaskbarGeometryReader : IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MinAutoRequestSpacing = TimeSpan.FromSeconds(1);
    public const int StartMoveTolerancePx = 2;

    private const string AdvancedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string TaskListButtonClass = "Taskbar.TaskListButtonAutomationPeer";
    private static readonly TimeSpan FailureLogSpacing = TimeSpan.FromSeconds(60);

    private readonly IAppLog _log;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEvent _stop = new(false);
    private nint _taskbar;
    private UiaSnapshot? _snapshot;
    private volatile bool _isCentered = true;
    private volatile bool _widgetsEnabled = true;
    private long _lastAutoRequestMs = long.MinValue / 2;
    private long _lastFailureLogMs = long.MinValue / 2;
    private int _disposed;

    public TaskbarGeometryReader(IAppLog log)
    {
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "AITokenUsage.TaskbarUIA" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>True once UIA reported the Start button or app buttons for the current taskbar.</summary>
    public bool IsLayoutReady
    {
        get
        {
            UiaSnapshot? snapshot = Volatile.Read(ref _snapshot);
            return snapshot is not null
                && snapshot.Taskbar == Volatile.Read(ref _taskbar)
                && (snapshot.Start is not null || snapshot.AppButtons is not null);
        }
    }

    /// <summary>Switches the target taskbar, drops the old cache and wakes the UIA thread.</summary>
    public void SetTaskbar(nint taskbar)
    {
        Volatile.Write(ref _taskbar, taskbar);
        Volatile.Write(ref _snapshot, null);
        RequestRefresh();
    }

    public void RequestRefresh()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _wake.Set();
        }
    }

    /// <summary>UI thread. Never sends messages to explorer; uses the cached UIA snapshot.</summary>
    public TaskbarGeometry? GetGeometry()
    {
        nint taskbar = Volatile.Read(ref _taskbar);
        if (taskbar == 0 || NativeMethods.TryGetWindowRect(taskbar) is not PixelRect bounds || bounds.IsEmpty)
        {
            return null;
        }

        uint dpi = NativeMethods.GetDpiForWindow(taskbar);
        UiaSnapshot? snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is null || snapshot.Taskbar != taskbar || snapshot.TaskbarRect != bounds)
        {
            RequestRefreshThrottled();
            if (snapshot is not null
                && snapshot.Taskbar == taskbar
                && snapshot.TaskbarRect.Width == bounds.Width
                && snapshot.TaskbarRect.Height == bounds.Height)
            {
                // Same size, new position (auto-hide slide): the buttons moved with the bar, so keep the snapshot
                // shifted by the same offset instead of dropping Widgets/AppButtons until the next UIA pass.
                snapshot = snapshot.Translate(bounds.Left - snapshot.TaskbarRect.Left, bounds.Top - snapshot.TaskbarRect.Top);
            }
            else
            {
                snapshot = null;
            }
        }

        PixelRect? start = snapshot?.Start;
        if (ValidChildRect(taskbar, "Start", bounds) is PixelRect cheapStart)
        {
            if (start is PixelRect uiaStart && Math.Abs(uiaStart.Left - cheapStart.Left) > StartMoveTolerancePx)
            {
                // The centered group moved (apps opened/closed): trust the hidden Start HWND now, refresh UIA soon.
                RequestRefreshThrottled();
                start = cheapStart;
            }

            start ??= cheapStart;
        }

        PixelRect? tray = snapshot?.Tray ?? ValidChildRect(taskbar, "TrayNotifyWnd", bounds);
        PixelRect? widgets = _widgetsEnabled ? snapshot?.Widgets : null;
        return new TaskbarGeometry(
            bounds,
            dpi,
            bounds.Width > bounds.Height,
            _isCentered,
            start,
            widgets,
            snapshot?.AppButtons,
            tray);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // The UIA thread disposes the wait handles itself when it exits; never block the UI thread on it
        // (it may be inside a UIA call that needs this UI thread).
        _stop.Set();
    }

    private void Run()
    {
        WaitHandle[] handles = [_stop, _wake];
        try
        {
            while (true)
            {
                nint taskbar = Volatile.Read(ref _taskbar);
                if (taskbar != 0)
                {
                    ReadRegistry();
                    UiaSnapshot? snapshot = ReadSnapshot(taskbar);
                    if (snapshot is not null && Volatile.Read(ref _taskbar) == taskbar)
                    {
                        Volatile.Write(ref _snapshot, snapshot);
                    }
                }

                if (WaitHandle.WaitAny(handles, RefreshInterval) == 0)
                {
                    return;
                }
            }
        }
        finally
        {
            _wake.Dispose();
            _stop.Dispose();
        }
    }

    private UiaSnapshot? ReadSnapshot(nint taskbar)
    {
        try
        {
            if (!NativeMethods.IsWindow(taskbar)
                || NativeMethods.TryGetWindowRect(taskbar) is not PixelRect bounds
                || bounds.IsEmpty)
            {
                return null;
            }

            PixelRect? start = null;
            PixelRect? widgets = null;
            PixelRect? tray = null;
            var appButtons = new List<PixelRect>();
            int ownProcessId = Environment.ProcessId;

            var cache = new CacheRequest { AutomationElementMode = AutomationElementMode.None, TreeScope = TreeScope.Element };
            cache.Add(AutomationElement.AutomationIdProperty);
            cache.Add(AutomationElement.ClassNameProperty);
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.IsOffscreenProperty);
            cache.Add(AutomationElement.ProcessIdProperty);

            // The root must be a live element (mode Full) so that FindAll can run on it.
            AutomationElement root = AutomationElement.FromHandle(taskbar);
            AutomationElementCollection elements;
            using (cache.Activate())
            {
                elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            }

            foreach (AutomationElement element in elements)
            {
                AutomationElement.AutomationElementInformation info = element.Cached;
                if (info.ProcessId == ownProcessId || info.IsOffscreen)
                {
                    continue;
                }

                if (ToPixelRect(info.BoundingRectangle) is not PixelRect rect || !IsValid(rect, bounds))
                {
                    continue;
                }

                switch (info.AutomationId)
                {
                    case "StartButton":
                        start = rect;
                        break;
                    case "WidgetsButton":
                        widgets = rect;
                        break;
                    case "SystemTrayIcon":
                    case "NotifyItemIcon":
                        tray = Union(tray, rect);
                        break;
                    default:
                        if (string.Equals(info.ClassName, TaskListButtonClass, StringComparison.Ordinal))
                        {
                            appButtons.Add(rect);
                        }

                        break;
                }
            }

            // Only the contiguous group right of Start; a far-away button (overflow etc.) must not widen AppButtons.
            uint dpi = NativeMethods.GetDpiForWindow(taskbar);
            int maxGap = (int)Math.Round(TaskbarButtonGrouping.MaxGapAt96Dpi * (dpi == 0 ? 96 : dpi) / 96.0);
            PixelRect? apps = TaskbarButtonGrouping.GroupContiguous(appButtons, start?.Right, maxGap);

            return new UiaSnapshot(taskbar, bounds, start, widgets, apps, tray);
        }
        catch (Exception ex)
        {
            LogFailure(ex);
            return null;
        }
    }

    private void ReadRegistry()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(AdvancedKeyPath);
            _isCentered = key?.GetValue("TaskbarAl") is not int alignment || alignment != 0;
            _widgetsEnabled = key?.GetValue("TaskbarDa") is not int widgets || widgets != 0;
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            LogFailure(ex);
        }
    }

    private void RequestRefreshThrottled()
    {
        long now = Environment.TickCount64;
        if (now - _lastAutoRequestMs < (long)MinAutoRequestSpacing.TotalMilliseconds)
        {
            return;
        }

        _lastAutoRequestMs = now;
        RequestRefresh();
    }

    private void LogFailure(Exception ex)
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastFailureLogMs) < (long)FailureLogSpacing.TotalMilliseconds)
        {
            return;
        }

        Interlocked.Exchange(ref _lastFailureLogMs, now);
        _log.Warn("taskbar UIA read failed (HWND fallbacks are used)", ex);
    }

    private static PixelRect? ValidChildRect(nint taskbar, string className, PixelRect bounds)
    {
        nint child = NativeMethods.FindWindowEx(taskbar, 0, className, null);
        return NativeMethods.TryGetWindowRect(child) is PixelRect rect && IsValid(rect, bounds) ? rect : null;
    }

    private static bool IsValid(PixelRect rect, PixelRect taskbar) => !rect.IsEmpty && rect.IsInside(taskbar);

    private static PixelRect? ToPixelRect(System.Windows.Rect rect)
    {
        if (rect.IsEmpty || double.IsInfinity(rect.Width) || double.IsInfinity(rect.Height) || rect.Width <= 0 || rect.Height <= 0)
        {
            return null;
        }

        return new PixelRect(
            (int)Math.Round(rect.Left),
            (int)Math.Round(rect.Top),
            (int)Math.Round(rect.Right),
            (int)Math.Round(rect.Bottom));
    }

    private static PixelRect Union(PixelRect? current, PixelRect add) => current is PixelRect c
        ? new PixelRect(Math.Min(c.Left, add.Left), Math.Min(c.Top, add.Top), Math.Max(c.Right, add.Right), Math.Max(c.Bottom, add.Bottom))
        : add;

    private sealed record UiaSnapshot(
        nint Taskbar,
        PixelRect TaskbarRect,
        PixelRect? Start,
        PixelRect? Widgets,
        PixelRect? AppButtons,
        PixelRect? Tray)
    {
        public UiaSnapshot Translate(int dx, int dy) => new(
            Taskbar,
            Shift(TaskbarRect, dx, dy),
            Shift(Start, dx, dy),
            Shift(Widgets, dx, dy),
            Shift(AppButtons, dx, dy),
            Shift(Tray, dx, dy));

        private static PixelRect Shift(PixelRect r, int dx, int dy) => new(r.Left + dx, r.Top + dy, r.Right + dx, r.Bottom + dy);

        private static PixelRect? Shift(PixelRect? r, int dx, int dy) => r is PixelRect rect ? Shift(rect, dx, dy) : null;
    }
}
