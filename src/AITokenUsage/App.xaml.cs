using System.Windows;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.UI;

namespace AITokenUsage;

public partial class App : Application
{
    private SingleInstance? _instance;
    private AppHost? _host;
    private TrayIconController? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = SingleInstance.TryAcquire();
        if (_instance is null)
        {
            // Another instance owns the mutex and has been signalled to open its settings window.
            Shutdown(0);
            return;
        }

        try
        {
            _host = new AppHost(Dispatcher);
            HookGlobalExceptionHandlers(_host.Log);
            _instance.StartListening(Dispatcher, _host.OpenSettings);
            _tray = new TrayIconController(_host);
            _host.Start();
        }
        catch (Exception ex)
        {
            // Covers every startup step: a throw here must not leave a process holding the mutex without a tray.
            ReportStartupFailure(ex, _host);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();

        // Windows logoff/shutdown (SessionEnding) and failed startups reach here without the tray's 종료 path:
        // finish the pending settings save and flush the log. No-op after the normal AppHost.DisposeAsync.
        _host?.FlushPendingWork();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void HookGlobalExceptionHandlers(IAppLog log)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            log.Error("unhandled exception on the UI thread", args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            log.Warn("unobserved task exception", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            log.Error($"fatal unhandled exception (terminating: {args.IsTerminating})", args.ExceptionObject as Exception);

            // FileAppLog writes on a background queue; a terminating crash would lose this line without a flush.
            (log as FileAppLog)?.Flush();
        };
    }

    private static void ReportStartupFailure(Exception ex, AppHost? host)
    {
        try
        {
            if (host is not null)
            {
                // The host already owns the log file; reuse its log instead of opening a second writer.
                host.Log.Error("startup failed", ex);
                host.FlushPendingWork();
            }
            else
            {
                // FileAppLog writes on a background queue; Dispose flushes so the line lands before the process exits.
                using var log = new FileAppLog(AppPaths.LogDirectory);
                log.Error("startup failed", ex);
            }
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
        {
            // Nothing else we can do; the message box below still tells the user.
        }

        MessageBox.Show(
            $"AI 사용량 위젯을 시작하지 못했습니다.\n로그: {AppPaths.LogDirectory}",
            "AI 사용량",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
