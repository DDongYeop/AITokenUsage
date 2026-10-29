using System.Text.RegularExpressions;
using System.Xml.Linq;
using AITokenUsage.Core.Resources;

namespace AITokenUsage.Core.Tests.Localization;

/// <summary>Every neutral (English) key must exist in the Korean file, be non-empty and use the same {n} placeholders.</summary>
public partial class ResourceCompletenessTests
{
    public static TheoryData<string> ResourceBases => new()
    {
        Path.Combine("src", "AITokenUsage.Core", "Resources", "Strings"),
        Path.Combine("src", "AITokenUsage", "Resources", "UiStrings"),
    };

    [Theory]
    [MemberData(nameof(ResourceBases))]
    public void Korean_resources_match_neutral_resources(string relativeBase)
    {
        string root = RepoRoot();
        string neutralPath = Path.Combine(root, relativeBase + ".resx");

        Dictionary<string, string> neutral = Load(neutralPath);
        Dictionary<string, string> korean = Load(Path.Combine(root, relativeBase + ".ko.resx"));

        Assert.NotEmpty(neutral);
        Assert.Empty(neutral.Keys.Except(korean.Keys));
        Assert.Empty(korean.Keys.Except(neutral.Keys));
        foreach ((string key, string value) in neutral)
        {
            Assert.False(string.IsNullOrWhiteSpace(value), $"empty neutral value: {key}");
            Assert.False(string.IsNullOrWhiteSpace(korean[key]), $"empty Korean value: {key}");
            Assert.Equal(Placeholders(value), Placeholders(korean[key]));
        }
    }

    [Fact]
    [UseCulture("en-US")]
    public void English_resources_are_loaded_under_en_US() =>
        Assert.Equal("Automatic (Windows setting)", Strings.LanguageNameAuto);

    [Fact]
    public void Korean_resources_are_loaded_by_default() =>
        Assert.Equal("자동 (Windows 설정)", Strings.LanguageNameAuto);

    private static Dictionary<string, string> Load(string path) =>
        XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? "");

    private static string Placeholders(string value) =>
        string.Join(",", PlaceholderRegex().Matches(value).Select(m => m.Groups[1].Value).Distinct().Order());

    private static string RepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AITokenUsage.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("AITokenUsage.sln not found above the test output directory.");
    }

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex PlaceholderRegex();
}
