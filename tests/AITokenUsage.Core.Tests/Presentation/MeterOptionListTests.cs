using AITokenUsage.Core.Models;
using AITokenUsage.Core.Presentation;
using static AITokenUsage.Core.Tests.Presentation.PresentationSamples;

namespace AITokenUsage.Core.Tests.Presentation;

public class MeterOptionListTests
{
    private static readonly MeterOption[] ClaudeStatic =
    [
        new("session", "현재 세션 (5시간)"),
        new("weekly", "주간 · 모든 모델"),
    ];

    [Fact]
    public void StaticOptionsOnly_KeepTheirOrderAndLabels()
    {
        IReadOnlyList<MeterOption> options = MeterOptionList.Build(ClaudeStatic, [], ["session", "weekly"]);

        Assert.Equal(new[] { "session", "weekly" }, options.Select(o => o.Key));
        Assert.Equal("현재 세션 (5시간)", options[0].Label);
    }

    [Fact]
    public void DynamicMeters_AreAppendedWithTheirLongLabel()
    {
        UsageMeter[] meters =
        [
            Meter("session", "5h", 14, longLabel: "현재 세션 (5시간)"),
            Meter("weekly:fable", "주·Fa", 35, longLabel: "주간 · Fable"),
        ];

        IReadOnlyList<MeterOption> options = MeterOptionList.Build(ClaudeStatic, meters, ["session", "weekly"]);

        Assert.Equal(new[] { "session", "weekly", "weekly:fable" }, options.Select(o => o.Key));
        Assert.Equal("주간 · Fable", options[2].Label);
    }

    [Fact]
    public void SelectedKeyWithoutData_IsKeptWithSuffix()
    {
        IReadOnlyList<MeterOption> options = MeterOptionList.Build(ClaudeStatic, [], ["session", "weekly:opus"]);

        Assert.Equal(3, options.Count);
        Assert.Equal(new MeterOption("weekly:opus", "weekly:opus (현재 데이터 없음)"), options[^1]);
    }

    [Fact]
    public void BlankAndDuplicateKeys_AreIgnored()
    {
        MeterOption[] statics = [new("pro", "Pro 일간"), new("pro", "중복"), new("", "빈 키")];

        IReadOnlyList<MeterOption> options = MeterOptionList.Build(statics, [Meter("", "x", 1)], [null, "", "pro"]);

        Assert.Equal(new MeterOption("pro", "Pro 일간"), Assert.Single(options));
    }

    [Fact]
    public void SameOptions_ComparesKeysAndLabelsInOrder()
    {
        IReadOnlyList<MeterOption> a = MeterOptionList.Build(ClaudeStatic, [], []);
        IReadOnlyList<MeterOption> b = MeterOptionList.Build(ClaudeStatic, [], []);
        IReadOnlyList<MeterOption> swapped = MeterOptionList.Build(new[] { ClaudeStatic[1], ClaudeStatic[0] }, [], []);

        Assert.True(MeterOptionList.SameOptions(a, b));
        Assert.False(MeterOptionList.SameOptions(a, swapped));
    }
}
