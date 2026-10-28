using System.Security;
using System.Windows.Threading;
using AITokenUsage.Core.Abstractions;
using Microsoft.Win32;

namespace AITokenUsage.Taskbar;

/// <summary>
/// Taskbar (SystemUsesLightTheme) and app (AppsUseLightTheme) light/dark mode (spec §4.6).
/// Updated on WM_SETTINGCHANGE "ImmersiveColorSet" (via Refresh) and by a 2 s registry poll.
/// Properties change and Changed is raised on the UI thread only.
/// </summary>
public sealed class ThemeWatcher : IDisposable
{
    public const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly Dispatcher _dispatcher;
    private readonly IAppLog _log;
    private readonly Timer _timer;
    private int _disposed;

    public ThemeWatcher(Dispatcher dispatcher, IAppLog log)
    {
        _dispatcher = dispatcher;
        _log = log;
        (IsTaskbarLight, IsAppLight) = ReadTheme();
        _timer = new Timer(_ => Poll(), null, PollInterval, PollInterval);
    }

    /// <summary>True when the taskbar is light (SystemUsesLightTheme = 1). Absent or 0 → dark.</summary>
    public bool IsTaskbarLight { get; private set; }

    /// <summary>True when apps use the light theme (AppsUseLightTheme ≠ 0). Absent → light (Windows default).</summary>
    public bool IsAppLight { get; private set; }

    public event EventHandler? Changed;

    /// <summary>Re-reads the registry on a pool thread (call on ImmersiveColorSet).</summary>
    public void Refresh()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            ThreadPool.QueueUserWorkItem(_ => Poll());
        }
    }

    public static (bool TaskbarLight, bool AppLight) ReadTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            bool taskbarLight = key?.GetValue("SystemUsesLightTheme") is int system && system != 0;
            bool appLight = key?.GetValue("AppsUseLightTheme") is not int apps || apps != 0;
            return (taskbarLight, appLight);
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            return (false, true);
        }
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
        try
        {
            if (Volatile.Read(ref _disposed) == 1 || _dispatcher.HasShutdownStarted)
            {
                return;
            }

            (bool taskbarLight, bool appLight) = ReadTheme();
            if (taskbarLight == IsTaskbarLight && appLight == IsAppLight)
            {
                return;
            }

            _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => Apply(taskbarLight, appLight)));
        }
        catch (Exception ex)
        {
            _log.Warn("theme poll failed", ex);
        }
    }

    private void Apply(bool taskbarLight, bool appLight)
    {
        if (Volatile.Read(ref _disposed) == 1 || (taskbarLight == IsTaskbarLight && appLight == IsAppLight))
        {
            return;
        }

        IsTaskbarLight = taskbarLight;
        IsAppLight = appLight;
        _log.Info($"theme changed: taskbarLight={taskbarLight}, appLight={appLight}");
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
