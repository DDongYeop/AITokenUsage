using System.Globalization;
using AITokenUsage.Core.Accounts;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Resources;
using AITokenUsage.Core.Settings;

namespace AITokenUsage.Core.Presentation;

/// <summary>Tells apart several panels of the same AI by plan: "팀", "개인" or "#2" (spec §4).</summary>
public static class AccountTags
{
    /// <summary>
    /// A new list in the same order. Only views whose kind (<see cref="ProviderIds.KindOf"/>) is claude or codex
    /// and that share their kind with at least one other view in the list get an <see cref="ProviderView.AccountTag"/>;
    /// nicknames are matched by full provider ID, independently of the tag. Tag: Team → "팀"/"Team", Personal → "개인"/"Solo", Unknown → "#n".
    /// When a tag repeats within a kind, the second and later ones get " n". n is the 1-based position among the views of that kind.
    /// </summary>
    public static IReadOnlyList<ProviderView> Apply(
        IReadOnlyList<ProviderView> enabledInOrder, IReadOnlyList<ProviderSettings>? providers = null)
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
            string? nickname = providers?.FirstOrDefault(p => p.Id == view.ProviderId)?.Nickname;
            string? tag = null;
            if (kindCounts.GetValueOrDefault(kind) >= 2)
            {
                int n = position[kind] = position.GetValueOrDefault(kind) + 1;
                tag = BaseTag(PlanCategories.FromPlan(view.DisplayPlan), n);
                if (!usedTags.Add((kind, tag)))
                {
                    tag += " " + n.ToString(CultureInfo.InvariantCulture);
                }
            }

            result.Add(view.AccountTag == tag && view.Nickname == nickname
                ? view
                : view with { AccountTag = tag, Nickname = nickname });
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
