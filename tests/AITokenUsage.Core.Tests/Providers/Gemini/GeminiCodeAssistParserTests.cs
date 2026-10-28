using AITokenUsage.Core.Providers.Gemini;
using AITokenUsage.Core.Tests.Fakes;

namespace AITokenUsage.Core.Tests.Providers.Gemini;

public sealed class GeminiCodeAssistParserTests
{
    private const string ConsumerIdToken =
        "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0.eyJpc3MiOiJodHRwczovL2FjY291bnRzLmdvb2dsZS5jb20iLCJlbWFpbCI6InVzZXJAZXhhbXBsZS5jb20ifQ.ZmFrZS1zaWduYXR1cmU";
    private const string WorkspaceIdToken =
        "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0.eyJpc3MiOiJodHRwczovL2FjY291bnRzLmdvb2dsZS5jb20iLCJlbWFpbCI6ImRldkBleGFtcGxlLmNvbSIsImhkIjoiZXhhbXBsZS5jb20ifQ.ZmFrZS1zaWduYXR1cmU";

    [Fact]
    public void Standard_tier_with_string_project()
    {
        var info = GeminiCodeAssistParser.Parse(Fixture.Read("gemini/load-standard.json"));

        Assert.True(info.HasCurrentTier);
        Assert.Equal("standard-tier", info.TierId);
        Assert.Equal("fake-project-123", info.Project);
        Assert.False(info.HasUnsupportedClient);
        Assert.False(info.IsConsumerShutdown(hasHostedDomain: false));
        Assert.Equal("Standard", GeminiCodeAssistParser.PlanLabel(info, hasHostedDomain: false));
    }

    [Fact]
    public void Paid_tier_name_and_object_project_id()
    {
        var info = GeminiCodeAssistParser.Parse(Fixture.Read("gemini/load-paid-object-project.json"));

        Assert.Equal("fake-project-object", info.Project);
        Assert.Equal("g1-pro-tier", info.TierId);
        Assert.Equal("Gemini Code Assist in Google One AI Pro", GeminiCodeAssistParser.PlanLabel(info, hasHostedDomain: false));
    }

    [Fact]
    public void Object_project_with_projectId_field()
    {
        var info = GeminiCodeAssistParser.Parse(
            """{"currentTier":{"id":"standard-tier"},"cloudaicompanionProject":{"projectId":"fake-project-from-projectId"}}""");

        Assert.Equal("fake-project-from-projectId", info.Project);
    }

    [Fact]
    public void Consumer_shutdown_requires_no_tier_no_paid_name_and_no_hd()
    {
        var info = GeminiCodeAssistParser.Parse(Fixture.Read("gemini/load-shutdown.json"));

        Assert.False(info.HasCurrentTier);
        Assert.True(info.HasUnsupportedClient);
        Assert.Null(info.Project);
        Assert.True(info.IsConsumerShutdown(hasHostedDomain: false));
        Assert.False(info.IsConsumerShutdown(hasHostedDomain: true));
    }

    [Fact]
    public void Paid_tier_name_prevents_shutdown()
    {
        var info = GeminiCodeAssistParser.Parse(
            """{"paidTier":{"name":"Gemini Code Assist in Google One AI Ultra"},"ineligibleTiers":[{"reasonCode":"UNSUPPORTED_CLIENT"}]}""");

        Assert.False(info.IsConsumerShutdown(hasHostedDomain: false));
    }

    [Fact]
    public void Unsupported_client_in_reason_message_is_detected()
    {
        var info = GeminiCodeAssistParser.Parse(
            """{"ineligibleTiers":[{"reasonCode":"UNKNOWN","reasonMessage":"server said unsupported_client"}]}""");

        Assert.True(info.HasUnsupportedClient);
    }

    [Theory]
    [InlineData("free-tier", false, "Free")]
    [InlineData("free-tier", true, "Workspace")]
    [InlineData("standard-tier", false, "Standard")]
    [InlineData("legacy-tier", false, "Legacy")]
    [InlineData("mystery-tier", false, null)]
    public void Plan_label_from_tier_id(string tierId, bool hasHostedDomain, string? expected)
    {
        var info = new GeminiCodeAssistInfo(true, tierId, null, "p", false);

        Assert.Equal(expected, GeminiCodeAssistParser.PlanLabel(info, hasHostedDomain));
    }

    [Theory]
    [InlineData(ConsumerIdToken, false)]
    [InlineData(WorkspaceIdToken, true)]
    [InlineData(null, false)]
    [InlineData("not-a-jwt", false)]
    public void Hosted_domain_claim_comes_from_id_token(string? idToken, bool expected)
    {
        Assert.Equal(expected, GeminiCodeAssistParser.HasHostedDomain(idToken));
    }

    [Fact]
    public void Subscription_required_reason_is_found_in_error_details()
    {
        Assert.True(GeminiCodeAssistParser.HasErrorReason(Fixture.Read("gemini/quota-subscription-required.json"), "SUBSCRIPTION_REQUIRED"));
        Assert.False(GeminiCodeAssistParser.HasErrorReason("""{"error":{"code":403,"details":[]}}""", "SUBSCRIPTION_REQUIRED"));
        Assert.False(GeminiCodeAssistParser.HasErrorReason("<html>forbidden</html>", "SUBSCRIPTION_REQUIRED"));
    }

    [Theory]
    [InlineData("""{"error":{"message":"UNSUPPORTED_CLIENT"}}""", true)]
    [InlineData("Please migrate to Antigravity to keep using Gemini", true)]
    [InlineData("This client is no longer supported for Gemini Code Assist", true)]
    [InlineData("""{"error":{"code":500,"message":"backend error"}}""", false)]
    public void Shutdown_text_markers(string body, bool expected)
    {
        Assert.Equal(expected, GeminiCodeAssistParser.LooksLikeShutdown(body));
    }
}
