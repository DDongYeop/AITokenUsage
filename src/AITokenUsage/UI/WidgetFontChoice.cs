using System.Globalization;
using System.Windows.Markup;
using System.Windows.Media;
using AITokenUsage.Resources;

namespace AITokenUsage.UI;

internal sealed record WidgetFontChoice(string? Name, string Label, FontFamily Family)
{
    private static readonly WidgetFontChoice Automatic =
        new(null, UiStrings.WidgetFontAutomatic, new FontFamily("Segoe UI Variable Text, Segoe UI"));

    // The default widget does not need to enumerate fonts until the picker is opened.
    private static readonly Lazy<WidgetFontChoice[]> Choices = new(BuildChoices);

    public static IReadOnlyList<WidgetFontChoice> All => Choices.Value;

    public static WidgetFontChoice Resolve(string? name) => name is null ? Automatic :
        All.FirstOrDefault(choice => string.Equals(choice.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Automatic;

    private static WidgetFontChoice[] BuildChoices()
    {
        var language = XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag);
        var installed = Fonts.SystemFontFamilies
            .DistinctBy(f => f.Source, StringComparer.OrdinalIgnoreCase)
            .Select(f => new WidgetFontChoice(f.Source, f.FamilyNames.TryGetValue(language, out string? label) ? label : f.Source, f))
            .OrderBy(choice => choice.Label, StringComparer.CurrentCultureIgnoreCase);
        return [Automatic, .. installed];
    }
}
