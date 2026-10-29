using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.Providers;
using AITokenUsage.Core.Settings;
using AITokenUsage.Resources;
using AITokenUsage.UI.Controls;

namespace AITokenUsage.UI.Settings;

/// <summary>
/// One AI on the "AI connections" page (v1.1 spec §4.3): status, "show on widget", the connection guide while the AI is
/// not connected, and (expanded) the two line drop-downs and reorder buttons. Edits its ProviderSettings in place and
/// raises <see cref="Changed"/>; the page applies and saves.
/// </summary>
public partial class ProviderCard : UserControl
{
    private const string ChevronDownGlyph = "\uE70D";
    private const string ChevronUpGlyph = "\uE70E";

    private readonly ProviderSettings _settings;
    private readonly IUsageProvider _provider;
    private readonly UIElement? _extra;
    private readonly ConnectionGuideView _guide;
    private IReadOnlyList<MeterOption> _options = Array.Empty<MeterOption>();
    private (ProviderStatus Status, string? Message)? _guideState;
    private bool _loading;

    /// <param name="extra">Copilot only: the GitHub sign-in panel, shown while signed out or signed in with the app.</param>
    public ProviderCard(AppHost host, ProviderSettings settings, UIElement? extra)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        _provider = host.Providers.FirstOrDefault(p => p.Id == settings.Id)
            ?? throw new ArgumentException($"No provider with id '{settings.Id}'.", nameof(settings));
        _extra = extra;
        InitializeComponent();

        string name = _provider.DisplayName;
        GlyphHost.Content = ProviderGlyphs.CreateIcon(_provider.Id, 16, ProviderGlyphs.BrandBrush(null));
        NameText.Text = name;
        ChevronGlyph.Text = ChevronDownGlyph;
        AutomationProperties.SetName(EnabledBox, ControlName(name, UiStrings.ProviderShowOnWidget));
        AutomationProperties.SetName(ExpandButton, ControlName(name, UiStrings.ProviderOptionsName));
        AutomationProperties.SetName(Line1Box, ControlName(name, UiStrings.ProviderLine1Label));
        AutomationProperties.SetName(Line2Box, ControlName(name, UiStrings.ProviderLine2Label));
        AutomationProperties.SetName(UpButton, ControlName(name, UiStrings.ProviderMoveUpButton));
        AutomationProperties.SetName(DownButton, ControlName(name, UiStrings.ProviderMoveDownButton));

        _guide = new ConnectionGuideView(host, _provider.Id, name);
        GuideHost.Child = _guide;
        ExtraHost.Content = extra;

        WhileLoading(() => EnabledBox.IsChecked = settings.Enabled);

