using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.SystemUsage;
using AITokenUsage.Core.Taskbar;

namespace AITokenUsage.UI;

/// <summary>One provider's two-line mini bar. Draws a <see cref="WidgetPanelModel"/>; holds no state of its own.</summary>
public partial class ProviderPanel : UserControl
{
    public const double StaleOpacity = 0.55;
    public const double BarWidth = 44;

    /// <summary>Row label size of AI panels ("5h", "7d", "Pro"); matches the 24 DIP label column in ProviderPanel.xaml.</summary>
    private const double LabelFontSize = 10.5;

    /// <summary>
    /// The PC panel's labels "CPU"/"RAM" are wider than the AI ones and, at 10.5, fill the whole 24 DIP label column and touch
    /// the bar. A smaller size on this panel only leaves a visible gap without changing the shared column layout.
    /// </summary>
    private const double SystemLabelFontSize = 9;

    private const double DotSize = 5;
    private const double DotSpacing = 4;
    private const double ProviderNameMaxWidth = 42;
    private const double EmptyTitleMaxWidth = 62;

    private readonly RowParts _row1;
    private readonly RowParts _row2;

    public ProviderPanel()
    {
        InitializeComponent();
        _row1 = new RowParts(Row1, Row1Label, Row1Track, Row1Fill, Row1Value, Row1Text, Row1Reset, Row1Gap, Row1ResetColumn);
        _row2 = new RowParts(Row2, Row2Label, Row2Track, Row2Fill, Row2Value, Row2Text, Row2Reset, Row2Gap, Row2ResetColumn);
        RenderTransform = new TranslateTransform();
    }

    /// <summary>The slide offset animated by the widget's transition.</summary>
    public TranslateTransform Slide => (TranslateTransform)RenderTransform;

    /// <param name="showResetTime">Reserve the right-hand time-to-reset column (WidgetMetrics.ResetGapDip + ResetColumnDip) and fill it on meter rows.</param>
    public void Render(WidgetPanelModel model, WidgetPalette palette, bool showResetTime)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(palette);

        bool hasProvider = model.ProviderId.Length > 0;
        ProviderGlyphs.Apply(GlyphPath, model.ProviderId, palette.Text);
        GlyphPath.Visibility = hasProvider ? Visibility.Visible : Visibility.Collapsed;
        NameText.Margin = hasProvider ? new Thickness(4, 0, 0, 0) : new Thickness(0);
        NameText.MaxWidth = hasProvider ? ProviderNameMaxWidth : EmptyTitleMaxWidth;
        NameText.Text = model.DisplayName;
        NameText.Foreground = palette.Text;
        StaleMark.Foreground = palette.Text;
        StaleMark.Visibility = model.IsStale ? Visibility.Visible : Visibility.Collapsed;
        RenderDots(model, palette);

        if (model.Row2 is null && model.Row1.Kind == WidgetRowKind.Message)
        {
            Row1.Visibility = Visibility.Collapsed;
            Row2.Visibility = Visibility.Collapsed;
            MessageText.Text = model.Row1.Value;
            MessageText.Foreground = palette.Muted;
            MessageText.Visibility = Visibility.Visible;
            return;
        }

        MessageText.Visibility = Visibility.Collapsed;
        RenderRow(_row1, model.Row1, palette, model.ProviderId, showResetTime);

        if (model.Row2 is null)
        {
            Grid.SetRowSpan(Row1, 2);
            Row2.Visibility = Visibility.Collapsed;
        }
        else
        {
            Grid.SetRowSpan(Row1, 1);
            RenderRow(_row2, model.Row2, palette, model.ProviderId, showResetTime);
        }
    }

    private static void RenderRow(RowParts row, WidgetRow model, WidgetPalette palette, string providerId, bool showResetTime)
    {
        row.Container.Visibility = Visibility.Visible;
        row.Container.Opacity = model.Dimmed ? StaleOpacity : 1.0;
        row.Label.Text = model.Label;
        row.Label.FontSize = providerId == SystemPanel.Id ? SystemLabelFontSize : LabelFontSize;
        row.Label.Foreground = palette.Muted;
        row.Gap.Width = new GridLength(showResetTime ? WidgetMetrics.ResetGapDip : 0);
        row.ResetColumn.Width = new GridLength(showResetTime ? WidgetMetrics.ResetColumnDip : 0);
        row.Reset.Visibility = Visibility.Collapsed;

        switch (model.Kind)
        {
            case WidgetRowKind.Meter:
                row.Reset.Text = model.ResetText;
                row.Reset.Foreground = palette.Muted;
                row.Reset.Visibility = showResetTime && model.ResetText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                row.Label.Visibility = Visibility.Visible;
                row.Track.Visibility = Visibility.Visible;
                row.Track.Background = palette.Track;
                row.Fill.Background = palette.BarBrush(providerId, model.Severity);
                row.Fill.Width = Math.Round(BarWidth * Math.Clamp(model.Fraction ?? 0, 0, 1));
                row.Value.Visibility = Visibility.Visible;
                row.Value.Text = model.Value;
                row.Value.Foreground = palette.Text;
                row.Text.Visibility = Visibility.Collapsed;
                break;

            case WidgetRowKind.Text:
                bool hasLabel = model.Label.Length > 0;
                row.Label.Visibility = hasLabel ? Visibility.Visible : Visibility.Collapsed;
                row.Track.Visibility = Visibility.Collapsed;
                row.Value.Visibility = Visibility.Collapsed;
                ShowText(row.Text, model.Value, palette.Text, hasLabel ? 1 : 0);
                break;

            default:
                row.Label.Visibility = Visibility.Collapsed;
                row.Track.Visibility = Visibility.Collapsed;
                row.Value.Visibility = Visibility.Collapsed;
                ShowText(row.Text, model.Value, palette.Muted, 0);
                break;
        }
    }

    private static void ShowText(TextBlock text, string value, Brush brush, int column)
    {
        text.Text = value;
        text.Foreground = brush;
        Grid.SetColumn(text, column);
        Grid.SetColumnSpan(text, 4 - column);
        text.Visibility = Visibility.Visible;
    }

    private void RenderDots(WidgetPanelModel model, WidgetPalette palette)
    {
        if (!model.ShowDots)
        {
            DotsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        while (DotsPanel.Children.Count < model.DotCount)
        {
            DotsPanel.Children.Add(new Ellipse
            {
                Width = DotSize,
                Height = DotSize,
                StrokeThickness = 1,
                Margin = new Thickness(0, 0, DotSpacing, 0),
            });
        }

        while (DotsPanel.Children.Count > model.DotCount)
        {
            DotsPanel.Children.RemoveAt(DotsPanel.Children.Count - 1);
        }

        for (int i = 0; i < DotsPanel.Children.Count; i++)
        {
            var dot = (Ellipse)DotsPanel.Children[i];
            bool current = i == model.DotIndex;
            dot.Fill = current ? palette.Text : Brushes.Transparent;
            dot.Stroke = current ? palette.Text : palette.Muted;
        }

        DotsPanel.Visibility = Visibility.Visible;
    }

    private sealed record RowParts(
        Grid Container, TextBlock Label, Border Track, Border Fill, TextBlock Value, TextBlock Text, TextBlock Reset, ColumnDefinition Gap, ColumnDefinition ResetColumn);
}
