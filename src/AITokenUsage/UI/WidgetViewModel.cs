using System.Windows.Threading;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.Rotation;
using AITokenUsage.Core.Settings;
using AITokenUsage.Taskbar;

namespace AITokenUsage.UI;

/// <summary>Feeds the widget: which provider is shown, its two lines, the dots and the text colours.</summary>
public sealed class WidgetViewModel : IDisposable
{
    /// <summary>How often the widget redraws so the time-to-reset column keeps counting down between data refreshes.</summary>
    public static readonly TimeSpan ResetRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly AppHost _host;
    private readonly ThemeWatcher _theme;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _resetTimer;
    private bool _pointerOver;
    private bool _hoverPaused;
    private bool _disposed;

    public WidgetViewModel(AppHost host, ThemeWatcher theme)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(theme);

        _host = host;
        _theme = theme;
        _dispatcher = Dispatcher.CurrentDispatcher;
        CurrentModel = BuildModel();
        Palette = ResolvePalette();

        _host.ViewChanged += OnViewChanged;
        _host.SettingsChanged += OnSettingsChanged;
        _host.Rotation.CurrentChanged += OnCurrentChanged;
        _theme.Changed += OnThemeChanged;

        _resetTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = ResetRefreshInterval };
        _resetTimer.Tick += OnResetTick;
        _resetTimer.Start();
    }

    public AppHost Host => _host;

    /// <summary>Whether the widget shows the time-to-reset column (and is therefore 210 instead of 176 DIP wide).</summary>
    public bool ShowResetTime => _host.Settings.ShowResetTime;

    public ThemeWatcher Theme => _theme;

    public WidgetPanelModel CurrentModel { get; private set; }

    public WidgetPalette Palette { get; private set; }

    public bool IsUserPaused => _host.Rotation.IsUserPaused;

    /// <summary>The shown provider changed. Argument: +1 next, −1 previous, 0 list change (redraw without animation).</summary>
    public event EventHandler<int>? ProviderSwitched;

    /// <summary>Same provider, new data, settings, dots or colours: redraw in place.</summary>
    public event EventHandler? Updated;

    public void Next() => _host.Rotation.Next();

    public void Previous() => _host.Rotation.Previous();

    public void SetUserPaused(bool paused) => _host.Rotation.SetUserPaused(paused);

    public void OpenSettings() => _host.OpenSettings();

    public void Exit() => _host.Shutdown();

    public async void RefreshNow()
    {
        try
        {
            await _host.RefreshNowAsync();
        }
        catch (Exception ex)
        {
            _host.Log.Error("Manual refresh from the widget menu failed.", ex);
        }
    }

    /// <summary>Pointer over the widget (from Task 18 also over its detail card). Pauses rotation when PauseOnHover is on.</summary>
    public void SetPointerOver(bool over)
    {
        _pointerOver = over;
        ApplyHoverPause();
    }

    public WidgetPanelModel BuildModel()
    {
        RotationController rotation = _host.Rotation;
        string? id = rotation.CurrentId;
        ProviderView? view = id is null ? null : _host.Usage.GetView(id);
        if (view is null)
        {
            return WidgetPanelBuilder.Empty();
        }

        ProviderSettings? settings = _host.Settings.Providers.FirstOrDefault(p => p.Id == view.ProviderId);
        (string line1, string line2) = settings is null
            ? ProviderDefaults.DefaultLines(view.ProviderId)
            : (settings.Line1Key, settings.Line2Key);

        return WidgetPanelBuilder.Build(
            view, line1, line2, _host.Settings.ValueMode, rotation.EligibleIds.Count, rotation.CurrentIndex,
            _host.ProviderContext.Time.GetUtcNow());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _resetTimer.Stop();
        _resetTimer.Tick -= OnResetTick;
        _host.ViewChanged -= OnViewChanged;
        _host.SettingsChanged -= OnSettingsChanged;
        _host.Rotation.CurrentChanged -= OnCurrentChanged;
        _theme.Changed -= OnThemeChanged;

        if (_hoverPaused)
        {
            _hoverPaused = false;
            _host.Rotation.SetHoverPaused(false);
        }
    }

    private WidgetPalette ResolvePalette() => WidgetPalette.Resolve(_theme.IsTaskbarLight, _host.Settings.TextColor);

    private void ApplyHoverPause()
    {
        bool paused = !_disposed && _pointerOver && _host.Settings.PauseOnHover;
        if (paused == _hoverPaused)
        {
            return;
        }

        _hoverPaused = paused;
        _host.Rotation.SetHoverPaused(paused);
    }

    /// <summary>
    /// Redraws the current panel in place (no slide). A running rotation transition is unaffected: the widget only
    /// re-renders its front panel, which already shows the current provider.
    /// </summary>
    private void OnResetTick(object? sender, EventArgs e) => RaiseUpdated();

    private void OnViewChanged(object? sender, ProviderView view) => RaiseUpdated();

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        ApplyHoverPause();
        RaiseUpdated();
    }

    private void OnCurrentChanged(object? sender, RotationChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        CurrentModel = BuildModel();
        ProviderSwitched?.Invoke(this, e.Direction);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.CheckAccess())
        {
            RaiseUpdated();
        }
        else
        {
            _dispatcher.InvokeAsync(RaiseUpdated);
        }
    }

    private void RaiseUpdated()
    {
        if (_disposed)
        {
            return;
        }

        CurrentModel = BuildModel();
        Palette = ResolvePalette();
        Updated?.Invoke(this, EventArgs.Empty);
    }
}
