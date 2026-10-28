using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.Providers;
using AITokenUsage.Core.Providers.Copilot;
using AITokenUsage.Core.Settings;
using AITokenUsage.Taskbar;
using Microsoft.Win32;

namespace AITokenUsage.UI;

/// <summary>Settings window (spec §2.5). Every change is applied and saved immediately; there is no OK/Cancel.</summary>
public partial class SettingsWindow : Window
{
    private static readonly TimeSpan SliderApplyDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan StatusRefreshInterval = TimeSpan.FromSeconds(30);

    private static readonly IReadOnlyList<SettingsChoice> PlacementChoices =
    [
        new(WidgetPlacement.LeftEdge, "작업 표시줄 왼쪽 끝"),
        new(WidgetPlacement.BeforeStart, "시작 버튼 왼쪽"),
        new(WidgetPlacement.BeforeTray, "알림 영역 왼쪽"),
    ];

    private static readonly IReadOnlyList<SettingsChoice> TextColorChoices =
    [
        new(TextColorMode.Auto, "자동 (작업 표시줄 테마)"),
        new(TextColorMode.Light, "밝게"),
        new(TextColorMode.Dark, "어둡게"),
    ];

    private static readonly IReadOnlyList<SettingsChoice> RefreshChoices =
        ProviderDefaults.RefreshChoicesMinutes.Select(minutes => new SettingsChoice(minutes, $"{minutes}분")).ToArray();

    private static SettingsWindow? s_current;

    private readonly AppHost _host;
    private readonly ThemeWatcher _theme;
    private readonly DispatcherTimer _sliderTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly CopilotSignInPanel? _copilotPanel;
    private readonly List<ProviderSettingsRow> _rows = [];
    private IReadOnlyList<MonitorChoice> _monitorChoices = Array.Empty<MonitorChoice>();
    private bool _loading;

    public SettingsWindow(AppHost host, ThemeWatcher theme)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(theme);

        _host = host;
        _theme = theme;
        InitializeComponent();
        ApplyTheme();

