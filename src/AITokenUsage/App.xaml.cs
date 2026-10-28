using System.Windows;

namespace AITokenUsage;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // The composition root (AppHost, tray icon, taskbar host) is added in Task 14.
        // With ShutdownMode=OnExplicitShutdown the process keeps running without any window.
        base.OnStartup(e);
    }
}