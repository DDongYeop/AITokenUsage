using System.Windows;
using System.Windows.Controls;

namespace AITokenUsage.UI.Controls;

/// <summary>
/// One settings row in the Windows 11 style (v1.1 spec §4.2): a rounded card with a header and an optional description on
/// the left and the control (Content) on the right. The template lives in SettingsStyles.xaml.
/// </summary>
public sealed class SettingsCard : ContentControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(SettingsCard), new FrameworkPropertyMetadata(""));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingsCard), new FrameworkPropertyMetadata(null));

    static SettingsCard()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SettingsCard), new FrameworkPropertyMetadata(typeof(SettingsCard)));

        // The card is a container: keyboard focus goes straight to the control it hosts.
        FocusableProperty.OverrideMetadata(typeof(SettingsCard), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(SettingsCard), new FrameworkPropertyMetadata(false));
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>Small secondary text under the header; hidden when null or empty.</summary>
    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}
