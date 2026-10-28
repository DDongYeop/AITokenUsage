using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AITokenUsage.Core.Taskbar;

namespace AITokenUsage.Taskbar;

/// <summary>
/// Finds explorer-owned Shell_TrayWnd / Shell_SecondaryTrayWnd windows (spec §4.1).
/// FindAll/Find send ABM_GETTASKBARPOS to explorer: call them from a background thread.
/// </summary>
public static class TaskbarLocator
{
    public const string PrimaryClass = "Shell_TrayWnd";
    public const string SecondaryClass = "Shell_SecondaryTrayWnd";
    public const string Win11BridgeClass = "Windows.UI.Composition.DesktopWindowContentBridge";

    /// <summary>Primary taskbar first, then secondary taskbars ordered by their left edge.</summary>
    public static IReadOnlyList<TaskbarInfo> FindAll()
    {
        var result = new List<TaskbarInfo>();
        foreach (nint hwnd in EnumerateExplorerWindows(PrimaryClass))
        {
            if (Describe(hwnd, isPrimary: true) is TaskbarInfo primary)
            {
                result.Add(primary);
                break;
            }
        }

        var secondaries = new List<TaskbarInfo>();
        foreach (nint hwnd in EnumerateExplorerWindows(SecondaryClass))
        {
            if (Describe(hwnd, isPrimary: false) is TaskbarInfo secondary)
            {
                secondaries.Add(secondary);
            }
        }

        result.AddRange(secondaries.OrderBy(t => t.Bounds.Left));
        return result;
    }

    /// <summary>The taskbar on the given monitor (e.g. @"\\.\DISPLAY2"); null or unknown device → primary taskbar.</summary>
    public static TaskbarInfo? Find(string? monitorDeviceName)
    {
        IReadOnlyList<TaskbarInfo> all = FindAll();
        if (!string.IsNullOrWhiteSpace(monitorDeviceName))
        {
            foreach (TaskbarInfo taskbar in all)
            {
                if (string.Equals(taskbar.MonitorDeviceName, monitorDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    return taskbar;
                }
            }
        }

        return all.FirstOrDefault(t => t.IsPrimary) ?? all.FirstOrDefault();
    }

    /// <summary>All top-level windows of the class owned by explorer.exe (replacement shells reuse the class name).</summary>
    public static IEnumerable<nint> EnumerateExplorerWindows(string className)
    {
        for (nint hwnd = NativeMethods.FindWindowEx(0, 0, className, null);
             hwnd != 0;
             hwnd = NativeMethods.FindWindowEx(0, hwnd, className, null))
        {
            if (IsExplorerWindow(hwnd))
            {
                yield return hwnd;
            }
        }
    }

    public static bool IsExplorerWindow(nint hwnd)
    {
        if (NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId) == 0 || processId == 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>The Windows 11 XAML taskbar hosts a DesktopWindowContentBridge child.</summary>
    public static bool IsWin11Taskbar(nint taskbar) =>
        NativeMethods.FindWindowEx(taskbar, 0, Win11BridgeClass, null) != 0;

    public static string? GetMonitorDeviceName(nint hwnd) =>
        NativeMethods.TryGetMonitorDetails(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST))?.DeviceName;

    /// <summary>Edge from geometry alone: horizontal bars are Top/Bottom, vertical bars Left/Right.</summary>
    public static TaskbarEdge EdgeFromRect(PixelRect bounds, PixelRect monitor)
    {
        if (bounds.Width >= bounds.Height)
        {
            return bounds.Top - monitor.Top <= monitor.Bottom - bounds.Bottom ? TaskbarEdge.Top : TaskbarEdge.Bottom;
        }

        return bounds.Left - monitor.Left <= monitor.Right - bounds.Right ? TaskbarEdge.Left : TaskbarEdge.Right;
    }

    private static TaskbarInfo? Describe(nint hwnd, bool isPrimary)
    {
        if (NativeMethods.TryGetWindowRect(hwnd) is not PixelRect bounds || bounds.IsEmpty)
        {
            return null;
        }

        nint monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        NativeMethods.MonitorDetails? details = NativeMethods.TryGetMonitorDetails(monitor);
        PixelRect monitorBounds = details?.Bounds ?? bounds;
        TaskbarEdge edge = DetectEdge(hwnd, bounds, monitorBounds, isPrimary);
        return new TaskbarInfo(
            hwnd,
            isPrimary,
            bounds,
            NativeMethods.GetDpiForWindow(hwnd),
            details?.DeviceName,
            monitorBounds,
            IsWin11Taskbar(hwnd),
            edge);
    }

    /// <summary>ABM_GETTASKBARPOS (primary only) cross-checked with the rect aspect; the aspect wins on disagreement.</summary>
    private static TaskbarEdge DetectEdge(nint hwnd, PixelRect bounds, PixelRect monitor, bool isPrimary)
    {
        TaskbarEdge byRect = EdgeFromRect(bounds, monitor);
        if (!isPrimary)
        {
            return byRect;
        }

        var data = new NativeMethods.APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            hWnd = hwnd,
        };
        if (NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETTASKBARPOS, ref data) == 0 || data.uEdge > 3)
        {
            return byRect;
        }

        var reported = (TaskbarEdge)data.uEdge;
        bool reportedHorizontal = reported is TaskbarEdge.Top or TaskbarEdge.Bottom;
        bool rectHorizontal = bounds.Width >= bounds.Height;
        return reportedHorizontal == rectHorizontal ? reported : byRect;
    }
}
