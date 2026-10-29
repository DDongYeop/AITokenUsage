using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace AITokenUsage.UI.Controls;

/// <summary>
/// A decorative Segoe Fluent Icons glyph. A plain TextBlock exposes its private-use character to UI Automation as text;
/// this one stays in the raw view only (not a control or content element, no name), like WinUI's AccessibilityView="Raw",
/// which WPF does not have. The element it decorates carries the accessible name.
/// </summary>
public sealed class IconGlyph : TextBlock
{
    protected override AutomationPeer OnCreateAutomationPeer() => new DecorativePeer(this);

    private sealed class DecorativePeer(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;

        protected override string GetNameCore() => "";

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetClassNameCore() => nameof(IconGlyph);
    }
}
