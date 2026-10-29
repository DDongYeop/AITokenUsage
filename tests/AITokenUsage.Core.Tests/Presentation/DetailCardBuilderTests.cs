using AITokenUsage.Core.Models;
using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.Settings;
using static AITokenUsage.Core.Tests.Presentation.PresentationSamples;

namespace AITokenUsage.Core.Tests.Presentation;

public class DetailCardBuilderTests
{
    private static readonly DateTimeOffset CopilotReset = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_OkClaude_RowsResetsExtraAndStatus()
    {
        var snapshot = new UsageSnapshot("claude", ProviderStatus.Ok, "Team · Max 5x",
            [
                Meter("session", "5h", 14, Now.AddHours(3).AddMinutes(12), longLabel: "현재 세션 (5시간)"),
                Meter("weekly", "주", 50, Now.AddDays(1).AddHours(8), longLabel: "주간 · 모든 모델"),
                Meter("weekly:fable", "주·Fa", 35, null, Severity.Normal, longLabel: "주간 · Fable"),
            ],
            Now.AddMinutes(-1), ExtraLine: "추가 사용 $12.40 / $50.00");

        DetailCardModel card = DetailCardBuilder.Build(View(snapshot), ValueMode.Used, Now, Kst);

        Assert.Equal("claude", card.ProviderId);
        Assert.Equal("Claude", card.DisplayName);
        Assert.Equal("Team · Max 5x", card.Plan);
        Assert.Equal(3, card.Rows.Count);

        CardRow session = card.Rows[0];
        Assert.Equal("현재 세션 (5시간)", session.Label);
        Assert.Equal("14%", session.Value);
        Assert.Equal(0.14, session.Fraction!.Value, 6);
        Assert.Equal("3시간 12분 후 · 오늘 15:12", session.ResetText);
        Assert.False(session.IsText);
        Assert.False(session.Dimmed);

        Assert.Equal("주간 · 모든 모델", card.Rows[1].Label);
        Assert.Equal("1일 8시간 후 · 10/29(목) 20:00", card.Rows[1].ResetText);
        Assert.Equal("주간 · Fable", card.Rows[2].Label);
        Assert.Equal("", card.Rows[2].ResetText);

        Assert.Equal("추가 사용 $12.40 / $50.00", card.ExtraLine);
        Assert.Equal(new[] { "1분 전 갱신" }, card.StatusLines);
        Assert.False(card.IsStale);
    }

    [Fact]
    public void Build_RemainingMode_ShowsRemainingPercent()
    {
        DetailCardModel card = DetailCardBuilder.Build(View(Ok("claude", Meter("session", "5h", 14))), ValueMode.Remaining, Now, Kst);

        Assert.Equal("86%", card.Rows[0].Value);
        Assert.Equal(0.86, card.Rows[0].Fraction!.Value, 6);
    }

    [Fact]
    public void Build_TextMeter_ShowsTextAndDateWithWeekday()
    {
        var snapshot = new UsageSnapshot("copilot", ProviderStatus.Ok, "Pro",
            [
                Meter("monthly", "월", 76.3, CopilotReset, longLabel: "월간 프리미엄 요청"),
                Meter("reset", "↻", null, CopilotReset, text: "↻ 11/1", longLabel: "리셋 날짜"),
            ],
            Now);

        DetailCardModel card = DetailCardBuilder.Build(View(snapshot, displayName: "Copilot"), ValueMode.Used, Now, Kst);

        CardRow monthly = card.Rows[0];
        Assert.Equal("76%", monthly.Value);
        Assert.Equal(Severity.Warning, monthly.Severity);
        Assert.Equal("3일 21시간 후 · 11/1(일) 09:00", monthly.ResetText);

        CardRow reset = card.Rows[1];
        Assert.True(reset.IsText);
        Assert.Null(reset.Fraction);
        Assert.Equal("리셋 날짜", reset.Label);
        Assert.Equal("↻ 11/1", reset.Value);
        Assert.Equal("11/1(일)", reset.ResetText);
    }

    [Fact]
    public void Build_StaleView_DimsRowsAndUsesLastGoodPlanAndExtraLine()
    {
        UsageSnapshot lastGood = Ok("claude", Meter("session", "5h", 14)) with { ExtraLine = "추가 사용 $1.00 / $50.00" };
        var view = View(Status("claude", ProviderStatus.RateLimited, null, TimeSpan.FromMinutes(21)), lastGood);

        DetailCardModel card = DetailCardBuilder.Build(view, ValueMode.Used, Now, Kst);

        Assert.True(card.IsStale);
        Assert.True(Assert.Single(card.Rows).Dimmed);
        Assert.Equal("Pro", card.Plan);
        Assert.Equal("추가 사용 $1.00 / $50.00", card.ExtraLine);
        Assert.Equal(new[] { "요청 제한 — 12:20 이후 재시도", "마지막 정상 값 · 3분 전" }, card.StatusLines);
    }

    [Fact]
    public void Build_LeavesSystemLineEmpty_AndModelAcceptsOne()
    {
        DetailCardModel card = DetailCardBuilder.Build(
            View(Ok("claude", Meter("session", "5h", 14, Now.AddHours(1)))), ValueMode.Used, Now, Kst);

        Assert.Null(card.SystemLine);
        Assert.Equal("PC CPU 1%", (card with { SystemLine = "PC CPU 1%" }).SystemLine);
    }

    [Fact]
    public void Build_SignedOutWithoutCache_HasNoRows()
    {
        var view = View(Status("claude", ProviderStatus.SignedOut, "터미널에서 claude 실행 후 /login"));

        DetailCardModel card = DetailCardBuilder.Build(view, ValueMode.Used, Now, Kst);

        Assert.Empty(card.Rows);
        Assert.Null(card.Plan);
        Assert.Null(card.ExtraLine);
        Assert.Equal(new[] { "로그인 필요 — 터미널에서 claude 실행 후 /login" }, card.StatusLines);
    }

    [Fact]
    public void Build_PastReset_SaysResetSoon()
    {
        DetailCardModel card = DetailCardBuilder.Build(
            View(Ok("claude", Meter("session", "5h", 99, Now.AddMinutes(-2)))), ValueMode.Used, Now, Kst);

        Assert.Equal("곧 리셋", card.Rows[0].ResetText);
        Assert.Equal(Severity.Critical, card.Rows[0].Severity);
    }
}
