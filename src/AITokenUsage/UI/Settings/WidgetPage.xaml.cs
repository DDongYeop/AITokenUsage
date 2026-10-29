using AITokenUsage.Core.Models;
using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.Settings;
using AITokenUsage.Resources;
using Microsoft.Win32;

namespace AITokenUsage.UI.Settings;

/// <summary>"Widget" page: monitor, position, value mode, time-until-reset column, detail card, PC usage and text colour (v1.1 spec §4.3).</summary>
public partial class WidgetPage : SettingsPage
{
    private static readonly IReadOnlyList<SettingsChoice> PlacementChoices =
    [
        new(WidgetPlacement.LeftEdge, UiStrings.PlacementLeftEdge),
        new(WidgetPlacement.BeforeStart, UiStrings.PlacementBeforeStart),
        new(WidgetPlacement.BeforeTray, UiStrings.PlacementBeforeTray),
    ];

    private static readonly IReadOnlyList<SettingsChoice> TextColorChoices =
    [
        new(TextColorMode.Auto, UiStrings.TextColorAuto),
        new(TextColorMode.Light, UiStrings.TextColorLight),
        new(TextColorMode.Dark, UiStrings.TextColorDark),
    ];

    private static readonly IReadOnlyList<SettingsChoice> SystemMonitorChoices =
    [
        new(SystemMonitorMode.Off, UiStrings.SystemMonitorOff),
        new(SystemMonitorMode.RotationPanel, UiStrings.SystemMonitorRotationPanel),
        new(SystemMonitorMode.AlwaysBeside, UiStrings.SystemMonitorAlwaysBeside),
        new(SystemMonitorMode.CardOnly, UiStrings.SystemMonitorCardOnly),
    ];

    public WidgetPage(AppHost host)
        : base(host)
    {
        InitializeComponent();
        HookEvents();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    protected override void OnLoad()
    {
        AppSettings settings = Host.Settings;

        LoadMonitors();
        PlacementBox.ItemsSource = PlacementChoices;
        PlacementBox.SelectedItem = Find(PlacementChoices, settings.Placement);
        UsedRadio.IsChecked = settings.ValueMode == ValueMode.Used;
        RemainingRadio.IsChecked = settings.ValueMode == ValueMode.Remaining;
        ShowResetTimeBox.IsChecked = settings.ShowResetTime;
        ShowDetailCardBox.IsChecked = settings.ShowDetailCard;
        SystemMonitorBox.ItemsSource = SystemMonitorChoices;
        SystemMonitorBox.SelectedItem = Find(SystemMonitorChoices, settings.SystemMonitor);
        TextColorBox.ItemsSource = TextColorChoices;
        TextColorBox.SelectedItem = Find(TextColorChoices, settings.TextColor);
    }

    protected override void OnDispose() => SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

    private void HookEvents()
    {
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
        HookToggle(ShowResetTimeBox, on => Update(s => s.ShowResetTime = on));
        HookToggle(ShowDetailCardBox, on => Update(s => s.ShowDetailCard = on));
        SystemMonitorBox.SelectionChanged += (_, _) =>
        {
            if (SystemMonitorBox.SelectedItem is SettingsChoice { Value: SystemMonitorMode mode })
            {
                Update(s => s.SystemMonitor = mode);
            }
        };
        TextColorBox.SelectionChanged += (_, _) =>
        {
            if (TextColorBox.SelectedItem is SettingsChoice { Value: TextColorMode mode })
            {
                Update(s => s.TextColor = mode);
            }
        };
    }

    private void LoadMonitors()
    {
        MonitorDescriptor[] monitors = System.Windows.Forms.Screen.AllScreens
            .Select(s => new MonitorDescriptor(s.DeviceName, s.Bounds.Left, s.Bounds.Top, s.Bounds.Width, s.Bounds.Height, s.Primary))
            .ToArray();
        IReadOnlyList<MonitorChoice> choices = MonitorChoices.Build(monitors, Host.Settings.MonitorDeviceName);
        MonitorBox.ItemsSource = choices;
        MonitorBox.SelectedIndex = MonitorChoices.IndexOf(choices, Host.Settings.MonitorDeviceName);
    }

    /// <summary>Raised on a SystemEvents thread: rebuild the monitor list on the UI thread without applying anything.</summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (!IsDisposed)
            {
                WhileLoading(LoadMonitors);
            }
        });
}
