using System.Windows.Controls;
using System.Windows.Media;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.SystemUsage;

namespace AITokenUsage.UI;

/// <summary>
/// The fixed PC column of the "always beside" mode: a divider and two lines (CPU, RAM) to the right of the AI panel.
/// It sits outside the AI panels' transition stage, so it never slides; the widget only redraws it on a new sample.
/// </summary>
public partial class SystemBesidePanel : UserControl
{
    public SystemBesidePanel()
    {
        InitializeComponent();
    }

    /// <summary>Draws the latest sample (null before the first one: both values show a dash).</summary>
    public void Render(SystemUsageSample? sample, WidgetPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);

        // Same rows (label, "23%", severity) the rotating PC panel shows, so the two views can never disagree.
        WidgetPanelModel model = SystemPanel.BuildWidgetModel(sample, 0, 0);
        WidgetRow cpu = model.Row1;
        WidgetRow ram = model.Row2 ?? model.Row1;

        Divider.Background = palette.Track;
        RenderRow(CpuLabel, CpuValue, cpu, palette);
        RenderRow(RamLabel, RamValue, ram, palette);
    }

    private static void RenderRow(TextBlock label, TextBlock value, WidgetRow row, WidgetPalette palette)
    {
        label.Text = row.Label;
        label.Foreground = palette.Muted;
        value.Text = row.Value;
        value.Foreground = ValueBrush(row.Severity, palette);
    }

    /// <summary>Normal values use the text colour; warning and critical use the shared warning/critical colours.</summary>
    private static Brush ValueBrush(Severity severity, WidgetPalette palette) =>
        severity == Severity.Normal ? palette.Text : palette.BarBrush(SystemPanel.Id, severity);
}
