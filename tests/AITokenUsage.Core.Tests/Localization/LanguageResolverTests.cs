using System.Globalization;
using AITokenUsage.Core.Localization;
using AITokenUsage.Core.Settings;

namespace AITokenUsage.Core.Tests.Localization;

public class LanguageResolverTests
{
    [Theory]
    [InlineData("auto", "ko-KR", "ko")]
    [InlineData("auto", "en-US", "en")]
    [InlineData("auto", "ja-JP", "en")]
    [InlineData(null, "ko-KR", "ko")]
    [InlineData("ko", "en-US", "ko")]
    [InlineData("en", "ko-KR", "en")]
    [InlineData("KO", "en-US", "ko")]
    [InlineData("fr", "ko-KR", "ko")]   // unknown → auto
    public void Resolve_picks_language(string? setting, string os, string expectedTwoLetter)
    {
        CultureInfo culture = LanguageResolver.Resolve(setting, CultureInfo.GetCultureInfo(os));

        Assert.Equal(expectedTwoLetter, culture.TwoLetterISOLanguageName);
    }

    [Fact]
    public void Korean_resolves_to_ko_KR_and_english_to_en_US()
    {
        Assert.Equal("ko-KR", LanguageResolver.Resolve("ko", CultureInfo.InvariantCulture).Name);
        Assert.Equal("en-US", LanguageResolver.Resolve("en", CultureInfo.InvariantCulture).Name);
    }

    [Theory]
    [InlineData(null, AppSettings.LanguageAuto)]
    [InlineData("", AppSettings.LanguageAuto)]
    [InlineData(" Ko ", AppSettings.LanguageKorean)]
    [InlineData("en", AppSettings.LanguageEnglish)]
    [InlineData("de", AppSettings.LanguageAuto)]
    public void Normalize_maps_to_known_values(string? input, string expected) =>
        Assert.Equal(expected, LanguageResolver.Normalize(input));

    [Theory]
    [InlineData("ko", "ko-KR", "ko-KR", false)]
    [InlineData("en", "ko-KR", "ko-KR", true)]
    [InlineData("auto", "ko-KR", "ko-KR", false)]   // auto resolves to the running Korean UI: nothing would change
    [InlineData("auto", "en-US", "ko-KR", true)]
    [InlineData("ko", "en-US", "en-US", true)]
    [InlineData("en", "en-US", "en-US", false)]
    [InlineData("fr", "ja-JP", "en-US", false)]     // unknown → auto → English on a non-Korean OS
    public void RequiresRestart_compares_the_resolved_culture_with_the_running_one(
        string setting, string os, string running, bool expected) =>
        Assert.Equal(
            expected,
            LanguageResolver.RequiresRestart(setting, CultureInfo.GetCultureInfo(os), CultureInfo.GetCultureInfo(running)));
}