        RoutedEventHandler onEnabledToggled = (_, _) =>
        {
            if (_loading)
            {
                return;
            }

            _settings.Enabled = EnabledBox.IsChecked == true;
            Changed?.Invoke(this, EventArgs.Empty);
        };
        EnabledBox.Checked += onEnabledToggled;
        EnabledBox.Unchecked += onEnabledToggled;
        ExpandButton.Checked += (_, _) => SetExpanded(true);
        ExpandButton.Unchecked += (_, _) => SetExpanded(false);
        UpButton.Click += (_, _) => MoveRequested?.Invoke(this, -1);
        DownButton.Click += (_, _) => MoveRequested?.Invoke(this, +1);
        Line1Box.SelectionChanged += (_, _) => OnLineChanged(Line1Box, key => _settings.Line1Key = key);
        Line2Box.SelectionChanged += (_, _) => OnLineChanged(Line2Box, key => _settings.Line2Key = key);
    }

    public string ProviderId => _settings.Id;

    /// <summary>−1 = move up, +1 = move down.</summary>
    public event EventHandler<int>? MoveRequested;

    /// <summary>The card changed its ProviderSettings; the page applies and saves.</summary>
    public event EventHandler? Changed;

    /// <summary>[Move up] is disabled on the first card and [Move down] on the last.</summary>
    public void SetMoveState(bool canMoveUp, bool canMoveDown)
    {
        UpButton.IsEnabled = canMoveUp;
        DownButton.IsEnabled = canMoveDown;
    }

    /// <summary>
    /// Keeps keyboard focus on the reorder buttons after the page has moved this card: the button for
    /// <paramref name="delta"/> if it is still enabled, otherwise the other one.
    /// </summary>
    public void FocusMoveButton(int delta)
    {
        Button preferred = delta < 0 ? UpButton : DownButton;
        Button other = delta < 0 ? DownButton : UpButton;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => (preferred.IsEnabled ? preferred : other).Focus()));
    }

    /// <summary>Updates the status line, badge, connection guide, Copilot panel visibility and line options. UI thread.</summary>
    public void Refresh(ProviderView? view, DateTimeOffset now)
    {
        string status = view is null ? "" : ProviderStatusText.ForSettings(view, now, TimeZoneInfo.Local);
        StatusText.Text = status;
        StatusText.ToolTip = status.Length > 0 ? status : null;
        Badge.Kind = view is null ? StatusBadgeKind.Neutral : StatusBadge.KindFor(view.Current.Status);

        RefreshGuide(view);
        RefreshExtra(view);
        RefreshOptions(view);
    }

    /// <summary>Releases the extra element (Copilot sign-in panel) when the page disposes its cards.</summary>
    public void DetachExtra() => ExtraHost.Content = null;

    private static string ControlName(string providerName, string control) =>
        string.Format(CultureInfo.CurrentCulture, UiStrings.ProviderControlNameFormat, providerName, control);

    private void SetExpanded(bool expanded)
    {
        OptionsPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ChevronGlyph.Text = expanded ? ChevronUpGlyph : ChevronDownGlyph;
    }

    /// <summary>
    /// Rebuilt only when the status or its message changes, so a [Check again] in progress or a "could not open a
    /// terminal" notice survives the refreshes that do not change anything.
    /// </summary>
    private void RefreshGuide(ProviderView? view)
    {
        (ProviderStatus, string?)? state = view is null ? null : (view.Current.Status, view.Current.StatusMessage);
        if (state == _guideState)
        {
            return;
        }

        _guideState = state;
        ConnectionGuide? guide = view is null
            ? null
            : ConnectionGuideBuilder.Build(_provider.Id, view.Current.Status, view.Current.StatusMessage);
        if (guide is null || view is null || RepeatsStatusLine(guide, view.Current.StatusMessage))
        {
            GuideHost.Visibility = Visibility.Collapsed;
            return;
        }

        _guide.Show(guide, view.Current.Status);
        GuideHost.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// A footnote-only guide whose footnote is the provider's own status message (Gemini "unsupported", e.g. "API key /
    /// Vertex — usage unavailable") says nothing the status line does not. Without a message the footnote is the default
    /// "personal accounts discontinued" explanation, which is kept.
    /// </summary>
    private static bool RepeatsStatusLine(ConnectionGuide guide, string? statusMessage) =>
        guide.Steps.Count == 0
        && !string.IsNullOrWhiteSpace(statusMessage)
        && string.Equals(guide.Footnote?.Trim(), statusMessage.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// The Copilot sign-in panel is shown while Copilot is signed out (or has no token source) and while the app's own
    /// GitHub sign-in is in progress or saved ("GitHub: login" + sign out).
    /// </summary>
    private void RefreshExtra(ProviderView? view)
    {
        if (_extra is null)
        {
            return;
        }

        bool needsSignIn = view?.Current.Status is ProviderStatus.SignedOut or ProviderStatus.NotInstalled;
        bool active = _extra is not CopilotSignInPanel panel || panel.IsActive;
        bool visible = needsSignIn || active;
        ExtraHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

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
        WhileLoading(() =>
        {
            Line1Box.ItemsSource = options.ToList();
            Line1Box.SelectedValue = _settings.Line1Key;
            Line2Box.ItemsSource = options.ToList();
            Line2Box.SelectedValue = _settings.Line2Key;
        });
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

    private void WhileLoading(Action fill)
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            fill();
        }
        finally
        {
            _loading = wasLoading;
        }
    }
}
