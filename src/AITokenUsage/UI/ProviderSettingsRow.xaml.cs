using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.Providers;
using AITokenUsage.Core.Settings;

namespace AITokenUsage.UI;

/// <summary>One "표시할 AI" row: enable, status, reorder and the two line drop-downs. Edits its ProviderSettings in place.</summary>
public partial class ProviderSettingsRow : UserControl
{
    private readonly ProviderSettings _settings;
    private readonly IUsageProvider _provider;
    private IReadOnlyList<MeterOption> _options = Array.Empty<MeterOption>();
    private bool _loading;

    public ProviderSettingsRow(ProviderSettings settings, IUsageProvider provider, bool canMoveUp, bool canMoveDown, UIElement? extra)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(provider);

        _settings = settings;
        _provider = provider;
        InitializeComponent();

        GlyphHost.Content = ProviderGlyphs.CreateIcon(provider.Id, 14, Brushes.Gray);
        NameText.Text = provider.DisplayName;
        EnabledBox.IsChecked = settings.Enabled;
        AutomationProperties.SetName(EnabledBox, $"{provider.DisplayName} 표시");
        AutomationProperties.SetName(UpButton, $"{provider.DisplayName} 위로");
        AutomationProperties.SetName(DownButton, $"{provider.DisplayName} 아래로");
        AutomationProperties.SetName(Line1Box, $"{provider.DisplayName} 1줄");
        AutomationProperties.SetName(Line2Box, $"{provider.DisplayName} 2줄");
        UpButton.IsEnabled = canMoveUp;
        DownButton.IsEnabled = canMoveDown;
        ExtraHost.Content = extra;
        ExtraHost.Visibility = extra is null ? Visibility.Collapsed : Visibility.Visible;

        RoutedEventHandler onEnabledToggled = (_, _) =>
        {
            _settings.Enabled = EnabledBox.IsChecked == true;
            Changed?.Invoke(this, EventArgs.Empty);
        };
        EnabledBox.Checked += onEnabledToggled;
        EnabledBox.Unchecked += onEnabledToggled;
        UpButton.Click += (_, _) => MoveRequested?.Invoke(this, -1);
        DownButton.Click += (_, _) => MoveRequested?.Invoke(this, +1);
        Line1Box.SelectionChanged += (_, _) => OnLineChanged(Line1Box, key => _settings.Line1Key = key);
        Line2Box.SelectionChanged += (_, _) => OnLineChanged(Line2Box, key => _settings.Line2Key = key);
    }

    public string ProviderId => _settings.Id;

    /// <summary>−1 = move up, +1 = move down.</summary>
    public event EventHandler<int>? MoveRequested;

    /// <summary>The row changed its ProviderSettings; the window applies and saves.</summary>
    public event EventHandler? Changed;

    public void Refresh(ProviderView? view, DateTimeOffset now)
    {
        string status = view is null ? "" : ProviderStatusText.ForSettings(view, now, TimeZoneInfo.Local);
        StatusText.Text = status;
        StatusText.ToolTip = status.Length > 0 ? status : null;
        RefreshOptions(view);
    }

    /// <summary>Releases the extra element (Copilot sign-in panel) so a rebuilt row can host it.</summary>
    public void DetachExtra() => ExtraHost.Content = null;

    private void RefreshOptions(ProviderView? view)
    {
        IReadOnlyList<UsageMeter> meters = view is null ? Array.Empty<UsageMeter>() : view.DisplayMeters;
        IReadOnlyList<MeterOption> options =
            MeterOptionList.Build(_provider.StaticMeterOptions, meters, [_settings.Line1Key, _settings.Line2Key]);
        if (MeterOptionList.SameOptions(options, _options))
        {
            return;
        }

        _options = options;
        _loading = true;
        try
        {
            Line1Box.ItemsSource = options.ToList();
            Line1Box.SelectedValue = _settings.Line1Key;
            Line2Box.ItemsSource = options.ToList();
            Line2Box.SelectedValue = _settings.Line2Key;
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnLineChanged(ComboBox box, Action<string> assign)
    {
        if (_loading || box.SelectedValue is not string key || key.Length == 0)
        {
            return;
        }

        assign(key);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
