using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using AITokenUsage.Accounts;
using AITokenUsage.Core.Accounts;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers.Copilot;
using AITokenUsage.Core.Settings;
using AITokenUsage.Resources;

namespace AITokenUsage.UI.Settings;

/// <summary>
/// "AI connections" page (v1.1 spec §4.3): one <see cref="ProviderCard"/> per AI in the settings order. Cards follow
/// AppHost.ViewChanged and a 30 s timer (relative "updated 3 min ago" texts); reordering moves the existing cards.
/// Below the cards, <see cref="AccountsSection"/> adds extra Claude/Codex accounts (multi-account spec §5). The page runs
/// one <see cref="AccountLoginSession"/> at a time (add or sign in again) and cancels it when it is disposed.
/// </summary>
public partial class ProvidersPage : SettingsPage
{
    private static readonly TimeSpan StatusRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly DispatcherTimer _statusTimer;
    private readonly List<ProviderCard> _cards = [];
    private readonly CopilotSignInPanel? _copilotPanel;
    private readonly AccountsSection _accounts;
    private AccountLoginSession? _session;

    /// <summary>The card whose "sign in again" runs; null while an add runs (or nothing does).</summary>
    private ProviderCard? _sessionCard;

    public ProvidersPage(AppHost host)
        : base(host)
    {
        InitializeComponent();

        CopilotUsageProvider? copilot = host.Providers.OfType<CopilotUsageProvider>().FirstOrDefault();
        if (copilot is not null)
        {
            _copilotPanel = new CopilotSignInPanel(host, copilot);
        }

        _accounts = new AccountsSection(host);
        _accounts.AddRequested += OnAddRequested;
        _accounts.CancelRequested += OnCancelLoginRequested;
        _accounts.InstalledChecked += OnInstalledChecked;
        AccountsHost.Content = _accounts;

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = StatusRefreshInterval };
        _statusTimer.Tick += OnStatusTimer;
        Host.ViewChanged += OnViewChanged;
    }

    protected override void OnLoad()
    {
        if (_cards.Count == 0)
        {
            CreateCards();
            _accounts.CheckInstalled();
        }

        ArrangeCards();
        RefreshAll();
        _statusTimer.Start();
    }

    protected override void OnDispose()
    {
        _statusTimer.Stop();
        _statusTimer.Tick -= OnStatusTimer;
        Host.ViewChanged -= OnViewChanged;

        // Window closed (or the app restarting) while a sign-in waits: cancel it, which cleans up like [Cancel].
        EndSession(cancel: true);
        _accounts.AddRequested -= OnAddRequested;
        _accounts.CancelRequested -= OnCancelLoginRequested;
        _accounts.InstalledChecked -= OnInstalledChecked;
        _accounts.Dispose();

        foreach (ProviderCard card in _cards)
        {
            UnhookCard(card);
            card.DetachExtra();
        }

        _copilotPanel?.Dispose();
    }

    private void CreateCards()
    {
        foreach (ProviderSettings settings in Host.Settings.Providers)
        {
            // Settings ids are normalized to the catalog, but skip anything without a provider rather than fail the page.
            // An account added in this session has no provider until the app restarts, so it has no card yet either.
            if (!Host.Providers.Any(p => p.Id == settings.Id))
            {
                continue;
            }

            UIElement? extra = settings.Id == ProviderIds.Copilot ? _copilotPanel : null;
            var card = new ProviderCard(Host, settings, extra);
            card.MoveRequested += OnMoveRequested;
            card.Changed += OnCardChanged;
            card.ReloginRequested += OnReloginRequested;
            card.LoginCancelRequested += OnCancelLoginRequested;
            card.RemoveRequested += OnRemoveRequested;
            _cards.Add(card);
        }
    }

    private void UnhookCard(ProviderCard card)
    {
        card.MoveRequested -= OnMoveRequested;
        card.Changed -= OnCardChanged;
        card.ReloginRequested -= OnReloginRequested;
        card.LoginCancelRequested -= OnCancelLoginRequested;
        card.RemoveRequested -= OnRemoveRequested;
    }

    /// <summary>Puts the existing cards in the settings order and updates which reorder buttons are enabled.</summary>
    private void ArrangeCards()
    {
        List<ProviderSettings> order = Host.Settings.Providers;
        ProviderCard[] arranged = _cards
            .OrderBy(card =>
            {
                int index = order.FindIndex(p => p.Id == card.ProviderId);
                return index < 0 ? int.MaxValue : index;
            })
            .ToArray();

        CardsPanel.Children.Clear();
        for (int i = 0; i < arranged.Length; i++)
        {
            arranged[i].SetMoveState(i > 0, i < arranged.Length - 1);
            CardsPanel.Children.Add(arranged[i]);
        }
    }

    private void RefreshAll()
    {
        _copilotPanel?.Refresh();
        RefreshCards();
    }

    /// <summary>
    /// Every card at once: a change to one panel (its plan, its "show on widget") can change the tags of the others
    /// ("Claude · Team"), so the names are recomputed from the labeled views together.
    /// </summary>
    private void RefreshCards()
    {
        DateTimeOffset now = Now();
        IReadOnlyList<ProviderView> labeled = Host.EnabledViewsInOrder();
        string[] cardIds = _cards.Select(c => c.ProviderId).ToArray();
        foreach (ProviderCard card in _cards)
        {
            ProviderView? view = labeled.FirstOrDefault(v => string.Equals(v.ProviderId, card.ProviderId, StringComparison.Ordinal))
                ?? Host.Usage.GetView(card.ProviderId);
            int panelsOfKind = AccountSettingsEditor.CountOfKind(cardIds, ProviderIds.KindOf(card.ProviderId));
            card.Refresh(view, now, panelsOfKind);
        }
    }

    private void OnStatusTimer(object? sender, EventArgs e)
    {
        if (!IsDisposed)
        {
            RefreshAll();
        }
    }

    /// <summary>AppHost raises ViewChanged on the UI thread; marshal anyway so a future caller cannot break the cards.</summary>
    private void OnViewChanged(object? sender, ProviderView view)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => OnViewChanged(sender, view));
            return;
        }

        if (IsDisposed)
        {
            return;
        }

        if (view.ProviderId == ProviderIds.Copilot)
        {
            _copilotPanel?.Refresh();
        }

        RefreshCards();
    }

    private void OnCardChanged(object? sender, EventArgs e)
    {
        if (!IsLoading)
        {
            ApplySettings();

            // "Show on widget" changes which panels get a tag.
            RefreshCards();
        }
    }

    private void OnMoveRequested(object? sender, int delta)
    {
        if (sender is not ProviderCard card || IsLoading)
        {
            return;
        }

        List<ProviderSettings> list = Host.Settings.Providers;
        int index = list.FindIndex(p => p.Id == card.ProviderId);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= list.Count)
        {
            return;
        }

        (list[index], list[target]) = (list[target], list[index]);
        ApplySettings();

        // The clicked button belongs to a card that is about to leave and re-enter the panel; move it after the click.
        Dispatcher.InvokeAsync(() =>
        {
            if (IsDisposed)
            {
                return;
            }

            ArrangeCards();
            RefreshCards();
            card.FocusMoveButton(delta);
        });
    }

    // ---- Account sign-in (add / sign in again) ----

    private void OnAddRequested(object? sender, string kind)
    {
        if (_session is not null || IsDisposed)
        {
            return;
        }

        // Also skip ids of providers still running after a removal that has not restarted yet.
        IEnumerable<string> inUse = Host.Settings.Providers.Select(p => p.Id).Concat(Host.Providers.Select(p => p.Id));
        string accountId = AccountSettingsEditor.NewAccountId(kind, inUse);
        _accounts.ShowProgress();
        StartSession(accountId, isNew: true, card: null);
    }

    private void OnReloginRequested(object? sender, EventArgs e)
    {
        if (sender is not ProviderCard card || _session is not null || IsDisposed)
        {
            return;
        }

        card.ShowLoginProgress();
        StartSession(card.ProviderId, isNew: false, card);
    }

    private void StartSession(string accountId, bool isNew, ProviderCard? card)
    {
        var session = new AccountLoginSession(Host, accountId, isNew);
        session.Completed += OnSessionCompleted;
        session.Failed += OnSessionFailed;
        _session = session;
        _sessionCard = card;
        UpdateLoginBusy();
        session.Start();
    }

    /// <summary>[Sign in again] needs the same CLI as adding: the section's PATH check result goes to the account cards.</summary>
    private void OnInstalledChecked(object? sender, EventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        foreach (ProviderCard card in _cards)
        {
            if (AccountIds.IsAccountId(card.ProviderId))
            {
                card.SetCliInstalled(_accounts.IsInstalled(ProviderIds.KindOf(card.ProviderId)));
            }
        }
    }

    private void OnCancelLoginRequested(object? sender, EventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        ProviderCard? card = _sessionCard;
        if (_session.HasAdded)
        {
            // Too late to cancel: the account is already in the settings. Show it as added (restart notice).
            EndSession(cancel: false);
            _accounts.ShowAdded();
            return;
        }

        EndSession(cancel: true);
        if (card is null)
        {
            _accounts.HideProgress();
        }
        else
        {
            card.HideLoginProgress();
        }
    }

    private void OnSessionCompleted(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _session) || IsDisposed)
        {
            return;
        }

        ProviderCard? card = _sessionCard;
        EndSession(cancel: false);
        if (card is null)
        {
            _accounts.ShowAdded();
        }
        else
        {
            card.ShowLoginResult(null);
        }

        RefreshCards();
    }

    private void OnSessionFailed(object? sender, AccountLoginFailure failure)
    {
        if (!ReferenceEquals(sender, _session) || IsDisposed)
        {
            return;
        }

        ProviderCard? card = _sessionCard;
        EndSession(cancel: false);
        if (card is null)
        {
            _accounts.ShowFailure(failure);
        }
        else
        {
            card.ShowLoginResult(failure);
        }
    }

    private void EndSession(bool cancel)
    {
        if (_session is not AccountLoginSession session)
        {
            return;
        }

        session.Completed -= OnSessionCompleted;
        session.Failed -= OnSessionFailed;
        if (cancel)
        {
            session.Cancel();
        }

        _session = null;
        _sessionCard = null;
        if (!IsDisposed)
        {
            UpdateLoginBusy();
        }
    }

    /// <summary>One sign-in at a time: every add / sign-in-again / remove button is off while one runs.</summary>
    private void UpdateLoginBusy()
    {
        bool busy = _session is not null;
        _accounts.SetBusy(busy);
        foreach (ProviderCard card in _cards)
        {
            card.SetLoginBusy(busy);
        }
    }

    // ---- Account removal ----

    /// <summary>Posted after the click: the confirmation is modal and the card leaves the panel afterwards.</summary>
    private void OnRemoveRequested(object? sender, EventArgs e)
    {
        if (sender is ProviderCard card)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => ConfirmAndRemove(card)));
        }
    }

    /// <summary>
    /// Yes/No (default No). Yes: the entry leaves the settings (saved), then the app restarts like a language change.
    /// The account's login and folder stay until the next start's sweep: its provider is still running now.
    /// </summary>
    private void ConfirmAndRemove(ProviderCard card)
    {
        if (IsDisposed || IsLoading || _session is not null || !_cards.Contains(card))
        {
            return;
        }

        string message = string.Format(CultureInfo.CurrentCulture, UiStrings.AccountRemoveConfirmFormat, card.Title);
        Window? owner = Window.GetWindow(this);
        MessageBoxResult answer = owner is null
            ? MessageBox.Show(message, UiStrings.AccountRemoveButton, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            : MessageBox.Show(owner, message, UiStrings.AccountRemoveButton, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes || IsDisposed)
        {
            return;
        }

        string accountId = card.ProviderId;
        AccountSettingsEditor.Remove(Host.Settings.Providers, accountId);
        ApplySettings();

        // The card goes now, so a failed restart still leaves the page matching the settings.
        UnhookCard(card);
        card.DetachExtra();
        _cards.Remove(card);
        ArrangeCards();
        RefreshCards();

        Host.Log.Info($"account {accountId} removed from the settings window; restarting");
        LanguageBootstrap.Restart(Host);
    }

    private DateTimeOffset Now() => Host.ProviderContext.Time.GetUtcNow();
}
