using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AITokenUsage.Accounts;
using AITokenUsage.Core.Accounts;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.Providers;
using AITokenUsage.Core.Providers.Claude;
using AITokenUsage.Core.Providers.Codex;
using AITokenUsage.Core.Settings;
using AITokenUsage.Resources;
using AITokenUsage.UI.Controls;

namespace AITokenUsage.UI.Settings;

/// <summary>
/// One AI on the "AI connections" page (v1.1 spec §4.3): status, "show on widget", the connection guide while the AI is
/// not connected, and (expanded) the two line drop-downs and reorder buttons. Edits its ProviderSettings in place and
/// raises <see cref="Changed"/>; the page applies and saves.
/// An extra account (multi-account spec §5) shows its email under the name and [Sign in again] instead of the guide, and
/// [Remove account] when expanded; the page runs the sign-in and the removal.
/// </summary>
public partial class ProviderCard : UserControl
{
    private const string ChevronDownGlyph = "\uE70D";
    private const string ChevronUpGlyph = "\uE70E";

    /// <summary>Extra accounts only: the [Sign in again] style key while the account is signed out (Fluent theme).</summary>
    private const string AccentButtonStyleKey = "AccentButtonStyle";

    private readonly ProviderSettings _settings;
    private readonly IUsageProvider _provider;
    private readonly UIElement? _extra;
    private readonly bool _isAccount;
    private readonly ConnectionGuideView? _guide;
    private IReadOnlyList<MeterOption> _options = Array.Empty<MeterOption>();
    private (ProviderStatus Status, string? Message)? _guideState;
    private string _title = "";
    private bool _signedOut;
    private bool _loginRunning;
    private bool _loginBusy;
    private bool? _cliInstalled;
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
        _isAccount = AccountIds.IsAccountId(settings.Id);
        InitializeComponent();

        string name = _provider.DisplayName;
        GlyphHost.Content = ProviderGlyphs.CreateIcon(_provider.Id, 16, ProviderGlyphs.BrandBrush(null));
        ChevronGlyph.Text = ChevronDownGlyph;
        SetTitle(name);

        if (_isAccount)
        {
            // An extra account signs in through the widget only: [Sign in again] replaces the connection guide.
            AccountPanel.Visibility = Visibility.Visible;
            RemoveButton.Visibility = Visibility.Visible;
            ReloginButton.Click += (_, _) => ReloginRequested?.Invoke(this, EventArgs.Empty);
            LoginCancelButton.Click += (_, _) => LoginCancelRequested?.Invoke(this, EventArgs.Empty);
            RemoveButton.Click += (_, _) => RemoveRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _guide = new ConnectionGuideView(host, _provider.Id, name);
            GuideHost.Child = _guide;
        }

        ExtraHost.Content = extra;

