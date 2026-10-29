using AITokenUsage.Core.Models;
using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.Settings;
using static AITokenUsage.Core.Tests.Presentation.PresentationSamples;

namespace AITokenUsage.Core.Tests.Presentation;

public class WidgetPanelBuilderTests
{
    private static readonly UsageMeter Session = Meter("session", "5h", 14);
    private static readonly UsageMeter Weekly = Meter("weekly", "주", 50);

    [Fact]
    public void Build_OkView_TwoMeterRowsAndDots()
    {
        WidgetPanelModel model = WidgetPanelBuilder.Build(View(Ok("claude", Session, Weekly)), "session", "weekly", ValueMode.Used, 3, 1, Now);

        Assert.Equal("claude", model.ProviderId);
        Assert.Equal("Claude", model.DisplayName);
        Assert.False(model.IsStale);
        Assert.Equal(WidgetRowKind.Meter, model.Row1.Kind);
        Assert.Equal("5h", model.Row1.Label);
        Assert.Equal("14%", model.Row1.Value);
        Assert.Equal(0.14, model.Row1.Fraction!.Value, 6);
        Assert.False(model.Row1.Dimmed);
        Assert.NotNull(model.Row2);
        Assert.Equal(WidgetRowKind.Meter, model.Row2.Kind);
        Assert.Equal("주", model.Row2.Label);
        Assert.Equal("50%", model.Row2.Value);
        Assert.True(model.ShowDots);
        Assert.Equal(3, model.DotCount);
        Assert.Equal(1, model.DotIndex);
    }

    [Fact]
    public void Build_SingleEligibleProvider_HidesDots()
    {
        WidgetPanelModel model = WidgetPanelBuilder.Build(View(Ok("claude", Session, Weekly)), "session", "weekly", ValueMode.Used, 1, 0, Now);

        Assert.False(model.ShowDots);
    }

    [Fact]
    public void Build_RemainingMode_ShowsRemainingPercent()
    {
        WidgetPanelModel model = WidgetPanelBuilder.Build(View(Ok("claude", Session, Weekly)), "session", "weekly", ValueMode.Remaining, 1, 0, Now);

        Assert.Equal("86%", model.Row1.Value);
        Assert.Equal(0.86, model.Row1.Fraction!.Value, 6);
        Assert.Equal("50%", model.Row2!.Value);
    }

    [Fact]
    public void Build_OneMeterOnly_LeavesSecondRowEmpty()
    {
        WidgetPanelModel model = WidgetPanelBuilder.Build(View(Ok("codex", Meter("monthly", "월", 20))), "5h", "weekly", ValueMode.Used, 1, 0, Now);

        Assert.Equal("월", model.Row1.Label);
        Assert.Null(model.Row2);
    }

    [Fact]
    public void Build_ConfiguredKeysMissing_FallsBackToFirstMeters()
    {
        WidgetPanelModel model = WidgetPanelBuilder.Build(View(Ok("claude", Session, Weekly)), "gone-1", "gone-2", ValueMode.Used, 1, 0, Now);

        Assert.Equal("5h", model.Row1.Label);
        Assert.Equal("주", model.Row2!.Label);
    }

    [Fact]
    public void Build_LoadingView_ShowsServiceMessage()
    {
        var loading = UsageSnapshot.WithStatus("claude", ProviderStatus.Ok, Now, "불러오는 중…");

        WidgetPanelModel model = WidgetPanelBuilder.Build(View(loading), "session", "weekly", ValueMode.Used, 1, 0, Now);

        Assert.Equal(WidgetRowKind.Message, model.Row1.Kind);
        Assert.Equal("불러오는 중…", model.Row1.Value);
        Assert.Null(model.Row2);
        Assert.False(model.IsStale);
    }

    [Theory]
    [InlineData(ProviderStatus.NotInstalled, null, "설치 안 됨")]
    [InlineData(ProviderStatus.SignedOut, "터미널에서 claude 실행 후 /login", "로그인 필요")]
    [InlineData(ProviderStatus.WaitingForCliRefresh, null, "갱신 대기")]
    [InlineData(ProviderStatus.Unsupported, "개인 계정 지원 종료 (2026-06-18) — Antigravity 사용", "지원 종료")]
    [InlineData(ProviderStatus.Unsupported, "API 키 로그인 — 플랜 한도 없음", "지원 안 됨")]
    [InlineData(ProviderStatus.RateLimited, null, "요청 제한")]
    [InlineData(ProviderStatus.Error, "네트워크 오류 — 잠시 후 다시 시도", "오류")]
    public void Build_NoData_ShowsShortStatusInPlaceOfBothLines(ProviderStatus status, string? message, string expected)
    {
        WidgetPanelModel model = WidgetPanelBuilder.Build(View(Status("claude", status, message)), "session", "weekly", ValueMode.Used, 1, 0, Now);

        Assert.Equal(WidgetRowKind.Message, model.Row1.Kind);
        Assert.Equal(expected, model.Row1.Value);
        Assert.Null(model.Row2);
        Assert.False(model.IsStale);
    }

