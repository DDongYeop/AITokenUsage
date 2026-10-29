using AITokenUsage.Core.Models;
using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.Resources;
using AITokenUsage.Core.Tests.Localization;

namespace AITokenUsage.Core.Tests.Presentation;

public class ConnectionGuideBuilderTests
{
    [Theory]
    [InlineData(ProviderIds.Claude)]
    [InlineData(ProviderIds.Codex)]
    [InlineData(ProviderIds.Gemini)]
    [InlineData(ProviderIds.Copilot)]
    public void Build_Ok_ReturnsNull(string providerId)
    {
        Assert.Null(ConnectionGuideBuilder.Build(providerId, ProviderStatus.Ok, null));
    }

    [Theory]
    [InlineData(ProviderIds.Claude)]
    [InlineData(ProviderIds.Codex)]
    [InlineData(ProviderIds.Gemini)]
    [InlineData(ProviderIds.Copilot)]
    public void Build_ErrorAndRateLimited_ReturnNull(string providerId)
    {
        Assert.Null(ConnectionGuideBuilder.Build(providerId, ProviderStatus.Error, "boom"));
        Assert.Null(ConnectionGuideBuilder.Build(providerId, ProviderStatus.RateLimited, null));
    }

    [Fact]
    public void Build_UnknownProvider_ReturnsNull()
    {
        Assert.Null(ConnectionGuideBuilder.Build("other", ProviderStatus.NotInstalled, null));
    }

    [Fact]
    public void Build_CodexNotInstalled_HasFourSteps()
    {
        ConnectionGuide guide = ConnectionGuideBuilder.Build(ProviderIds.Codex, ProviderStatus.NotInstalled, null)!;

        Assert.Equal(4, guide.Steps.Count);
        Assert.Equal("winget install OpenJS.NodeJS.LTS", guide.Steps[0].Command);
        Assert.Equal(GuideAction.None, guide.Steps[0].Action);
        Assert.Equal("npm install -g @openai/codex", guide.Steps[1].Command);
        Assert.Equal(GuideAction.OpenTerminal, guide.Steps[2].Action);
        Assert.Equal("codex login", guide.Steps[2].TerminalCommand);
        Assert.Equal(GuideAction.Recheck, guide.Steps[3].Action);
        Assert.Null(guide.Footnote);
    }

    [Fact]
    public void Build_CodexSignedOut_HasLoginAndRecheck()
    {
        ConnectionGuide guide = ConnectionGuideBuilder.Build(ProviderIds.Codex, ProviderStatus.SignedOut, null)!;

        Assert.Equal(2, guide.Steps.Count);
        Assert.Equal(GuideAction.OpenTerminal, guide.Steps[0].Action);
        Assert.Equal("codex login", guide.Steps[0].TerminalCommand);
        Assert.Equal(GuideAction.Recheck, guide.Steps[1].Action);
    }

    [Fact]
    public void Build_CodexOtherStatuses_ReturnNull()
    {
        Assert.Null(ConnectionGuideBuilder.Build(ProviderIds.Codex, ProviderStatus.WaitingForCliRefresh, null));
        Assert.Null(ConnectionGuideBuilder.Build(ProviderIds.Codex, ProviderStatus.Unsupported, null));
    }

    [Fact]
    public void Build_ClaudeNotInstalled_InstallsThenLogsInWithSlashLogin()
    {
        ConnectionGuide guide = ConnectionGuideBuilder.Build(ProviderIds.Claude, ProviderStatus.NotInstalled, null)!;

        Assert.Equal(4, guide.Steps.Count);
        Assert.Equal("winget install OpenJS.NodeJS.LTS", guide.Steps[0].Command);
        Assert.Equal("npm install -g @anthropic-ai/claude-code", guide.Steps[1].Command);
        Assert.Equal(GuideAction.OpenTerminal, guide.Steps[2].Action);
        Assert.Equal("claude", guide.Steps[2].TerminalCommand);
        Assert.Contains("/login", guide.Steps[2].Text);
        Assert.Equal(GuideAction.Recheck, guide.Steps[3].Action);
    }

    [Fact]
    public void Build_ClaudeSignedOut_HasLoginAndRecheck()
    {
        ConnectionGuide guide = ConnectionGuideBuilder.Build(ProviderIds.Claude, ProviderStatus.SignedOut, null)!;

        Assert.Equal(2, guide.Steps.Count);
        Assert.Equal("claude", guide.Steps[0].TerminalCommand);
        Assert.Contains("/login", guide.Steps[0].Text);
        Assert.Equal(GuideAction.Recheck, guide.Steps[1].Action);
    }

