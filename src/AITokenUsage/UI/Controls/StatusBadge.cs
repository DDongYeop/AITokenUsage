using System.Windows;
using System.Windows.Controls;
using AITokenUsage.Core.Models;

namespace AITokenUsage.UI.Controls;

/// <summary>Colour of a <see cref="StatusBadge"/> dot (v1.1 spec §4.2).</summary>
public enum StatusBadgeKind
{
    /// <summary>Grey: not installed / not supported.</summary>
    Neutral,

    /// <summary>Green #2EA043: connected.</summary>
    Connected,

    /// <summary>Orange #F5A524: sign-in needed / waiting for the CLI to refresh.</summary>
    Warning,

    /// <summary>Red #F04438: error / rate limited.</summary>
    Error,
}

/// <summary>A coloured dot and a short status text. The template lives in SettingsStyles.xaml.</summary>
public sealed class StatusBadge : Control
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(StatusBadgeKind), typeof(StatusBadge), new FrameworkPropertyMetadata(StatusBadgeKind.Neutral));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(StatusBadge), new FrameworkPropertyMetadata(""));

    static StatusBadge()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(StatusBadge), new FrameworkPropertyMetadata(typeof(StatusBadge)));
        FocusableProperty.OverrideMetadata(typeof(StatusBadge), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(StatusBadge), new FrameworkPropertyMetadata(false));
    }

    public StatusBadgeKind Kind
    {
        get => (StatusBadgeKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The badge colour for a provider status (v1.1 spec §4.2 table).</summary>
    public static StatusBadgeKind KindFor(ProviderStatus status) => status switch
    {
        ProviderStatus.Ok => StatusBadgeKind.Connected,
        ProviderStatus.SignedOut or ProviderStatus.WaitingForCliRefresh => StatusBadgeKind.Warning,
        ProviderStatus.Error or ProviderStatus.RateLimited => StatusBadgeKind.Error,
        _ => StatusBadgeKind.Neutral,
    };
}
