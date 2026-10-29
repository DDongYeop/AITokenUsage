using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Polling;
using AITokenUsage.Core.Providers;
using AITokenUsage.Core.Rotation;
using AITokenUsage.Core.Settings;
using AITokenUsage.Core.SystemUsage;
using AITokenUsage.Taskbar;
using AITokenUsage.UI;
using AITokenUsage.UI.Settings;

namespace AITokenUsage;

/// <summary>
/// Composition root. Owns settings, providers, polling, rotation and the app-level services.
/// Members are meant to be used on the UI thread unless stated otherwise.
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    public static readonly TimeSpan RotationTickInterval = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan PendingWorkTimeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private readonly DispatcherTimer _rotationTimer;
    private readonly ProviderPathWatcher _pathWatcher;
    private readonly SystemUsageSampler _systemSampler;
    private Task _backgroundChain = Task.CompletedTask;
    private int _refreshMinutes;
    private bool _started;
    private bool _shutdownRequested;
    private int _disposed;

    public AppHost(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        Dispatcher = dispatcher;

        Log = new FileAppLog(AppPaths.LogDirectory);
        Log.Info($"AITokenUsage {typeof(AppHost).Assembly.GetName().Version} starting (pid {Environment.ProcessId})");

        // Startup only: settings are needed synchronously, before anything is attached to the taskbar.
        SettingsStore = new SettingsStore(AppPaths.SettingsFile, Log);
        Settings = SettingsStore.Load();

        // Before any localized string is created below (UsageService seeds "loading" views in its constructor).
        CultureInfo culture = LanguageBootstrap.Apply(Settings.Language);
        Log.Info($"UI language: {culture.Name} (setting '{Settings.Language}')");

        _refreshMinutes = Settings.RefreshMinutes;

        _http = HttpClientFactory.Create();
        ProviderContext = new ProviderContext(
            _http,
            TimeProvider.System,
            new RealFileSystem(),
            new RealEnvironment(),
            new RealProcessRunner(),
            new WindowsCredentialStore(),
            new DpapiSecretStore(AppPaths.LocalDirectory),
            Log);
        Providers = ProviderCatalog.CreateAll(ProviderContext);

        Usage = new UsageService(
            Providers,
            () => TimeSpan.FromMinutes(Volatile.Read(ref _refreshMinutes)),
            new SnapshotCache(AppPaths.CacheFile, Log),
            TimeProvider.System,
            Log);
        Usage.ViewChanged += OnUsageViewChanged;

        Rotation = new RotationController(TimeProvider.System);
        _rotationTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = RotationTickInterval };
        _rotationTimer.Tick += OnRotationTick;

        _pathWatcher = new ProviderPathWatcher(Providers, Usage.NotifyPathChanged, Log);

        // Created idle: Start/ApplySettings run it only while the SystemMonitor setting is not Off.
        _systemSampler = new SystemUsageSampler(dispatcher, TimeProvider.System, Log);
        _systemSampler.Sampled += OnSystemSampled;

        Shell = new ShellEventWindow(Log);
        Theme = new ThemeWatcher(dispatcher, Log);
        Shell.ImmersiveColorSetChanged += OnImmersiveColorSetChanged;
    }

    public Dispatcher Dispatcher { get; }

    public AppSettings Settings { get; }

    public SettingsStore SettingsStore { get; }

    public ProviderContext ProviderContext { get; }

    public IReadOnlyList<IUsageProvider> Providers { get; }

    public UsageService Usage { get; }

    public RotationController Rotation { get; }

    public IAppLog Log { get; }

    public ThemeWatcher Theme { get; }

    public ShellEventWindow Shell { get; }

    /// <summary>Created in Start().</summary>
    public TaskbarHost? Taskbar { get; private set; }

    /// <summary>Raised on the UI thread at the end of ApplySettings.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>UsageService.ViewChanged marshalled to the UI thread (after Rotation.UpdateProviders).</summary>
    public event EventHandler<ProviderView>? ViewChanged;

    /// <summary>The latest PC measurement; null while the SystemMonitor setting is Off (or before the first reading).</summary>
    public SystemUsageSample? SystemUsage { get; private set; }

    /// <summary>
    /// Raised on the UI thread after <see cref="SystemUsage"/> changed (every 2 s while measuring). The rotation list is
    /// not rebuilt for it: the PC panel's id and status never change between samples.
    /// </summary>
    public event EventHandler? SystemUsageChanged;

    public void Start()
    {
        Dispatcher.VerifyAccess();
        if (_started || Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        _started = true;
        ApplySystemMonitor();
        ConfigureRotation();
        Usage.Start();
        _pathWatcher.Start();
        _rotationTimer.Start();
        bool startWithWindows = Settings.StartWithWindows;
        QueueBackground(() =>
        {
            if (StartupRegistration.Sync(startWithWindows, Log))
            {
                AdoptInstallerStartupRegistration();
            }
        });
        _ = Task.Run(LogTaskbarDiagnostics);
        Log.Info($"diagnostics: theme taskbarLight={Theme.IsTaskbarLight} appLight={Theme.IsAppLight}");
        Taskbar = new TaskbarHost(() => new WidgetWindow(new WidgetViewModel(this, Theme)), this, Shell);
        Taskbar.Start();
        Log.Info("app host started");
    }

    /// <summary>
    /// Normalize → refresh interval (reschedule when changed) → PC sampler on/off → rotation → save snapshot (background)
    /// → startup registry (background) → SettingsChanged.
    /// </summary>
    public void ApplySettings()
    {
        Dispatcher.VerifyAccess();
        if (IsShuttingDown)
        {
            // Shutdown() already flushed the settings window's pending change; a later one (e.g. a window closed by
            // Application.Shutdown) must not queue background work or touch disposed services.
            Log.Info("ApplySettings ignored: shutting down");
            return;
        }

        Settings.Normalize();

        // Only the UI thread writes _refreshMinutes, so this read is the previously applied value.
        int previousRefreshMinutes = Volatile.Read(ref _refreshMinutes);
        Volatile.Write(ref _refreshMinutes, Settings.RefreshMinutes);
        if (Settings.RefreshMinutes != previousRefreshMinutes)
        {
            Log.Info($"refresh interval changed {previousRefreshMinutes} → {Settings.RefreshMinutes} min; rescheduling");
            Usage.RescheduleAll();
        }

        ApplySystemMonitor();
        ConfigureRotation();

        // Serialize a deep copy taken on the UI thread: the pool thread never reads the Settings object the UI edits.
        // Queued saves run in order, so the last ApplySettings' snapshot is what ends up on disk.
        AppSettings snapshot = Settings.Clone();
        QueueBackground(() => SettingsStore.Save(snapshot));
        bool startWithWindows = Settings.StartWithWindows;
        QueueBackground(() => StartupRegistration.Apply(startWithWindows, Log));

        RaiseSettingsChanged();
    }

    /// <summary>
    /// Background thread: the installer registered this exe, so turn the setting on (UI thread) and persist it through
    /// ApplySettings. Its own StartupRegistration.Apply(true) is then a no-op because the value already matches.
    /// </summary>
    private void AdoptInstallerStartupRegistration()
    {
        if (Volatile.Read(ref _disposed) == 1 || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (Settings.StartWithWindows)
            {
                return;
            }

            Log.Info("startup registration adopted from installer");
            Settings.StartWithWindows = true;
            ApplySettings();
        }));
    }

    /// <summary>Each subscriber runs in isolation: a failing subscriber never blocks the others or the caller.</summary>
    private void RaiseSettingsChanged()
    {
        if (SettingsChanged is not EventHandler handlers)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log.Error("app host: a SettingsChanged subscriber failed", ex);
            }
        }
    }

    /// <summary>Each subscriber runs in isolation: a failing subscriber never blocks the others or the publisher.</summary>
    private void RaiseViewChanged(ProviderView view)
    {
        if (ViewChanged is not EventHandler<ProviderView> handlers)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<ProviderView>)handler)(this, view);
            }
            catch (Exception ex)
            {
                Log.Error("app host: a ViewChanged subscriber failed", ex);
            }
        }
    }

    private bool IsShuttingDown => _shutdownRequested || Volatile.Read(ref _disposed) == 1;

    /// <summary>
    /// Settings order ∩ Enabled, as current views. With the RotationPanel PC mode the virtual "PC" panel
    /// (<see cref="SystemPanel.Id"/>, status Ok, never in UsageService) follows the AIs at the end.
    /// </summary>
    public IReadOnlyList<ProviderView> EnabledViewsInOrder()
    {
        var result = new List<ProviderView>(Settings.Providers.Count + 1);
        foreach (ProviderSettings provider in Settings.Providers)
        {
            if (provider.Enabled && Usage.GetView(provider.Id) is ProviderView view)
            {
                result.Add(view);
            }
        }

        if (Settings.SystemMonitor == SystemMonitorMode.RotationPanel)
        {
            result.Add(SystemPanel.ToView(SystemUsage));
        }

        return result;
    }

    /// <summary>True when the PC line belongs on the AI detail card and tray tooltip (AlwaysBeside and CardOnly, design §3.4).</summary>
    public bool ShowsSystemLine => Settings.SystemMonitor is SystemMonitorMode.AlwaysBeside or SystemMonitorMode.CardOnly;

    public async Task RefreshNowAsync()
    {
        try
        {
            await Usage.RefreshNowAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("manual refresh failed", ex);
        }
    }

    /// <summary>
    /// Opens the settings window. Reached from the second-instance signal and from a tray left click.
    /// Shows the single SettingsWindow (or activates the open one); marshals to the UI thread.
    /// </summary>
    public void OpenSettings()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(OpenSettings);
            return;
        }

        if (IsShuttingDown)
        {
            Log.Info("show-settings ignored: shutting down");
            return;
        }

        Log.Info("show-settings requested");
        SettingsWindow.ShowOrActivate(this);
    }

    /// <summary>
    /// Synchronous, bounded (PendingWorkTimeout) last-resort cleanup for exits that bypass Shutdown → DisposeAsync
    /// (Windows logoff/shutdown, a failed startup): waits for the queued settings save, then flushes the log.
    /// Does nothing when DisposeAsync already ran or is running (it does the same). Safe to call more than once.
    /// </summary>
    public void FlushPendingWork()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        // The chain runs on the thread pool and never needs the UI thread, so waiting here cannot deadlock.
        if (!_backgroundChain.Wait(PendingWorkTimeout))
        {
            Log.Warn("background settings work still pending at exit");
        }

        (Log as FileAppLog)?.Flush();
    }

    /// <summary>Disposes everything, then calls Application.Current.Shutdown(). Safe to call twice.</summary>
    public void Shutdown()
    {
        Dispatcher.VerifyAccess();
        if (_shutdownRequested)
        {
            return;
        }

        // Close the settings window first: its Closed handler applies a pending slider change synchronously, and
        // that ApplySettings must run before the shutdown flag (and the disposal below) turns it into a no-op.
        // The save it queues is awaited by DisposeAsync through the background chain.
        try
        {
            SettingsWindow.CloseCurrent();
        }
        catch (Exception ex)
        {
            Log.Warn("closing the settings window at shutdown failed", ex);
        }

        _shutdownRequested = true;
        Log.Info("shutdown requested");
        _ = ShutdownAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // Everything before the first await runs synchronously on the caller (UI) thread.
        Taskbar?.Dispose();
        _systemSampler.Sampled -= OnSystemSampled;
        _systemSampler.Dispose();
        _rotationTimer.Stop();
        _rotationTimer.Tick -= OnRotationTick;
        Usage.ViewChanged -= OnUsageViewChanged;
        _pathWatcher.Dispose();
        Shell.ImmersiveColorSetChanged -= OnImmersiveColorSetChanged;
        Shell.Dispose();
        Theme.Dispose();

        await Usage.DisposeAsync();

        // The final settings save is the last item on the chain; wait for it (bounded) before flushing the log.
        Task pending = _backgroundChain;
        if (await Task.WhenAny(pending, Task.Delay(PendingWorkTimeout)) != pending)
        {
            Log.Warn("background settings work still pending at shutdown");
        }

        _http.Dispose();
        Log.Info("app host stopped");

        // After the final save: push buffered log lines (including the save result) to disk before the process exits.
        (Log as FileAppLog)?.Flush();
    }

    private async Task ShutdownAsync()
    {
        try
        {
            await DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("error while shutting down", ex);
        }
        finally
        {
            Application.Current?.Shutdown();
        }
    }

    /// <summary>Runs the PC sampler only while the SystemMonitor setting is not Off; Off also forgets the last sample.</summary>
    private void ApplySystemMonitor()
    {
        if (Settings.SystemMonitor == SystemMonitorMode.Off)
        {
            if (_systemSampler.IsRunning)
            {
                _systemSampler.Stop();
                Log.Info("system usage sampler stopped");
            }

            SystemUsage = null;
            return;
        }

        if (!_systemSampler.IsRunning)
        {
            Log.Info($"system usage sampler started ({Settings.SystemMonitor})");
            _systemSampler.Start();
        }
    }

    private void OnSystemSampled(object? sender, SystemUsageSample sample)
    {
        // DispatcherTimer tick: already on the UI thread.
        if (IsShuttingDown)
        {
            return;
        }

        SystemUsage = sample;
        if (SystemUsageChanged is not EventHandler handlers)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log.Error("app host: a SystemUsageChanged subscriber failed", ex);
            }
        }
    }

    private void ConfigureRotation()
    {
        Rotation.Configure(Settings.RotationEnabled, TimeSpan.FromSeconds(Settings.RotationSeconds), Settings.SkipUnavailable);
        Rotation.UpdateProviders(EnabledViewsInOrder());
    }

    /// <summary>Runs work on the thread pool, strictly after previously queued work. UI thread only.</summary>
    private void QueueBackground(Action work)
    {
        _backgroundChain = _backgroundChain.ContinueWith(
            _ => RunSafely(work),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private void RunSafely(Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            Log.Warn("background settings work failed", ex);
        }
    }

    private void OnImmersiveColorSetChanged(object? sender, EventArgs e) => Theme.Refresh();

    /// <summary>Background thread: logs every explorer-owned taskbar (manual verification aid).</summary>
    private void LogTaskbarDiagnostics()
    {
        try
        {
            IReadOnlyList<TaskbarInfo> taskbars = TaskbarLocator.FindAll();
            if (taskbars.Count == 0)
            {
                Log.Warn("diagnostics: no explorer-owned taskbar found");
                return;
            }

            for (int i = 0; i < taskbars.Count; i++)
            {
                TaskbarInfo t = taskbars[i];
                Log.Info(
                    $"diagnostics: taskbar[{i}] hwnd=0x{(long)t.Handle:X} primary={t.IsPrimary} " +
                    $"rect=({t.Bounds.Left},{t.Bounds.Top},{t.Bounds.Right},{t.Bounds.Bottom}) dpi={t.Dpi} " +
                    $"monitor={t.MonitorDeviceName ?? "?"} win11={t.IsWin11} edge={t.Edge}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("diagnostics: taskbar enumeration failed", ex);
        }
    }

    private void OnUsageViewChanged(object? sender, ProviderView view)
    {
        // Raised on a background thread by UsageService.
        if (Volatile.Read(ref _disposed) == 1 || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => PublishView(view)));
    }

    private void PublishView(ProviderView view)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        Rotation.UpdateProviders(EnabledViewsInOrder());
        RaiseViewChanged(view);
    }

    private void OnRotationTick(object? sender, EventArgs e) => Rotation.OnTick();
}