    [Fact]
    public void Build_ClaudeWaitingForCliRefresh_OpensClaudeThenRechecks()
    {
        ConnectionGuide guide = ConnectionGuideBuilder.Build(ProviderIds.Claude, ProviderStatus.WaitingForCliRefresh, null)!;

        Assert.Equal(2, guide.Steps.Count);
        Assert.Equal(GuideAction.OpenTerminal, guide.Steps[0].Action);
        Assert.Equal("claude", guide.Steps[0].TerminalCommand);
        Assert.Equal(GuideAction.Recheck, guide.Steps[1].Action);
    }

    [Fact]
    public void Build_GeminiNotInstalled_InstallsThenOpensGemini()
    {
        ConnectionGuide guide = ConnectionGuideBuilder.Build(ProviderIds.Gemini, ProviderStatus.NotInstalled, null)!;

        Assert.Equal(4, guide.Steps.Count);
        Assert.Equal("npm install -g @google/gemini-cli", guide.Steps[1].Command);
        Assert.Equal(GuideAction.OpenTerminal, guide.Steps[2].Action);
        Assert.Equal("gemini", guide.Steps[2].TerminalCommand);
        Assert.Equal(GuideAction.Recheck, guide.Steps[3].Action);
    }

    [Fact]
    public void Build_GeminiSignedOut_HasLoginAndRecheck()
    {
        ConnectionGuide guide = ConnectionGuideBuilder.Build(ProviderIds.Gemini, ProviderStatus.SignedOut, null)!;

        Assert.Equal(2, guide.Steps.Count);
        Assert.Equal("gemini", guide.Steps[0].TerminalCommand);
        Assert.Equal(GuideAction.Recheck, guide.Steps[1].Action);
    }

    [Fact]
    public void Build_GeminiUnsupported_HasNoStepsAndFootnoteIsStatusMessage()
    {
        ConnectionGuide guide = ConnectionGuideBuilder.Build(ProviderIds.Gemini, ProviderStatus.Unsupported, "개인 계정 종료")!;

        Assert.Empty(guide.Steps);
        Assert.Equal("개인 계정 종료", guide.Footnote);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_GeminiUnsupportedWithoutMessage_UsesDefaultNotice(string? message)
    {
        ConnectionGuide guide = ConnectionGuideBuilder.Build(ProviderIds.Gemini, ProviderStatus.Unsupported, message)!;

        Assert.Empty(guide.Steps);
        Assert.Equal(Strings.GuideGeminiDiscontinued, guide.Footnote);
    }

    [Theory]
    [InlineData(ProviderStatus.SignedOut)]
    [InlineData(ProviderStatus.NotInstalled)]
    public void Build_Copilot_HasOnlyRecheck(ProviderStatus status)
    {
        ConnectionGuide guide = ConnectionGuideBuilder.Build(ProviderIds.Copilot, status, null)!;

        GuideStep step = Assert.Single(guide.Steps);
        Assert.Equal(GuideAction.Recheck, step.Action);
    }

    [Fact]
    public void Build_CopilotOtherStatuses_ReturnNull()
    {
        Assert.Null(ConnectionGuideBuilder.Build(ProviderIds.Copilot, ProviderStatus.WaitingForCliRefresh, null));
        Assert.Null(ConnectionGuideBuilder.Build(ProviderIds.Copilot, ProviderStatus.Unsupported, null));
    }

    [Fact]
    public void Build_CodexSignedOut_TextIsKoreanByDefault()
    {
        ConnectionGuide guide = ConnectionGuideBuilder.Build(ProviderIds.Codex, ProviderStatus.SignedOut, null)!;

        Assert.Equal("터미널에서 로그인", guide.Steps[0].Text);
    }
}

[UseCulture("en-US")]
public class ConnectionGuideBuilderEnglishTests
{
    [Fact]
    public void Build_CodexSignedOut_FirstStepTextIsEnglish()
    {
        ConnectionGuide guide = ConnectionGuideBuilder.Build(ProviderIds.Codex, ProviderStatus.SignedOut, null)!;

        Assert.Equal("Sign in from a terminal", guide.Steps[0].Text);
    }
}
