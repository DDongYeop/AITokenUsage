using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Presentation;
using AITokenUsage.Resources;

namespace AITokenUsage.UI.Settings;

/// <summary>
/// The "how to connect" steps of a provider card (v1.1 spec §4.4): a numbered row per step, a copyable command box,
/// [Sign in from terminal] / [Open terminal] and [Check again] buttons, and the footnote.
/// </summary>
public partial class ConnectionGuideView : UserControl
{
    private const double StepNumberSize = 18;
    private static readonly FontFamily CommandFont = new("Cascadia Mono, Consolas");

    /// <summary>How long a copy button says "Copied" before it reverts to "Copy".</summary>
    private static readonly TimeSpan CopiedFeedbackDuration = TimeSpan.FromSeconds(2);

    private readonly AppHost _host;
    private readonly string _providerId;
    private readonly string _providerName;
    private readonly DispatcherTimer _copiedTimer;
    private Button? _copiedButton;

    /// <param name="providerName">Display name used in the buttons' UI Automation names ("Claude: Check again").</param>
    public ConnectionGuideView(AppHost host, string providerId, string providerName)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        _host = host;
        _providerId = providerId;
        _providerName = providerName;
        InitializeComponent();

        _copiedTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = CopiedFeedbackDuration };
        _copiedTimer.Tick += (_, _) => RevertCopiedButton();

        // Page switch or window close: never leave the timer running for a view that is gone.
        Unloaded += (_, _) => RevertCopiedButton();
    }

    /// <summary>
    /// Replaces the shown steps. <paramref name="status"/> picks the terminal button label: Claude waiting for its CLI to
    /// refresh the login gets [Open terminal], every other terminal step [Sign in from terminal].
    /// </summary>
    public void Show(ConnectionGuide guide, ProviderStatus status)
    {
        ArgumentNullException.ThrowIfNull(guide);

        RevertCopiedButton();
        StepsPanel.Children.Clear();
        for (int i = 0; i < guide.Steps.Count; i++)
        {
            FrameworkElement row = CreateStepRow(i + 1, guide.Steps[i], status);
            row.Margin = new Thickness(0, 0, 0, i < guide.Steps.Count - 1 ? 10 : 0);
            StepsPanel.Children.Add(row);
        }

        FootnoteText.Text = guide.Footnote ?? "";
        FootnoteText.Visibility = string.IsNullOrWhiteSpace(guide.Footnote) ? Visibility.Collapsed : Visibility.Visible;
        FootnoteText.Margin = new Thickness(0, guide.Steps.Count > 0 ? 10 : 0, 0, 0);
    }

    private FrameworkElement CreateStepRow(int number, GuideStep step, ProviderStatus status)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(CreateStepNumber(number));

        var body = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        body.Children.Add(new TextBlock
        {
            Text = step.Text,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            MinHeight = StepNumberSize,
        });

        if (!string.IsNullOrWhiteSpace(step.Command))
        {
            body.Children.Add(CreateCommandBox(step.Command));
        }

        switch (step.Action)
        {
            case GuideAction.OpenTerminal when !string.IsNullOrWhiteSpace(step.TerminalCommand):
                AddTerminalButton(body, step.TerminalCommand, status);
                break;
            case GuideAction.Recheck:
                body.Children.Add(CreateRecheckButton());
                break;
        }

        return grid;
    }

    private static Border CreateStepNumber(int number)
    {
        var text = new TextBlock
        {
            Text = number.ToString(CultureInfo.CurrentCulture),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");

        var circle = new Border
        {
            Width = StepNumberSize,
            Height = StepNumberSize,
            CornerRadius = new CornerRadius(StepNumberSize / 2),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
            Child = text,
        };
        circle.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
        return circle;
    }

    /// <summary>The command in a monospace box with a [Copy] button next to it.</summary>
    private Grid CreateCommandBox(string command)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var commandText = new TextBlock
        {
            Text = command,
            FontFamily = CommandFont,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var box = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 5, 8, 5),
            Child = commandText,
        };
        box.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        box.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        grid.Children.Add(box);

        var copy = new Button
        {
            Content = UiStrings.GuideCopyButton,
            MinWidth = 0,
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(copy, ControlName(string.Format(CultureInfo.CurrentCulture, UiStrings.GuideCopyCommandNameFormat, command)));
        copy.Click += (_, _) => CopyCommand(copy, command);
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);

        return grid;
    }

    /// <summary>
    /// One attempt, no retry delay (like the Copilot device code): WPF's Clipboard.SetText retries for up to ~1 s on the UI
    /// thread, which the taskbar widget shares with explorer's input queue. A busy clipboard is ignored; click again.
    /// </summary>
    private void CopyCommand(Button button, string command)
    {
        try
        {
            System.Windows.Forms.Clipboard.SetDataObject(command, copy: true, retryTimes: 0, retryDelay: 0);
        }
        catch (ExternalException ex)
        {
            _host.Log.Warn("Copying a connection guide command to the clipboard failed.", ex);
            return;
        }

        // One "Copied" at a time: a second copy reverts the previous button and restarts the 2 s feedback.
        RevertCopiedButton();
        button.Content = UiStrings.GuideCopiedButton;
        _copiedButton = button;
        _copiedTimer.Start();
    }

    private void RevertCopiedButton()
    {
        _copiedTimer.Stop();
        if (_copiedButton is not null)
        {
            _copiedButton.Content = UiStrings.GuideCopyButton;
            _copiedButton = null;
        }
    }

    private string ControlName(string control) =>
        string.Format(CultureInfo.CurrentCulture, UiStrings.ProviderControlNameFormat, _providerName, control);

    /// <summary>
    /// [Sign in from terminal] (or [Open terminal]) plus a hidden failure notice. The step has no copyable command of its
    /// own, so the notice also shows the terminal command in a command box for running it by hand.
    /// </summary>
    private void AddTerminalButton(StackPanel body, string terminalCommand, ProviderStatus status)
    {
        string label = status == ProviderStatus.WaitingForCliRefresh ? UiStrings.GuideOpenTerminalButton : UiStrings.GuideSignInTerminalButton;
        var button = new Button
        {
            Content = label,
            MinWidth = 0,
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(button, ControlName(label));
        body.Children.Add(button);

        var failureText = new TextBlock
        {
            Text = UiStrings.GuideTerminalFailed,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        failureText.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        AutomationProperties.SetLiveSetting(failureText, AutomationLiveSetting.Assertive);
        var failure = new StackPanel { Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        failure.Children.Add(failureText);
        failure.Children.Add(CreateCommandBox(terminalCommand));
        body.Children.Add(failure);

        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            failure.Visibility = Visibility.Collapsed;
            bool opened;
            try
            {
                opened = await TerminalLauncher.Launch(terminalCommand, _host.Log);
            }
            catch (Exception ex)
            {
                _host.Log.Error("Opening a terminal from the connection guide failed.", ex);
                opened = false;
            }

            button.IsEnabled = true;
            failure.Visibility = opened ? Visibility.Collapsed : Visibility.Visible;
            if (!opened)
            {
                AnnounceWhenShown(failureText);
            }
        };
    }

    /// <summary>
    /// Screen readers announce a live region only when it raises LiveRegionChanged; wait for layout so the text is
    /// visible (and its peer in the tree) when the event fires.
    /// </summary>
    private void AnnounceWhenShown(TextBlock text) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!text.IsVisible)
            {
                return;
            }

            AutomationPeer? peer = UIElementAutomationPeer.FromElement(text) ?? UIElementAutomationPeer.CreatePeerForElement(text);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }));

    /// <summary>[Check again]: refreshes only this provider now; disabled until the result is published.</summary>
    private Button CreateRecheckButton()
    {
        var button = new Button
        {
            Content = UiStrings.GuideRecheckButton,
            MinWidth = 0,
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(button, ControlName(UiStrings.GuideRecheckButton));
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                await _host.Usage.RefreshNowAsync(_providerId);
            }
            catch (Exception ex)
            {
                _host.Log.Warn($"re-check of {_providerId} from the connection guide failed", ex);
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        return button;
    }
}