        _sliderTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = SliderApplyDelay };
        _sliderTimer.Tick += OnSliderTimer;
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = StatusRefreshInterval };
        _statusTimer.Tick += (_, _) => RefreshProviderRows();

        CopilotUsageProvider? copilot = _host.Providers.OfType<CopilotUsageProvider>().FirstOrDefault();
        if (copilot is not null)
        {
            _copilotPanel = new CopilotSignInPanel(_host, copilot);
        }

        LoadValues();
        BuildProviderRows();
        HookEvents();

        _host.ViewChanged += OnViewChanged;
        _theme.Changed += OnThemeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += OnClosed;
        _statusTimer.Start();
    }

    /// <summary>Opens the single settings window, or restores and activates the one already open. UI thread only.</summary>
    public static void ShowOrActivate(AppHost host, ThemeWatcher theme)
    {
        if (s_current is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();
            return;
        }

        var window = new SettingsWindow(host, theme);
        s_current = window;
        window.Show();
        window.Activate();
    }

    /// <summary>
    /// Closes the open settings window, if any. OnClosed applies a pending slider change synchronously, so call this
    /// before the host starts shutting down (AppHost.ApplySettings is a no-op afterwards). UI thread only.
    /// </summary>
    public static void CloseCurrent() => s_current?.Close();

    private void ApplyTheme()
    {
#pragma warning disable WPF0001 // Window.ThemeMode (Fluent) is flagged experimental in .NET 9/10.
        ThemeMode = _theme.IsAppLight ? ThemeMode.Light : ThemeMode.Dark;
#pragma warning restore WPF0001
    }

    private void LoadValues()
    {
        _loading = true;
        try
        {
            AppSettings settings = _host.Settings;

            RotationEnabledBox.IsChecked = settings.RotationEnabled;
            IntervalSlider.Value = settings.RotationSeconds;
            IntervalText.Text = FormatSeconds(settings.RotationSeconds);
            IntervalSlider.IsEnabled = settings.RotationEnabled;
            PauseOnHoverBox.IsChecked = settings.PauseOnHover;
            SkipUnavailableBox.IsChecked = settings.SkipUnavailable;

            LoadMonitors();
            PlacementBox.ItemsSource = PlacementChoices;
            PlacementBox.SelectedItem = Find(PlacementChoices, settings.Placement);
            UsedRadio.IsChecked = settings.ValueMode == ValueMode.Used;
            RemainingRadio.IsChecked = settings.ValueMode == ValueMode.Remaining;
            TextColorBox.ItemsSource = TextColorChoices;
            TextColorBox.SelectedItem = Find(TextColorChoices, settings.TextColor);

            RefreshBox.ItemsSource = RefreshChoices;
            RefreshBox.SelectedItem = Find(RefreshChoices, settings.RefreshMinutes);
            StartWithWindowsBox.IsChecked = settings.StartWithWindows;
            ShowTrayBox.IsChecked = settings.ShowTrayIcon;

            FooterText.Text = $"변경 사항은 바로 적용되고 저장됩니다 · {_host.SettingsStore.FilePath}";
        }
        finally
        {
            _loading = false;
        }
    }

    private void LoadMonitors()
    {
        MonitorDescriptor[] monitors = System.Windows.Forms.Screen.AllScreens
            .Select(s => new MonitorDescriptor(s.DeviceName, s.Bounds.Left, s.Bounds.Top, s.Bounds.Width, s.Bounds.Height, s.Primary))
            .ToArray();
        _monitorChoices = MonitorChoices.Build(monitors, _host.Settings.MonitorDeviceName);
        MonitorBox.ItemsSource = _monitorChoices;
        MonitorBox.SelectedIndex = MonitorChoices.IndexOf(_monitorChoices, _host.Settings.MonitorDeviceName);
    }

    private void HookEvents()
    {
        HookCheck(RotationEnabledBox, enabled =>
        {
            IntervalSlider.IsEnabled = enabled;
            Update(s => s.RotationEnabled = enabled);
        });
        IntervalSlider.ValueChanged += OnIntervalChanged;
        HookCheck(PauseOnHoverBox, on => Update(s => s.PauseOnHover = on));
        HookCheck(SkipUnavailableBox, on => Update(s => s.SkipUnavailable = on));

        MonitorBox.SelectionChanged += (_, _) =>
        {
            if (MonitorBox.SelectedItem is MonitorChoice choice)
            {
                Update(s => s.MonitorDeviceName = choice.DeviceName);
            }
        };
        PlacementBox.SelectionChanged += (_, _) =>
        {
            if (PlacementBox.SelectedItem is SettingsChoice { Value: WidgetPlacement placement })
            {
                Update(s => s.Placement = placement);
            }
        };
        UsedRadio.Checked += (_, _) => Update(s => s.ValueMode = ValueMode.Used);
        RemainingRadio.Checked += (_, _) => Update(s => s.ValueMode = ValueMode.Remaining);
        TextColorBox.SelectionChanged += (_, _) =>
        {
            if (TextColorBox.SelectedItem is SettingsChoice { Value: TextColorMode mode })
            {
                Update(s => s.TextColor = mode);
            }
        };

        RefreshBox.SelectionChanged += (_, _) =>
        {
            if (RefreshBox.SelectedItem is SettingsChoice { Value: int minutes })
            {
                Update(s => s.RefreshMinutes = minutes);
            }
        };
        HookCheck(StartWithWindowsBox, on => Update(s => s.StartWithWindows = on));
        HookCheck(ShowTrayBox, on => Update(s => s.ShowTrayIcon = on));
    }

    /// <summary>Applies on Checked and Unchecked so keyboard, UI Automation and mouse changes all take effect (never on load: handlers attach after values are set).</summary>
    private static void HookCheck(CheckBox box, Action<bool> apply)
    {
        RoutedEventHandler handler = (_, _) => apply(box.IsChecked == true);
        box.Checked += handler;
        box.Unchecked += handler;
    }

    private void Update(Action<AppSettings> change)
    {
        if (_loading)
        {
            return;
        }

        change(_host.Settings);
        ApplySettings();
    }

    private void ApplySettings()
    {
        try
        {
            _host.ApplySettings();
        }
        catch (Exception ex)
        {
            _host.Log.Error("Applying settings from the settings window failed.", ex);
        }
    }

    private void OnIntervalChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        IntervalText.Text = FormatSeconds((int)Math.Round(e.NewValue));
        if (_loading)
        {
            return;
        }

        _sliderTimer.Stop();
        _sliderTimer.Start();
    }

    private void OnSliderTimer(object? sender, EventArgs e)
    {
        _sliderTimer.Stop();
        int seconds = (int)Math.Round(IntervalSlider.Value);
        Update(s => s.RotationSeconds = seconds);
    }

    private void BuildProviderRows()
    {
        foreach (ProviderSettingsRow old in _rows)
        {
            old.MoveRequested -= OnMoveRequested;
            old.Changed -= OnRowChanged;
            old.DetachExtra();
        }

        _rows.Clear();
        ProviderRowsPanel.Children.Clear();

        List<ProviderSettings> providers = _host.Settings.Providers;
        DateTimeOffset now = Now();
        for (int i = 0; i < providers.Count; i++)
        {
            ProviderSettings settings = providers[i];
            IUsageProvider? provider = _host.Providers.FirstOrDefault(p => p.Id == settings.Id);
            if (provider is null)
            {
                continue;
            }

            UIElement? extra = provider is CopilotUsageProvider ? _copilotPanel : null;
            var row = new ProviderSettingsRow(settings, provider, i > 0, i < providers.Count - 1, extra);
            row.MoveRequested += OnMoveRequested;
            row.Changed += OnRowChanged;
            row.Refresh(_host.Usage.GetView(settings.Id), now);
            _rows.Add(row);
            ProviderRowsPanel.Children.Add(row);
        }
    }

    private void RefreshProviderRows()
    {
        DateTimeOffset now = Now();
        foreach (ProviderSettingsRow row in _rows)
        {
            row.Refresh(_host.Usage.GetView(row.ProviderId), now);
        }

        _copilotPanel?.Refresh();
    }

    private void OnRowChanged(object? sender, EventArgs e) => ApplySettings();

    private void OnMoveRequested(object? sender, int delta)
    {
        if (sender is not ProviderSettingsRow row)
        {
            return;
        }

        List<ProviderSettings> list = _host.Settings.Providers;
        int index = list.FindIndex(p => p.Id == row.ProviderId);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= list.Count)
        {
            return;
        }

        (list[index], list[target]) = (list[target], list[index]);
        ApplySettings();

        // The clicked button lives in a row that is about to be replaced; rebuild after this click finishes.
        Dispatcher.InvokeAsync(BuildProviderRows);
    }

    private void OnViewChanged(object? sender, ProviderView view)
    {
        DateTimeOffset now = Now();
        foreach (ProviderSettingsRow row in _rows)
        {
            if (row.ProviderId == view.ProviderId)
            {
                row.Refresh(view, now);
            }
        }

        if (view.ProviderId == ProviderIds.Copilot)
        {
            _copilotPanel?.Refresh();
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(ApplyTheme);

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            _loading = true;
            try
            {
                LoadMonitors();
            }
            finally
            {
                _loading = false;
            }
        });

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_sliderTimer.IsEnabled)
        {
            OnSliderTimer(this, EventArgs.Empty);
        }

        _statusTimer.Stop();
        _host.ViewChanged -= OnViewChanged;
        _theme.Changed -= OnThemeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _copilotPanel?.Dispose();

        if (ReferenceEquals(s_current, this))
        {
            s_current = null;
        }
    }

    private DateTimeOffset Now() => _host.ProviderContext.Time.GetUtcNow();

    private static SettingsChoice? Find(IReadOnlyList<SettingsChoice> choices, object value) =>
        choices.FirstOrDefault(c => Equals(c.Value, value));

    private static string FormatSeconds(int seconds) => $"{seconds}초";
}
