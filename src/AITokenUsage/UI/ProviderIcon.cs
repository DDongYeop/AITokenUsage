using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Settings;
using AITokenUsage.Core.SystemUsage;
using GlyphPath = System.Windows.Shapes.Path;

namespace AITokenUsage.UI;

public sealed class ProviderIcon : ContentControl
{
    private static readonly DropShadowEffect LightOutline = Outline(Colors.White);
    private static readonly DropShadowEffect DarkOutline = Outline(Colors.Black);

    // UI-thread only. All accounts of one service share the OS lookup, including an in-flight one.
    private static readonly Dictionary<string, Task<ImageSource?>> Installed = new(StringComparer.Ordinal);
    private long _revision;

    internal void Render(string? id, ProviderIconStyle style, bool darkBackground, Brush neutral)
    {
        long revision = ++_revision;
        ImageSource? source = null;
        if (!string.IsNullOrEmpty(id) && id != SystemPanel.Id)
        {
            if (style == ProviderIconStyle.Service)
            {
                string kind = ProviderIds.KindOf(id);
                if (!Installed.TryGetValue(kind, out var lookup))
                {
                    lookup = Task.Run(() => LoadInstalled(id));
                    Installed[kind] = lookup;
                }

                if (lookup.IsCompletedSuccessfully) source = lookup.Result;
                else _ = ShowWhenReady(lookup, revision, darkBackground);
            }
            else if (style == ProviderIconStyle.Custom)
            {
                source = CustomProviderIcons.Load(id);
            }
        }

        if (source is not null) ShowImage(source, darkBackground);
        else if (Content is GlyphPath path) ProviderGlyphs.Apply(path, id, neutral);
        else Content = ProviderGlyphs.CreateIcon(id, Width, neutral);
    }

    private static ImageSource? LoadInstalled(string id)
    {
        try
        {
            return InstalledServiceIcons.Load(id);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Installed icon lookup for {id} failed: {ex.Message}");
            return null;
        }
    }

    private async Task ShowWhenReady(Task<ImageSource?> lookup, long revision, bool darkBackground)
    {
        var source = await lookup.ConfigureAwait(false);
        if (source is null || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_revision == revision) ShowImage(source, darkBackground);
        }));
    }

    private void ShowImage(ImageSource source, bool darkBackground)
    {
        if (Content is not Image image)
        {
            image = new Image { Width = Width, Height = Height, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            Content = image;
        }

        image.Source = source;
        image.Effect = darkBackground ? LightOutline : DarkOutline;
    }

    // A narrow contrasting edge keeps monochrome artwork visible without tinting its pixels.
    private static DropShadowEffect Outline(Color color)
    {
        var effect = new DropShadowEffect { Color = color, ShadowDepth = 0, BlurRadius = 1.5, Opacity = 0.8 };
        effect.Freeze();
        return effect;
    }
}
