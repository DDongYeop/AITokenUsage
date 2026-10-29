using System.Globalization;
using System.Windows;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Util;
using AITokenUsage.Resources;
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

        // A restart (language change) starts the new instance while the old one is still shutting down (it releases the
        // mutex only after AppHost.DisposeAsync): wait for it, and never signal that exiting instance on timeout.
        bool restarted = e.Args.Contains(LanguageBootstrap.RestartedArgument);
        _instance = restarted
            ? SingleInstance.TryAcquire(TimeSpan.FromSeconds(10), signalFirstInstanceOnTimeout: false)
            : SingleInstance.TryAcquire();
        if (_instance is null)
        {
            if (restarted)
            {
                LogRestartTimeout();
            }

            // Otherwise another instance owns the mutex and has been signalled to open its settings window.
            Shutdown(0);
            return;
        }

        try
        {
            // AppHost applies the UI language itself (LanguageBootstrap.Apply) right after loading the settings.
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

        // Windows logoff/shutdown (SessionEnding) and failed startups reach here without the tray's Exit path:
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

    private static void LogRestartTimeout()
    {
        try
        {
            // No AppHost yet: open the log just for this line; Dispose flushes the background queue.
            using var log = new FileAppLog(AppPaths.LogDirectory);
            log.Warn("restarted instance gave up waiting for the previous instance to release the single-instance mutex; exiting");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing else we can do; the process exits right after.
        }
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
            string.Format(CultureInfo.CurrentCulture, UiStrings.StartupFailedMessage, AppPaths.LogDirectory),
            TrayTextFormatter.AppTitle,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
