using System.Runtime.InteropServices;
using AITokenUsage.Core.Taskbar;

namespace AITokenUsage.Taskbar;

/// <summary>Win32 interop for taskbar embedding (research win11-taskbar-embedding.md §6). x64 only.</summary>
internal static unsafe partial class NativeMethods
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const uint WS_POPUP = 0x80000000;
    public const uint WS_CHILD = 0x40000000;
    public const uint WS_CLIPSIBLINGS = 0x04000000;
    public const uint WS_CAPTION = 0x00C00000;
    public const uint WS_SYSMENU = 0x00080000;
    public const uint WS_THICKFRAME = 0x00040000;
    public const uint WS_MINIMIZEBOX = 0x00020000;
    public const uint WS_MAXIMIZEBOX = 0x00010000;

    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_APPWINDOW = 0x00040000;
    public const uint WS_EX_NOACTIVATE = 0x08000000;

    public const nint HWND_TOP = 0;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public const uint GA_PARENT = 1;
    public const int SW_HIDE = 0;

    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_DPICHANGED = 0x02E0;
    public const uint SPI_SETWORKAREA = 0x002F;
    public const uint MSGFLT_ALLOW = 1;

    public const uint ABM_GETSTATE = 4;
    public const uint ABM_GETTASKBARPOS = 5;
    public const uint ABS_AUTOHIDE = 1;

    public const int QUNS_BUSY = 2;
    public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    public const int QUNS_PRESENTATION_MODE = 4;
    public const int QUNS_ACCEPTS_NOTIFICATIONS = 5;

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint MONITORINFOF_PRIMARY = 1;
    public const uint DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly PixelRect ToPixelRect() => new(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public nint lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFOEXW
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        public fixed ushort szDevice[32];
    }

    public readonly record struct MonitorDetails(PixelRect Bounds, PixelRect WorkArea, bool IsPrimary, string DeviceName);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForSystem();

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint monitor, ref MONITORINFOEXW info);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetParent(nint child, nint newParent);

    [LibraryImport("user32.dll")]
    public static partial nint GetAncestor(nint hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hwnd, int command);

    /// <summary>On success the system owns the region; on failure the caller must DeleteObject it.</summary>
    [LibraryImport("user32.dll")]
    public static partial int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint gdiObject);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessage(string name);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ChangeWindowMessageFilterEx(nint hwnd, uint message, uint action, nint changeFilterStruct);

    /// <summary>Sends a message to the taskbar: call only from background threads.</summary>
    [LibraryImport("shell32.dll")]
    public static partial nuint SHAppBarMessage(uint message, ref APPBARDATA data);

    [LibraryImport("shell32.dll")]
    public static partial int SHQueryUserNotificationState(out int state);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial nint GetDesktopWindow();

    [LibraryImport("user32.dll")]
    public static partial nint GetShellWindow();

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, uint size);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static partial int GetClassNameNative(nint hwnd, char* buffer, int maxCount);

    public static PixelRect? TryGetWindowRect(nint hwnd) =>
        hwnd != 0 && GetWindowRect(hwnd, out RECT rect) ? rect.ToPixelRect() : null;

    /// <summary>The cursor position in physical screen pixels, or null when it cannot be read (e.g. secure desktop).</summary>
    public static PixelPoint? TryGetCursorPos() =>
        GetCursorPos(out POINT point) ? new PixelPoint(point.X, point.Y) : null;

    public static MonitorDetails? TryGetMonitorDetails(nint monitor)
    {
        if (monitor == 0)
        {
            return null;
        }

        var info = new MONITORINFOEXW { cbSize = (uint)sizeof(MONITORINFOEXW) };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return null;
        }

        string device = new((char*)info.szDevice);
        return new MonitorDetails(
            info.rcMonitor.ToPixelRect(),
            info.rcWork.ToPixelRect(),
            (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
            device);
    }

    public static string GetClassName(nint hwnd)
    {
        char* buffer = stackalloc char[256];
        int length = GetClassNameNative(hwnd, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    public static uint GetStyle(nint hwnd) => unchecked((uint)(long)GetWindowLongPtr(hwnd, GWL_STYLE));

    public static uint GetExStyle(nint hwnd) => unchecked((uint)(long)GetWindowLongPtr(hwnd, GWL_EXSTYLE));

    public static void SetStyle(nint hwnd, uint style) => SetWindowLongPtr(hwnd, GWL_STYLE, (nint)style);

    public static void SetExStyle(nint hwnd, uint exStyle) => SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)exStyle);

    /// <summary>Clips the window to (0,0,width,height); width/height 0 collapses it.</summary>
    public static void SetRectRegion(nint hwnd, int width, int height)
    {
        nint region = CreateRectRgn(0, 0, width, height);
        if (region != 0 && SetWindowRgn(hwnd, region, true) == 0)
        {
            DeleteObject(region);
        }
    }
}
