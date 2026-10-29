using System.Globalization;
using AITokenUsage.Core.Settings;

namespace AITokenUsage.Core.Localization;

/// <summary>Maps the "Language" setting (auto/ko/en) and the Windows UI language to the app culture (v1.1 spec §3.2).</summary>
public static class LanguageResolver
{
    /// <summary>Trims and lower-cases the setting; anything other than "ko" or "en" (including null) becomes "auto".</summary>
    public static string Normalize(string? setting) => (setting ?? "").Trim().ToLowerInvariant() switch
    {
        AppSettings.LanguageKorean => AppSettings.LanguageKorean,
        AppSettings.LanguageEnglish => AppSettings.LanguageEnglish,
        _ => AppSettings.LanguageAuto,
    };

    /// <summary>The app culture (ko-KR or en-US): "ko"/"en" win; "auto" or an unknown value follows the OS UI language (Korean only when it is Korean).</summary>
    public static CultureInfo Resolve(string? setting, CultureInfo osUiCulture)
    {
        ArgumentNullException.ThrowIfNull(osUiCulture);

        string language = Normalize(setting);
        if (language == AppSettings.LanguageAuto)
        {
            language = osUiCulture.TwoLetterISOLanguageName == "ko" ? AppSettings.LanguageKorean : AppSettings.LanguageEnglish;
        }

        return CultureInfo.GetCultureInfo(language == AppSettings.LanguageKorean ? "ko-KR" : "en-US");
    }

    /// <summary>
    /// True when a restart with <paramref name="setting"/> would show a different language than the running app
    /// (the culture applied at startup). "auto" that resolves to the running language needs no restart.
    /// </summary>
    public static bool RequiresRestart(string? setting, CultureInfo osUiCulture, CultureInfo runningCulture)
    {
        ArgumentNullException.ThrowIfNull(runningCulture);

        return !string.Equals(Resolve(setting, osUiCulture).Name, runningCulture.Name, StringComparison.OrdinalIgnoreCase);
    }
}
