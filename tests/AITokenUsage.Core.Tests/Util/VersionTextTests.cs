using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Tests.Util;

public class VersionTextTests
{
    [Theory]
    [InlineData("1.1.0+3c1eb68a9f", "1.1.0")]
    [InlineData("1.1.0-beta.2+3c1eb68", "1.1.0-beta.2")]
    [InlineData("1.1.0", "1.1.0")]
    [InlineData(" 1.2.3 ", "1.2.3")]
    public void FromInformational_drops_the_build_metadata(string informational, string expected) =>
        Assert.Equal(expected, VersionText.FromInformational(informational, fallback: null));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+abc")]
    public void FromInformational_uses_the_fallback_when_there_is_no_version(string? informational) =>
        Assert.Equal("1.2.3", VersionText.FromInformational(informational, new Version(1, 2, 3, 0)));

    [Fact]
    public void FromInformational_without_any_version_is_a_question_mark() =>
        Assert.Equal("?", VersionText.FromInformational(null, fallback: null));
}