        NicknameBox.Text = settings.Nickname ?? "";
        NicknameBox.LostKeyboardFocus += (_, _) => CommitNickname();
        NicknameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitNickname();
                e.Handled = true;
            }
        };

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

    internal void CommitNickname()
    {
        string? previous = _settings.Nickname;
        _settings.Nickname = NicknameBox.Text;
        NicknameBox.Text = _settings.Nickname ?? "";
        if (previous != _settings.Nickname)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public string ProviderId => _settings.Id;

    /// <summary>The shown name: "Claude", or "Claude · Team" when several panels of that AI are on.</summary>
    public string Title => _title;

    /// <summary>−1 = move up, +1 = move down.</summary>
    public event EventHandler<int>? MoveRequested;

    /// <summary>The card changed its ProviderSettings; the page applies and saves.</summary>
    public event EventHandler? Changed;

    /// <summary>Extra account: [Sign in again].</summary>
    public event EventHandler? ReloginRequested;

    /// <summary>Extra account: [Cancel] while its sign-in runs.</summary>
    public event EventHandler? LoginCancelRequested;

    /// <summary>Extra account: [Remove account] (the page confirms).</summary>
    public event EventHandler? RemoveRequested;

    /// <summary>True while any account sign-in runs on the page: [Sign in again] and [Remove account] are off.</summary>
    public void SetLoginBusy(bool busy)
    {
        _loginBusy = busy;
        UpdateAccountButtons();
    }

    /// <summary>
    /// Extra account: the result of the page's PATH check for this AI's CLI (reported once the startup account clean-up
    /// is done too). Only true turns [Sign in again] on; false also explains why; null (not checked yet) keeps it off.
    /// </summary>
    public void SetCliInstalled(bool? installed)
    {
        _cliInstalled = installed;
        CliMissingText.Text = ProviderIds.KindOf(_settings.Id) == ProviderIds.Codex
            ? UiStrings.AccountReloginCodexMissing
            : UiStrings.AccountReloginClaudeMissing;
        UpdateAccountButtons();
    }

    /// <summary>This card's sign-in started: the progress line with [Cancel] replaces [Sign in again].</summary>
    public void ShowLoginProgress()
    {
        _loginRunning = true;
        ShowLoginMessage(null, isError: false);
        UpdateAccountButtons();
    }

    /// <summary>This card's sign-in ended: null = signed in, otherwise why not. Cancelled: <see cref="HideLoginProgress"/>.</summary>
    public void ShowLoginResult(AccountLoginFailure? failure)
    {
        HideLoginProgress();
        ShowLoginMessage(failure is AccountLoginFailure reason ? AccountsSection.FailureText(reason) : UiStrings.AccountReloginDone,
            isError: failure is not null);
    }

    public void HideLoginProgress()
    {
        _loginRunning = false;
        UpdateAccountButtons();
    }

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

    /// <summary>
    /// Updates the name, subtitle, status line, badge, connection guide (or account actions), Copilot panel visibility and
    /// line options. <paramref name="view"/> is the labeled view (AppHost.LabeledView); <paramref name="panelsOfKind"/>
    /// counts the cards of this AI on the page. UI thread.
    /// </summary>
    public void Refresh(ProviderView? view, DateTimeOffset now, int panelsOfKind)
    {
        SetTitle(view?.IdentityTitle ?? _provider.DisplayName);
        SetSubtitle(Subtitle(panelsOfKind));

        string status = view is null ? "" : ProviderStatusText.ForSettings(view, now, TimeZoneInfo.Local);
        StatusText.Text = status;
        StatusText.ToolTip = status.Length > 0 ? status : null;
        Badge.Kind = view is null ? StatusBadgeKind.Neutral : StatusBadge.KindFor(view.Current.Status);

        if (_isAccount)
        {
            _signedOut = view?.Current.Status == ProviderStatus.SignedOut;
            UpdateAccountButtons();
        }
        else
        {
            RefreshGuide(view);
        }

        RefreshExtra(view);
        RefreshOptions(view);
    }

    /// <summary>Releases the extra element (Copilot sign-in panel) when the page disposes its cards.</summary>
    public void DetachExtra() => ExtraHost.Content = null;

    private static string ControlName(string providerName, string control) =>
        string.Format(CultureInfo.CurrentCulture, UiStrings.ProviderControlNameFormat, providerName, control);

    /// <summary>The name and the UI Automation names that include it ("Claude · Team: Show on widget").</summary>
    private void SetTitle(string title)
    {
        if (string.Equals(title, _title, StringComparison.Ordinal))
        {
            return;
        }

        _title = title;
        NameText.Text = title;
        AutomationProperties.SetName(EnabledBox, ControlName(title, UiStrings.ProviderShowOnWidget));
        AutomationProperties.SetName(ExpandButton, ControlName(title, UiStrings.ProviderOptionsName));
        AutomationProperties.SetName(NicknameBox, ControlName(title, UiStrings.ProviderNicknameLabel));
        AutomationProperties.SetName(Line1Box, ControlName(title, UiStrings.ProviderLine1Label));
        AutomationProperties.SetName(Line2Box, ControlName(title, UiStrings.ProviderLine2Label));
        AutomationProperties.SetName(UpButton, ControlName(title, UiStrings.ProviderMoveUpButton));
        AutomationProperties.SetName(DownButton, ControlName(title, UiStrings.ProviderMoveDownButton));
        AutomationProperties.SetName(ReloginButton, ControlName(title, UiStrings.AccountReloginButton));
        AutomationProperties.SetName(LoginCancelButton, ControlName(title, UiStrings.AccountCancelButton));
        AutomationProperties.SetName(RemoveButton, ControlName(title, UiStrings.AccountRemoveButton));
    }

    /// <summary>
    /// Extra account: its email (Claude: the account folder's .claude.json; Codex: the id_token of the last read), when known.
    /// Built-in Claude/Codex: "Claude Code on this PC" / "Codex on this PC" while another panel of that AI is on the page.
    /// </summary>
    private string? Subtitle(int panelsOfKind)
    {
        if (_isAccount)
        {
            return _provider switch
            {
                ClaudeUsageProvider claude => claude.AccountEmail,
                CodexUsageProvider codex => codex.AccountEmail,
                _ => null,
            };
        }

        if (panelsOfKind < 2)
        {
            return null;
        }

        return _provider.Id switch
        {
            ProviderIds.Claude => UiStrings.AccountDefaultClaude,
            ProviderIds.Codex => UiStrings.AccountDefaultCodex,
            _ => null,
        };
    }

    private void SetSubtitle(string? subtitle)
    {
        SubtitleText.Text = subtitle ?? "";
        SubtitleText.ToolTip = string.IsNullOrWhiteSpace(subtitle) ? null : subtitle;
        SubtitleText.Visibility = string.IsNullOrWhiteSpace(subtitle) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// [Sign in again] is hidden while this card's sign-in runs (the progress line shows instead), off while another
    /// sign-in runs or the CLI is missing, and accent-styled while the account is signed out. [Remove account] is off
    /// during any sign-in.
    /// </summary>
    private void UpdateAccountButtons()
    {
        if (!_isAccount)
        {
            return;
        }

        ReloginButton.Visibility = _loginRunning ? Visibility.Collapsed : Visibility.Visible;
        LoginProgressPanel.Visibility = _loginRunning ? Visibility.Visible : Visibility.Collapsed;
        ReloginButton.IsEnabled = !_loginBusy && _cliInstalled == true;
        CliMissingText.Visibility = _cliInstalled == false && !_loginRunning ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.IsEnabled = !_loginBusy;

        if (_signedOut)
        {
            ReloginButton.SetResourceReference(StyleProperty, AccentButtonStyleKey);
        }
        else
        {
            ReloginButton.ClearValue(StyleProperty);
        }
    }

    private void ShowLoginMessage(string? message, bool isError)
    {
        LoginMessageText.Text = message ?? "";
        LoginMessageText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        LoginMessageText.SetResourceReference(
            TextBlock.ForegroundProperty, isError ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
    }

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

        _guide?.Show(guide, view.Current.Status);
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
