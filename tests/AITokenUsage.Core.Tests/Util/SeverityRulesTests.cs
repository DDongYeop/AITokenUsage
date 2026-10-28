using AITokenUsage.Core.Models;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Tests.Util;

public class SeverityRulesTests
{
    private static UsageMeter Meter(double? used, Severity? server = null) =>
        new("session", "5h", "현재 세션 (5시간)", used, null, server);

    [Theory]
    [InlineData(0, Severity.Normal)]
    [InlineData(74.9, Severity.Normal)]
    [InlineData(75, Severity.Warning)]
    [InlineData(89.99, Severity.Warning)]
    [InlineData(90, Severity.Critical)]
    [InlineData(100, Severity.Critical)]
    [InlineData(250, Severity.Critical)]
    [InlineData(-10, Severity.Normal)]
    public void WithoutServerSeverity_UsesThresholds(double used, Severity expected)
    {
        Assert.Equal(expected, SeverityRules.Effective(Meter(used)));
    }

    [Theory]
    [InlineData(10, Severity.Critical)]
    [InlineData(95, Severity.Normal)]
    [InlineData(80, Severity.Warning)]
    public void ServerSeverity_Wins(double used, Severity server)
    {
        Assert.Equal(server, SeverityRules.Effective(Meter(used, server)));
    }

    [Fact]
    public void UnknownPercent_IsNormal()
    {
        Assert.Equal(Severity.Normal, SeverityRules.Effective(Meter(null)));
        Assert.Equal(Severity.Normal, SeverityRules.Effective(Meter(double.NaN)));
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(42.5, 42.5)]
    [InlineData(100, 100)]
    [InlineData(130, 100)]
    public void Clamp_LimitsToZeroToHundred(double input, double expected)
    {
        Assert.Equal(expected, SeverityRules.Clamp(input));
    }

    [Fact]
    public void Clamp_MapsNullAndNaNToNull_AndInfinityToBounds()
    {
        Assert.Null(SeverityRules.Clamp(null));
        Assert.Null(SeverityRules.Clamp(double.NaN));
        Assert.Equal(100, SeverityRules.Clamp(double.PositiveInfinity));
        Assert.Equal(0, SeverityRules.Clamp(double.NegativeInfinity));
    }
}
