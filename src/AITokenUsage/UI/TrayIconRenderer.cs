using AITokenUsage.Core.Models;
using AITokenUsage.Core.Settings;
using AITokenUsage.Core.Util;
using Drawing = System.Drawing;

namespace AITokenUsage.UI;

/// <summary>Draws the tray icon: a filled circle in the bar color with the line-1 number inside.</summary>
internal static class TrayIconRenderer
{
    public const int NoDataArgb = unchecked((int)0xFF6E6E6E);
    public const int WarningArgb = unchecked((int)0xFFF5A524);
    public const int CriticalArgb = unchecked((int)0xFFF04438);
    private const int StaleAlphaMask = unchecked((int)0x99000000);

    public readonly record struct IconSpec(string Text, int Argb);

    /// <summary>Brand colors from spec §2.1 (Task 17's ProviderGlyphs uses the same values).</summary>
    public static int BrandArgb(string providerId) => providerId switch
    {
        ProviderIds.Claude => unchecked((int)0xFFD97757),
        ProviderIds.Codex => unchecked((int)0xFF10A37F),
        ProviderIds.Gemini => unchecked((int)0xFF4285F4),
        ProviderIds.Copilot => unchecked((int)0xFF8957E5),
        _ => NoDataArgb,
    };

    public static IconSpec CreateSpec(ProviderView? view, UsageMeter? line1, ValueMode mode)
    {
        string text = TrayTextFormatter.IconText(line1, mode);
        if (view is null || line1?.UsedPercent is null)
        {
            return new IconSpec(text, NoDataArgb);
        }

        int argb = SeverityRules.Effective(line1) switch
        {
            Severity.Critical => CriticalArgb,
            Severity.Warning => WarningArgb,
            _ => BrandArgb(view.ProviderId),
        };

        if (view.IsStale)
        {
            argb = (argb & 0x00FFFFFF) | StaleAlphaMask;
        }

        return new IconSpec(text, argb);
    }

    public static Drawing.Bitmap DrawBitmap(IconSpec spec, int size)
    {
        var bitmap = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using Drawing.Graphics graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(Drawing.Color.Transparent);
        graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAlias;

        using (var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(spec.Argb)))
        {
            graphics.FillEllipse(fill, 0.5f, 0.5f, size - 1f, size - 1f);
        }

        float fontPx = size * (spec.Text.Length >= 3 ? 0.40f : 0.56f);
        using var font = new Drawing.Font("Segoe UI", fontPx, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
        using var format = new Drawing.StringFormat
        {
            Alignment = Drawing.StringAlignment.Center,
            LineAlignment = Drawing.StringAlignment.Center,
            FormatFlags = Drawing.StringFormatFlags.NoWrap | Drawing.StringFormatFlags.NoClip,
        };
        using var textBrush = new Drawing.SolidBrush(Drawing.Color.White);
        graphics.DrawString(spec.Text, font, textBrush, new Drawing.RectangleF(0, 0, size, size), format);
        return bitmap;
    }
}
