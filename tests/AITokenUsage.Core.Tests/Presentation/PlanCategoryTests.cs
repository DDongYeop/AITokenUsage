using AITokenUsage.Core.Presentation;

namespace AITokenUsage.Core.Tests.Presentation;

public class PlanCategoryTests
{
    [Theory]
    [InlineData("Team", PlanCategory.Team)]
    [InlineData("Team · Max 5x", PlanCategory.Team)]
    [InlineData("TEAM", PlanCategory.Team)]
    [InlineData("Enterprise", PlanCategory.Team)]
    [InlineData("ent", PlanCategory.Team)]
    [InlineData("Ent Plus", PlanCategory.Team)]
    [InlineData("Business", PlanCategory.Team)]
    [InlineData("chatgpt business", PlanCategory.Team)]
    [InlineData("Edu", PlanCategory.Team)]
    [InlineData("education", PlanCategory.Team)]
    [InlineData("Max 5x", PlanCategory.Personal)]
    [InlineData("Max 20x", PlanCategory.Personal)]
    [InlineData("Pro", PlanCategory.Personal)]
    [InlineData("Plus", PlanCategory.Personal)]
    [InlineData("Free", PlanCategory.Personal)]
    [InlineData("Go", PlanCategory.Personal)]
    [InlineData("  pro  ", PlanCategory.Personal)]
    [InlineData(null, PlanCategory.Unknown)]
    [InlineData("", PlanCategory.Unknown)]
    [InlineData("   ", PlanCategory.Unknown)]
    public void FromPlan_FollowsSpecSection4(string? plan, PlanCategory expected)
    {
        Assert.Equal(expected, PlanCategories.FromPlan(plan));
    }
}
