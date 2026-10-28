using System.Runtime.InteropServices;
using System.Windows.Interop;
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Taskbar;

/// <summary>
/// Hidden top-level WS_POPUP window that receives shell broadcasts (spec §4.4). Create on the UI thread;
/// events are raised on the UI thread.
/// </summary>
public sealed class ShellEventWindow : IDisposable
{
    public const string WindowName = "AITokenUsage.ShellEvents";

    private readonly IAppLog _log;
    private readonly HwndSource _source;
    private readonly HwndSourceHook _hook;
    private readonly uint _taskbarCreatedMessage;
    private bool _disposed;

    public ShellEventWindow(IAppLog log)
    {
        _log = log;
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");

        var parameters = new HwndSourceParameters(WindowName)
        {
            WindowStyle = unchecked((int)NativeMethods.WS_POPUP),
            ExtendedWindowStyle = (int)NativeMethods.WS_EX_TOOLWINDOW,
            ParentWindow = 0,
            PositionX = 0,
            PositionY = 0,
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(parameters);
        _hook = WndProc;
        _source.AddHook(_hook);

        if (_taskbarCreatedMessage != 0)
        {
            NativeMethods.ChangeWindowMessageFilterEx(_source.Handle, _taskbarCreatedMessage, NativeMethods.MSGFLT_ALLOW, 0);
        }

        _log.Info($"shell event window created (hwnd=0x{(long)_source.Handle:X})");
    }

    public nint Handle => _source.Handle;

    /// <summary>Explorer restarted (or the primary display DPI changed).</summary>
    public event EventHandler? TaskbarCreated;

    /// <summary>WM_DISPLAYCHANGE or WM_DPICHANGED.</summary>
    public event EventHandler? DisplayChanged;

    /// <summary>WM_SETTINGCHANGE with lParam "ImmersiveColorSet" (may arrive several times; handle idempotently).</summary>
    public event EventHandler? ImmersiveColorSetChanged;

    /// <summary>WM_SETTINGCHANGE "TraySettings" or SPI_SETWORKAREA: taskbar alignment/size may have changed.</summary>
    public event EventHandler? TaskbarSettingsChanged;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.RemoveHook(_hook);
        _source.Dispose();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && msg == (int)_taskbarCreatedMessage)
        {
            _log.Info("shell: TaskbarCreated");
            Raise(TaskbarCreated);
        }
        else if (msg is NativeMethods.WM_DISPLAYCHANGE or NativeMethods.WM_DPICHANGED)
        {
            _log.Info(msg == NativeMethods.WM_DISPLAYCHANGE ? "shell: display changed (WM_DISPLAYCHANGE)" : "shell: display changed (WM_DPICHANGED)");
            Raise(DisplayChanged);
        }
        else if (msg == NativeMethods.WM_SETTINGCHANGE)
        {
            string? area = lParam != 0 ? Marshal.PtrToStringUni(lParam) : null;
            if (string.Equals(area, "ImmersiveColorSet", StringComparison.OrdinalIgnoreCase))
            {
                _log.Info("shell: ImmersiveColorSet");
                Raise(ImmersiveColorSetChanged);
            }
            else if (string.Equals(area, "TraySettings", StringComparison.OrdinalIgnoreCase)
                     || unchecked((uint)(long)wParam) == NativeMethods.SPI_SETWORKAREA)
            {
                _log.Info("shell: taskbar settings changed");
                Raise(TaskbarSettingsChanged);
            }
        }

        return 0;
    }

    private void Raise(EventHandler? handler)
    {
        if (_disposed || handler is null)
        {
            return;
        }

        try
        {
            handler(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Error("shell event handler failed", ex);
        }
    }
}
