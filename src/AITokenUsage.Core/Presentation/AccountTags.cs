using System.Globalization;
using AITokenUsage.Core.Accounts;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Resources;

namespace AITokenUsage.Core.Presentation;

/// <summary>Tells apart several panels of the same AI by plan: "팀", "개인" or "#2" (spec §4).</summary>
public static class AccountTags
{
    /// <summary>
    /// A new list in the same order. Only views whose kind (<see cref="ProviderIds.KindOf"/>) is claude or codex
    /// and that share their kind with at least one other view in the list get an <see cref="ProviderView.AccountTag"/>;
    /// every other view is the same instance. Tag: Team → "팀"/"Team", Personal → "개인"/"Solo", Unknown → "#n".
    /// When a tag repeats within a kind, the second and later ones get " n". n is the 1-based position among the views of that kind.
    /// </summary>
    public static IReadOnlyList<ProviderView> Apply(IReadOnlyList<ProviderView> enabledInOrder)
    {
        ArgumentNullException.ThrowIfNull(enabledInOrder);

        var kindCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ProviderView view in enabledInOrder)
        {
            string kind = ProviderIds.KindOf(view.ProviderId);
            if (AccountIds.SupportedKinds.Contains(kind, StringComparer.Ordinal))
            {
                kindCounts[kind] = kindCounts.GetValueOrDefault(kind) + 1;
            }
        }

        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        var usedTags = new HashSet<(string Kind, string Tag)>();
        var result = new List<ProviderView>(enabledInOrder.Count);
        foreach (ProviderView view in enabledInOrder)
        {
            string kind = ProviderIds.KindOf(view.ProviderId);
            if (kindCounts.GetValueOrDefault(kind) < 2)
            {
                result.Add(view);
                continue;
            }

            int n = position[kind] = position.GetValueOrDefault(kind) + 1;
            string tag = BaseTag(PlanCategories.FromPlan(view.DisplayPlan), n);
            if (!usedTags.Add((kind, tag)))
            {
                tag = tag + " " + n.ToString(CultureInfo.InvariantCulture);
            }

            result.Add(view with { AccountTag = tag });
        }

        return result;
    }

    private static string BaseTag(PlanCategory category, int n) => category switch
    {
        PlanCategory.Team => Strings.AccountTagTeam,
        PlanCategory.Personal => Strings.AccountTagPersonal,
        _ => "#" + n.ToString(CultureInfo.InvariantCulture),
    };
}
