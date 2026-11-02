namespace AITokenUsage.Core.Presentation;

/// <summary>Whether a plan belongs to a team/organization or to one person (spec §4).</summary>
public enum PlanCategory
{
    Unknown,
    Personal,
    Team,
}

public static class PlanCategories
{
    private static readonly string[] TeamWords = ["team", "enterprise", "business", "edu"];

    /// <summary>
    /// Team when the lower-cased label contains team, enterprise, business or edu, or starts with "ent";
    /// Personal for any other non-blank label (max, pro, plus, free, go...); Unknown when null or blank.
    /// </summary>
    public static PlanCategory FromPlan(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan))
        {
            return PlanCategory.Unknown;
        }

        string lower = plan.Trim().ToLowerInvariant();
        bool team = lower.StartsWith("ent", StringComparison.Ordinal)
            || TeamWords.Any(word => lower.Contains(word, StringComparison.Ordinal));
        return team ? PlanCategory.Team : PlanCategory.Personal;
    }
}
