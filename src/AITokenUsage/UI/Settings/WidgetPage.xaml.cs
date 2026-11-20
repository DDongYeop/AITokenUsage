using System.Windows;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.Settings;
using AITokenUsage.Resources;
using Microsoft.Win32;

namespace AITokenUsage.UI.Settings;

/// <summary>Display and appearance settings for the taskbar widget.</summary>
public partial class WidgetPage : SettingsPage
{
    private static readonly IReadOnlyList<SettingsChoice> PlacementChoices =
    [
        new(WidgetPlacement.LeftEdge, UiStrings.PlacementLeftEdge),
        new(WidgetPlacement.BeforeStart, UiStrings.PlacementBeforeStart),
        new(WidgetPlacement.BeforeTray, UiStrings.PlacementBeforeTray),
    ];

    private static readonly IReadOnlyList<SettingsChoice> IconStyleChoices =
    [
        new(ProviderIconStyle.Classic, UiStrings.ProviderIconClassic),
        new(ProviderIconStyle.Service, UiStrings.ProviderIconService),
        new(ProviderIconStyle.Custom, UiStrings.ProviderIconCustom),
    ];

    private static readonly IReadOnlyList<SettingsChoice> TextColorChoices =
    [
        new(TextColorMode.Auto, UiStrings.TextColorAuto),
        new(TextColorMode.Light, UiStrings.TextColorLight),
        new(TextColorMode.Dark, UiStrings.TextColorDark),
    ];

    private static readonly IReadOnlyList<SettingsChoice> TextWeightChoices =
    [
        new(TextWeightMode.Default, UiStrings.TextWeightDefault),
        new(TextWeightMode.Normal, UiStrings.TextWeightNormal),
        new(TextWeightMode.Bold, UiStrings.TextWeightBold),
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
        IsVisibleChanged += (_, _) => { if (IsVisible) LoadCustomProviders(); };
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
        RefreshOnHoverBox.IsChecked = settings.RefreshOnHover;
        SystemMonitorBox.ItemsSource = SystemMonitorChoices;
        SystemMonitorBox.SelectedItem = Find(SystemMonitorChoices, settings.SystemMonitor);
        TextWeightBox.ItemsSource = TextWeightChoices;
        TextWeightBox.SelectedItem = Find(TextWeightChoices, settings.TextWeight);
        SmoothWidgetTextBox.IsChecked = settings.SmoothWidgetText;
        LoadCustomProviders();
        IconStyleBox.ItemsSource = IconStyleChoices;
        IconStyleBox.SelectedItem = Find(IconStyleChoices, settings.ProviderIconStyle);
        RefreshCustomIcon();
        TextColorBox.ItemsSource = TextColorChoices;
        TextColorBox.SelectedItem = Find(TextColorChoices, settings.TextColor);
        FontBox.ItemsSource = WidgetFontChoice.All;
        FontBox.SelectedItem = WidgetFontChoice.Resolve(settings.WidgetFontFamily);
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
        HookToggle(RefreshOnHoverBox, on => Update(s => s.RefreshOnHover = on));
        SystemMonitorBox.SelectionChanged += (_, _) =>
        {
            if (SystemMonitorBox.SelectedItem is SettingsChoice { Value: SystemMonitorMode mode })
            {
                Update(s => s.SystemMonitor = mode);
            }
        };
        HookToggle(SmoothWidgetTextBox, on => Update(s => s.SmoothWidgetText = on));
        TextWeightBox.SelectionChanged += (_, _) =>
        {
            if (TextWeightBox.SelectedItem is SettingsChoice { Value: TextWeightMode mode })
            {
                Update(s => s.TextWeight = mode);
            }
        };
        IconStyleBox.SelectionChanged += (_, _) =>
        {
            if (IconStyleBox.SelectedItem is SettingsChoice { Value: ProviderIconStyle style })
            {
                Update(s => s.ProviderIconStyle = style);
                RefreshCustomIcon();
            }
        };
        CustomProviderBox.SelectionChanged += (_, _) => RefreshCustomIcon();
        ChooseIconButton.Click += (_, _) => ChangeCustomIcon(remove: false);
        RemoveIconButton.Click += (_, _) => ChangeCustomIcon(remove: true);
        TextColorBox.SelectionChanged += (_, _) =>
        {
            if (TextColorBox.SelectedItem is SettingsChoice { Value: TextColorMode mode })
            {
                Update(s => s.TextColor = mode);
            }
        };
        FontBox.SelectionChanged += (_, _) =>
        {
            if (FontBox.SelectedItem is WidgetFontChoice choice)
            {
                Update(s => s.WidgetFontFamily = choice.Name);
            }
        };
    }

    private void LoadCustomProviders()
    {
        object? selected = (CustomProviderBox.SelectedItem as SettingsChoice)?.Value;
        var choices = Host.Settings.Providers.Select(p =>
        {
            string? title = Host.Providers.FirstOrDefault(provider => provider.Id == p.Id)?.DisplayName;
            string label = ProviderIds.KindOf(p.Id) != p.Id && title is not null ? $"{title} · {p.Id}" : title ?? p.Id;
            return new SettingsChoice(p.Id, label);
        }).ToArray();
        CustomProviderBox.ItemsSource = choices;
        CustomProviderBox.SelectedItem = choices.FirstOrDefault(c => Equals(c.Value, selected)) ?? choices.FirstOrDefault();
    }

    private void RefreshCustomIcon()
    {
        bool custom = Host.Settings.ProviderIconStyle == ProviderIconStyle.Custom;
        CustomIconCard.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        if (!custom || CustomProviderBox.SelectedItem is not SettingsChoice { Value: string id }) return;

        CustomIconPreview.Render(id, ProviderIconStyle.Custom, !Host.Theme.IsAppLight, Foreground);
        RemoveIconButton.IsEnabled = CustomProviderIcons.Load(id) is not null;
    }

    private void ChangeCustomIcon(bool remove)
    {
        if (CustomProviderBox.SelectedItem is not SettingsChoice { Value: string id }) return;
        Window owner = Window.GetWindow(this);
        try
        {
            if (remove)
            {
                CustomProviderIcons.Remove(id);
            }
            else
            {
                var dialog = new OpenFileDialog
                {
                    Title = UiStrings.CustomIconChoose,
                    Filter = $"{UiStrings.ImageFiles}|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.ico",
                };
                if (dialog.ShowDialog(owner) != true) return;
                CustomProviderIcons.Import(id, dialog.FileName);
            }

            Host.RaiseSettingsChanged();
            RefreshCustomIcon();
        }
        catch (Exception ex)
        {
            Host.Log.Warn($"Could not update custom icon for {id}", ex);
            MessageBox.Show(owner, UiStrings.CustomIconError + Environment.NewLine + ex.Message,
                UiStrings.CustomIconHeader, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
