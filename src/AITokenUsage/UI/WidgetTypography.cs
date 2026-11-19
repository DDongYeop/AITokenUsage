using System.Windows;
using System.Windows.Media;
using AITokenUsage.Core.Settings;

namespace AITokenUsage.UI;

public static class WidgetTypography
{
    // Scope the resources to the widget content, including the hidden transition panel.
    public static void Apply(FrameworkElement root, TextWeightMode weight, bool smooth)
    {
        root.Resources["WidgetBodyWeight"] = weight == TextWeightMode.Bold ? FontWeights.Bold : FontWeights.Normal;
        root.Resources["WidgetEmphasisWeight"] = weight switch
        {
            TextWeightMode.Normal => FontWeights.Normal,
            TextWeightMode.Bold => FontWeights.Bold,
            _ => FontWeights.SemiBold,
        };
        root.Resources["WidgetMarkWeight"] = weight == TextWeightMode.Normal ? FontWeights.Normal : FontWeights.Bold;
        if (smooth)
        {
            TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(root, TextRenderingMode.Grayscale);
        }
        else
        {
            root.ClearValue(TextOptions.TextFormattingModeProperty);
            root.ClearValue(TextOptions.TextRenderingModeProperty);
        }
    }
}
