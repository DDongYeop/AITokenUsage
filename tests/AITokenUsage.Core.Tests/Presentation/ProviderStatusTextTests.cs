using AITokenUsage.Core.Models;
using AITokenUsage.Core.Presentation;
using static AITokenUsage.Core.Tests.Presentation.PresentationSamples;

namespace AITokenUsage.Core.Tests.Presentation;

public class ProviderStatusTextTests
{
    private static readonly UsageMeter Session = Meter("session", "5h", 14);
    private static readonly TimeSpan TwentyOneMinutes = TimeSpan.FromMinutes(21);

    [Fact]
    public void Settings_Ok_ShowsConnectedPlanAndAge()
    {
        Assert.Equal("연결됨 · Pro · 3분 전", ProviderStatusText.ForSettings(View(Ok("claude", Session)), Now, Kst));
    }

    [Fact]
    public void Settings_OkWithMessage_ShowsMessageInsteadOfAge()
    {
        var snapshot = new UsageSnapshot("codex", ProviderStatus.Ok, "Plus", [Session], Now, "로그 기준 · 11:58");

        Assert.Equal("연결됨 · Plus · 로그 기준 · 11:58",
            ProviderStatusText.ForSettings(View(snapshot, displayName: "Codex"), Now, Kst));
    }

    [Fact]
    public void Settings_OkWithoutPlan_SkipsPlan()
    {
        var snapshot = new UsageSnapshot("claude", ProviderStatus.Ok, null, [Session], Now);

        Assert.Equal("연결됨 · 방금", ProviderStatusText.ForSettings(View(snapshot), Now, Kst));
    }

    [Fact]
    public void Settings_Loading_ShowsServiceMessage()
    {
        var loading = UsageSnapshot.WithStatus("claude", ProviderStatus.Ok, Now, "불러오는 중…");

        Assert.Equal("불러오는 중…", ProviderStatusText.ForSettings(View(loading), Now, Kst));
    }

    [Theory]
    [InlineData(ProviderStatus.NotInstalled, "설치 안 됨 — codex 설치 후 codex login", "설치 안 됨 — codex 설치 후 codex login")]
    [InlineData(ProviderStatus.NotInstalled, null, "설치 안 됨")]
    [InlineData(ProviderStatus.SignedOut, "터미널에서 claude 실행 후 /login", "로그인 필요 — 터미널에서 claude 실행 후 /login")]
    [InlineData(ProviderStatus.SignedOut, "로그인 필요 — 터미널에서 codex login", "로그인 필요 — 터미널에서 codex login")]
    [InlineData(ProviderStatus.SignedOut, null, "로그인 필요")]
    [InlineData(ProviderStatus.Unsupported, "API 키 로그인 — 사용량 조회 불가", "API 키 로그인 — 사용량 조회 불가")]
    [InlineData(ProviderStatus.Unsupported, "개인 계정 지원 종료 (2026-06-18) — Antigravity 사용", "개인 계정 지원 종료 (2026-06-18) — Antigravity 사용")]
    [InlineData(ProviderStatus.Unsupported, null, "지원 안 됨")]
    [InlineData(ProviderStatus.Error, null, "오류")]
    public void Settings_StatusesWithoutRetry(ProviderStatus status, string? message, string expected)
    {
        Assert.Equal(expected, ProviderStatusText.ForSettings(View(Status("claude", status, message)), Now, Kst));
    }

    [Fact]
    public void Settings_WaitingForCli_NamesTheCli()
    {
        var view = View(Status("claude", ProviderStatus.WaitingForCliRefresh, "Claude Code가 로그인을 갱신하면 자동으로 다시 불러옵니다"));

        Assert.Equal("Claude Code의 로그인 갱신 대기 중", ProviderStatusText.ForSettings(view, Now, Kst));
    }

