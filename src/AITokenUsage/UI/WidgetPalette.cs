using System.Windows.Media;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Settings;

namespace AITokenUsage.UI;

/// <summary>Frozen brushes for text drawn straight on the taskbar (also reused by the detail card).</summary>
public sealed class WidgetPalette
{
    public const double MutedOpacity = 0.65;
    public const double TrackOpacity = 0.18;

    /// <summary>White text, for a dark taskbar.</summary>
    public static readonly WidgetPalette LightText = new(Colors.White);

    /// <summary>Black text, for a light taskbar.</summary>
    public static readonly WidgetPalette DarkText = new(Colors.Black);

    private static readonly SolidColorBrush WarningBrush = FrozenBrush(ProviderGlyphs.WarningColor, 1);
    private static readonly SolidColorBrush CriticalBrush = FrozenBrush(ProviderGlyphs.CriticalColor, 1);

    private WidgetPalette(Color text)
    {
        TextColor = text;
        Text = FrozenBrush(text, 1);
        Muted = FrozenBrush(text, MutedOpacity);
        Track = FrozenBrush(text, TrackOpacity);
    }

    public Color TextColor { get; }

    public SolidColorBrush Text { get; }

    public SolidColorBrush Muted { get; }

    public SolidColorBrush Track { get; }

    /// <summary>"자동" follows the taskbar (SystemUsesLightTheme); "밝게"/"어둡게" override it.</summary>
    public static WidgetPalette Resolve(bool taskbarIsLight, TextColorMode mode) => mode switch
    {
        TextColorMode.Light => LightText,
        TextColorMode.Dark => DarkText,
        _ => taskbarIsLight ? DarkText : LightText,
    };

    /// <summary>Critical/warning colours win over the brand colour.</summary>
    public Brush BarBrush(string? providerId, Severity severity) => severity switch
    {
        Severity.Critical => CriticalBrush,
        Severity.Warning => WarningBrush,
        _ => ProviderGlyphs.BrandBrush(providerId),
    };

    private static SolidColorBrush FrozenBrush(Color color, double opacity)
    {
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }
}