    [Fact]
    public void Build_StaleRateLimited_DimsBothRowsAndMarksName()
    {
        var view = View(Status("claude", ProviderStatus.RateLimited, null, TimeSpan.FromMinutes(10)), Ok("claude", Session, Weekly));

        WidgetPanelModel model = WidgetPanelBuilder.Build(view, "session", "weekly", ValueMode.Used, 2, 0, Now);

        Assert.True(model.IsStale);
        Assert.True(model.Row1.Dimmed);
        Assert.Equal("14%", model.Row1.Value);
        Assert.Equal(WidgetRowKind.Meter, model.Row2!.Kind);
        Assert.True(model.Row2.Dimmed);
    }

    [Fact]
    public void Build_StaleWaitingForCli_KeepsLine1AndReplacesLine2()
    {
        var view = View(Status("claude", ProviderStatus.WaitingForCliRefresh), Ok("claude", Session, Weekly));

        WidgetPanelModel model = WidgetPanelBuilder.Build(view, "session", "weekly", ValueMode.Used, 1, 0, Now);

        Assert.True(model.IsStale);
        Assert.Equal(WidgetRowKind.Meter, model.Row1.Kind);
        Assert.True(model.Row1.Dimmed);
        Assert.Equal(WidgetRowKind.Message, model.Row2!.Kind);
        Assert.Equal("갱신 대기", model.Row2.Value);
        Assert.False(model.Row2.Dimmed);
    }

    [Fact]
    public void Build_StaleSignedOut_ReplacesLine2WithLoginRequired()
    {
        var view = View(Status("claude", ProviderStatus.SignedOut, "터미널에서 claude 실행 후 /login"), Ok("claude", Session, Weekly));

        WidgetPanelModel model = WidgetPanelBuilder.Build(view, "session", "weekly", ValueMode.Used, 1, 0, Now);

        Assert.Equal("로그인 필요", model.Row2!.Value);
        Assert.Equal(WidgetRowKind.Message, model.Row2.Kind);
    }

    [Fact]
    public void Build_RowsCarryEffectiveSeverity()
    {
        var view = View(Ok("claude", Meter("session", "5h", 92), Meter("weekly", "주", 10, severity: Severity.Warning)));

        WidgetPanelModel model = WidgetPanelBuilder.Build(view, "session", "weekly", ValueMode.Used, 1, 0, Now);

        Assert.Equal(Severity.Critical, model.Row1.Severity);
        Assert.Equal(Severity.Warning, model.Row2!.Severity);
    }

    [Fact]
    public void Build_TextOverrideRows_SpanWhenTextStartsWithLabel()
    {
        var credits = Meter("monthly", "월", null, text: "12 크레딧 사용");
        var reset = Meter("reset", "↻", null, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), text: "↻ 11/1");
        var view = View(Ok("copilot", credits, reset), displayName: "Copilot");

        WidgetPanelModel model = WidgetPanelBuilder.Build(view, "monthly", "reset", ValueMode.Used, 1, 0, Now);

        Assert.Equal(WidgetRowKind.Text, model.Row1.Kind);
        Assert.Equal("월", model.Row1.Label);
        Assert.Equal("12 크레딧 사용", model.Row1.Value);
        Assert.Null(model.Row1.Fraction);
        Assert.Equal(WidgetRowKind.Text, model.Row2!.Kind);
        Assert.Equal("", model.Row2.Label);
        Assert.Equal("↻ 11/1", model.Row2.Value);
    }

    [Fact]
    public void Empty_ShowsSetupHint()
    {
        WidgetPanelModel model = WidgetPanelBuilder.Empty();

        Assert.Equal("", model.ProviderId);
        Assert.Equal("AI 사용량", model.DisplayName);
        Assert.Equal(WidgetRowKind.Message, model.Row1.Kind);
        Assert.Equal("우클릭하여 설정", model.Row1.Value);
        Assert.Null(model.Row2);
        Assert.False(model.ShowDots);
    }

    [Fact]
    public void Build_MeterRow_CarriesCompactResetText()
    {
        var session = Meter("session", "5h", 14, Now.AddHours(4).AddMinutes(12));
        var view = View(Ok("claude", session, Weekly));

        WidgetPanelModel model = WidgetPanelBuilder.Build(view, "session", "weekly", ValueMode.Used, 1, 0, Now);

        Assert.Equal("4h12m", model.Row1.ResetText);
        Assert.Equal("", model.Row2!.ResetText);
    }

    [Fact]
    public void Build_TextAndMessageRows_HaveNoResetText()
    {
        var credits = Meter("monthly", "월", null, Now.AddDays(3), text: "12 크레딧 사용");
        var view = View(Ok("copilot", credits), displayName: "Copilot");

        WidgetPanelModel text = WidgetPanelBuilder.Build(view, "monthly", null, ValueMode.Used, 1, 0, Now);
        WidgetPanelModel message = WidgetPanelBuilder.Build(View(Status("claude", ProviderStatus.SignedOut)), "session", "weekly", ValueMode.Used, 1, 0, Now);

        Assert.Equal("", text.Row1.ResetText);
        Assert.Equal("", message.Row1.ResetText);
        Assert.Equal("", WidgetRow.Message("x").ResetText);
    }
}