    [Fact]
    public void Settings_RateLimited_ShowsRetryClock()
    {
        var view = View(Status("claude", ProviderStatus.RateLimited, "요청 제한 — 잠시 후 다시 시도", TwentyOneMinutes));

        Assert.Equal("요청 제한 — 12:20 이후 재시도", ProviderStatusText.ForSettings(view, Now, Kst));
    }

    [Fact]
    public void Settings_RateLimitedWithoutRetry_SaysRetrySoon()
    {
        var view = View(Status("claude", ProviderStatus.RateLimited));

        Assert.Equal("요청 제한 — 잠시 후 다시 시도", ProviderStatusText.ForSettings(view, Now, Kst));
    }

    [Fact]
    public void Settings_ErrorWithRetry_ReplacesRetrySoonWithClock()
    {
        var view = View(Status("claude", ProviderStatus.Error, "네트워크 오류 — 잠시 후 다시 시도", TwentyOneMinutes));

        Assert.Equal("네트워크 오류 — 12:20 이후 재시도", ProviderStatusText.ForSettings(view, Now, Kst));
    }

    [Fact]
    public void Card_Ok_ShowsAge()
    {
        Assert.Equal(new[] { "3분 전 갱신" }, ProviderStatusText.ForCard(View(Ok("claude", Session)), Now, Kst));
    }

    [Fact]
    public void Card_OkWithMessage_AppendsMessage()
    {
        var snapshot = new UsageSnapshot("codex", ProviderStatus.Ok, "Plus", [Session], Now, "로그 기준 · 11:58");

        Assert.Equal(new[] { "방금 갱신 · 로그 기준 · 11:58" },
            ProviderStatusText.ForCard(View(snapshot, displayName: "Codex"), Now, Kst));
    }

    [Fact]
    public void Card_Loading_ShowsServiceMessage()
    {
        var loading = UsageSnapshot.WithStatus("claude", ProviderStatus.Ok, Now, "불러오는 중…");

        Assert.Equal(new[] { "불러오는 중…" }, ProviderStatusText.ForCard(View(loading), Now, Kst));
    }

    [Fact]
    public void Card_StaleRateLimited_AddsLastGoodAge()
    {
        var view = View(Status("claude", ProviderStatus.RateLimited, null, TwentyOneMinutes), Ok("claude", Session));

        Assert.Equal(new[] { "요청 제한 — 12:20 이후 재시도", "마지막 정상 값 · 3분 전" },
            ProviderStatusText.ForCard(view, Now, Kst));
    }

    [Fact]
    public void Card_WaitingForCli_UsesProviderMessage()
    {
        var view = View(
            Status("claude", ProviderStatus.WaitingForCliRefresh, "Claude Code가 로그인을 갱신하면 자동으로 다시 불러옵니다"),
            Ok("claude", Session));

        Assert.Equal(new[] { "Claude Code가 로그인을 갱신하면 자동으로 다시 불러옵니다", "마지막 정상 값 · 3분 전" },
            ProviderStatusText.ForCard(view, Now, Kst));
    }

    [Fact]
    public void Card_SignedOutWithoutCache_IsASingleLine()
    {
        var view = View(Status("claude", ProviderStatus.SignedOut, "터미널에서 claude 실행 후 /login"));

        Assert.Equal(new[] { "로그인 필요 — 터미널에서 claude 실행 후 /login" }, ProviderStatusText.ForCard(view, Now, Kst));
    }

    [Fact]
    public void CliName_MapsKnownProviders()
    {
        Assert.Equal("Claude Code", ProviderStatusText.CliName(ProviderIds.Claude));
        Assert.Equal("Codex", ProviderStatusText.CliName(ProviderIds.Codex));
        Assert.Equal("Gemini CLI", ProviderStatusText.CliName(ProviderIds.Gemini));
        Assert.Equal("Copilot", ProviderStatusText.CliName(ProviderIds.Copilot));
        Assert.Equal("other", ProviderStatusText.CliName("other"));
    }
}
