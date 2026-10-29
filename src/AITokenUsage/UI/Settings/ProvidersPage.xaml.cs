using System.Windows;
using System.Windows.Threading;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers.Copilot;
using AITokenUsage.Core.Settings;

namespace AITokenUsage.UI.Settings;

/// <summary>
/// "AI connections" page (v1.1 spec §4.3): one <see cref="ProviderCard"/> per AI in the settings order. Cards follow
/// AppHost.ViewChanged and a 30 s timer (relative "updated 3 min ago" texts); reordering moves the existing cards.
/// </summary>
public partial class ProvidersPage : SettingsPage
{
    private static readonly TimeSpan StatusRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly DispatcherTimer _statusTimer;
    private readonly List<ProviderCard> _cards = [];
    private readonly CopilotSignInPanel? _copilotPanel;

    public ProvidersPage(AppHost host)
        : base(host)
    {
        InitializeComponent();

        CopilotUsageProvider? copilot = host.Providers.OfType<CopilotUsageProvider>().FirstOrDefault();
        if (copilot is not null)
        {
            _copilotPanel = new CopilotSignInPanel(host, copilot);
        }

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = StatusRefreshInterval };
        _statusTimer.Tick += OnStatusTimer;
        Host.ViewChanged += OnViewChanged;
    }

    protected override void OnLoad()
    {
        if (_cards.Count == 0)
        {
            CreateCards();
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

        foreach (ProviderCard card in _cards)
        {
            card.MoveRequested -= OnMoveRequested;
            card.Changed -= OnCardChanged;
            card.DetachExtra();
        }

        _copilotPanel?.Dispose();
    }

    private void CreateCards()
    {
        foreach (ProviderSettings settings in Host.Settings.Providers)
        {
            // Settings ids are normalized to the catalog, but skip anything without a provider rather than fail the page.
            if (!Host.Providers.Any(p => p.Id == settings.Id))
            {
                continue;
            }

            UIElement? extra = settings.Id == ProviderIds.Copilot ? _copilotPanel : null;
            var card = new ProviderCard(Host, settings, extra);
            card.MoveRequested += OnMoveRequested;
            card.Changed += OnCardChanged;
            _cards.Add(card);
        }
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
        DateTimeOffset now = Now();
        foreach (ProviderCard card in _cards)
        {
            card.Refresh(Host.Usage.GetView(card.ProviderId), now);
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

        DateTimeOffset now = Now();
        foreach (ProviderCard card in _cards)
        {
            if (card.ProviderId == view.ProviderId)
            {
                card.Refresh(view, now);
            }
        }
    }

    private void OnCardChanged(object? sender, EventArgs e)
    {
        if (!IsLoading)
        {
            ApplySettings();
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
            card.FocusMoveButton(delta);
        });
    }

    private DateTimeOffset Now() => Host.ProviderContext.Time.GetUtcNow();
}
