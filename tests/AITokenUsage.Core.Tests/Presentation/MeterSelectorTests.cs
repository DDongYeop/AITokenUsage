using AITokenUsage.Core.Models;
using AITokenUsage.Core.Presentation;

namespace AITokenUsage.Core.Tests.Presentation;

public class MeterSelectorTests
{
    private static readonly UsageMeter Session = Meter("session", "5h", 14);
    private static readonly UsageMeter Weekly = Meter("weekly", "주", 50);
    private static readonly UsageMeter Fable = Meter("weekly:fable", "주·Fa", 35);

    private static UsageMeter Meter(string key, string shortLabel, double? used) =>
        new(key, shortLabel, "긴 " + key, used, null);

    [Fact]
    public void BothKeysFound_ReturnsThemInLineOrder()
    {
        var (first, second) = MeterSelector.Select([Weekly, Session], "session", "weekly");

        Assert.Same(Session, first);
        Assert.Same(Weekly, second);
    }

    [Fact]
    public void BothKeysMissing_FallsBackToFirstMeters()
    {
        var monthly = Meter("monthly", "월", 20);

        var (first, second) = MeterSelector.Select([monthly], "5h", "weekly");

        Assert.Same(monthly, first);
        Assert.Null(second);
    }

    [Fact]
    public void MissingLine2_TakesFirstMeterOtherThanLine1()
    {
        var (first, second) = MeterSelector.Select([Session, Weekly, Fable], "weekly", "weekly:opus");

        Assert.Same(Weekly, first);
        Assert.Same(Session, second);
    }

    [Fact]
    public void MissingLine1_DoesNotReuseTheLine2Meter()
    {
        var (first, second) = MeterSelector.Select([Session, Weekly], "gone", "session");

        Assert.Same(Weekly, first);
        Assert.Same(Session, second);
    }

    [Fact]
    public void SameKeyOnBothLines_ShowsTheSameMeterTwice()
    {
        var (first, second) = MeterSelector.Select([Session, Weekly], "weekly", "weekly");

        Assert.Same(Weekly, first);
        Assert.Same(Weekly, second);
    }

    [Fact]
    public void NoMeters_ReturnsNulls()
    {
        var (first, second) = MeterSelector.Select([], "session", "weekly");

        Assert.Null(first);
        Assert.Null(second);
    }

    [Fact]
    public void BlankKeys_UseMeterOrder()
    {
        var (first, second) = MeterSelector.Select([Session, Weekly], "", null);

        Assert.Same(Session, first);
        Assert.Same(Weekly, second);
    }
}
