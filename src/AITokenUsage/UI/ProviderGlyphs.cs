using System.Windows;
using System.Windows.Media;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.SystemUsage;
using Path = System.Windows.Shapes.Path;

namespace AITokenUsage.UI;

/// <summary>Simple geometric provider marks (not brand logos) and the spec §2.1 colours.</summary>
public static class ProviderGlyphs
{
    public const double DesignSize = 14;
    public const double StrokeThickness = 1.6;

    public static readonly Color ClaudeColor = Color.FromRgb(0xD9, 0x77, 0x57);
    public static readonly Color CodexColor = Color.FromRgb(0x10, 0xA3, 0x7F);
    public static readonly Color GeminiColor = Color.FromRgb(0x42, 0x85, 0xF4);
    public static readonly Color CopilotColor = Color.FromRgb(0x89, 0x57, 0xE5);
    public static readonly Color NeutralColor = Color.FromRgb(0x8A, 0x8A, 0x8A);

    /// <summary>The "PC" panel's neutral grey (design §3.1): its icon and normal bars.</summary>
    public static readonly Color SystemColor = Color.FromRgb(0x8B, 0x94, 0x9E);
    public static readonly Color WarningColor = Color.FromRgb(0xF5, 0xA5, 0x24);
    public static readonly Color CriticalColor = Color.FromRgb(0xF0, 0x44, 0x38);

    private static readonly Geometry ClaudeGeometry =
        Frozen(Geometry.Parse("M7,0.8 L7,13.2 M0.8,7 L13.2,7 M2.6,2.6 L11.4,11.4 M11.4,2.6 L2.6,11.4"));

    private static readonly Geometry CodexGeometry = Frozen(new GeometryGroup
    {
        Children = new GeometryCollection
        {
            new EllipseGeometry(new Point(7, 7), 5.8, 5.8),
            new EllipseGeometry(new Point(7, 7), 2.4, 2.4),
        },
    });

    private static readonly Geometry GeminiGeometry =
        Frozen(Geometry.Parse("M7,0 C7.5,4.5 9.5,6.5 14,7 C9.5,7.5 7.5,9.5 7,14 C6.5,9.5 4.5,7.5 0,7 C4.5,6.5 6.5,4.5 7,0 Z"));

    private static readonly Geometry CopilotGeometry =
        Frozen(Geometry.Parse("M7,1 L12.2,4 L12.2,10 L7,13 L1.8,10 L1.8,4 Z"));

    /// <summary>A plain monitor: screen, neck and foot (not a brand logo).</summary>
    private static readonly Geometry SystemGeometry =
        Frozen(Geometry.Parse("M1.5,2 L12.5,2 L12.5,9.5 L1.5,9.5 Z M7,9.5 L7,12 M4.5,12 L9.5,12"));

    private static readonly Geometry NeutralGeometry =
        Frozen(Geometry.Parse("M2.5,12 L2.5,7.5 M7,12 L7,2.5 M11.5,12 L11.5,9"));

    private static readonly Dictionary<string, SolidColorBrush> BrandBrushes = new(StringComparer.Ordinal)
    {
        [ProviderIds.Claude] = FrozenBrush(ClaudeColor),
        [ProviderIds.Codex] = FrozenBrush(CodexColor),
        [ProviderIds.Gemini] = FrozenBrush(GeminiColor),
        [ProviderIds.Copilot] = FrozenBrush(CopilotColor),
        [SystemPanel.Id] = FrozenBrush(SystemColor),
    };

    private static readonly SolidColorBrush NeutralBrush = FrozenBrush(NeutralColor);

    public static Color BrandColor(string? providerId) => providerId switch
    {
        ProviderIds.Claude => ClaudeColor,
        ProviderIds.Codex => CodexColor,
        ProviderIds.Gemini => GeminiColor,
        ProviderIds.Copilot => CopilotColor,
        SystemPanel.Id => SystemColor,
        _ => NeutralColor,
    };

    public static SolidColorBrush BrandBrush(string? providerId) =>
        providerId is not null && BrandBrushes.TryGetValue(providerId, out SolidColorBrush? brush) ? brush : NeutralBrush;

    public static Geometry GetGeometry(string? providerId) => providerId switch
    {
        ProviderIds.Claude => ClaudeGeometry,
        ProviderIds.Codex => CodexGeometry,
        ProviderIds.Gemini => GeminiGeometry,
        ProviderIds.Copilot => CopilotGeometry,
        SystemPanel.Id => SystemGeometry,
        _ => NeutralGeometry,
    };

    /// <summary>Gemini's star is filled; every other mark is a rounded stroke.</summary>
    public static bool IsFilled(string? providerId) => providerId == ProviderIds.Gemini;

    /// <summary>
    /// Points an existing 14×14 Path at the provider's mark in its brand colour.
    /// Unknown or empty ids (the "AI 사용량" empty state) use the neutral mark in <paramref name="neutralBrush"/>.
    /// </summary>
    public static void Apply(Path path, string? providerId, Brush neutralBrush)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(neutralBrush);

        bool known = providerId is not null && BrandBrushes.ContainsKey(providerId);
        Brush brush = known ? BrandBrush(providerId) : neutralBrush;

        path.Data = GetGeometry(providerId);
        path.Stretch = Stretch.None;

        if (IsFilled(providerId))
        {
            path.Fill = brush;
            path.Stroke = null;
            return;
        }

        path.Fill = null;
        path.Stroke = brush;
        path.StrokeThickness = StrokeThickness;
        path.StrokeStartLineCap = PenLineCap.Round;
        path.StrokeEndLineCap = PenLineCap.Round;
        path.StrokeLineJoin = PenLineJoin.Round;
    }

    /// <summary>A new Path showing the mark at <paramref name="size"/> DIP.</summary>
    public static Path CreateIcon(string? providerId, double size, Brush neutralBrush)
    {
        var path = new Path { Width = DesignSize, Height = DesignSize };
        Apply(path, providerId, neutralBrush);

        if (Math.Abs(size - DesignSize) > 0.01)
        {
            double scale = size / DesignSize;
            path.LayoutTransform = new ScaleTransform(scale, scale);
        }

        return path;
    }

    private static Geometry Frozen(Geometry geometry)
    {
        if (geometry.CanFreeze && !geometry.IsFrozen)
        {
            geometry.Freeze();
        }

        return geometry;
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
