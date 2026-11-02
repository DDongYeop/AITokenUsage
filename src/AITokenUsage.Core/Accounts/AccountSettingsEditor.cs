using AITokenUsage.Core.Models;
using AITokenUsage.Core.Settings;

namespace AITokenUsage.Core.Accounts;

/// <summary>Adds and removes extra-account entries in the settings provider list (spec §2.2-5, §5).</summary>
public static class AccountSettingsEditor
{
    /// <summary>Tries before giving up on a fresh key (only reachable with a broken random source).</summary>
    public const int MaxCreateAttempts = 100;

    /// <summary>A new account id of <paramref name="kind"/> that is not in <paramref name="existingIds"/>.</summary>
    public static string NewAccountId(string kind, IEnumerable<string> existingIds, Func<int, int>? nextInt = null)
    {
        ArgumentNullException.ThrowIfNull(existingIds);

        var existing = new HashSet<string>(existingIds, StringComparer.Ordinal);
        for (int attempt = 0; attempt < MaxCreateAttempts; attempt++)
        {
            string id = AccountIds.Create(kind, nextInt);
            if (!existing.Contains(id))
            {
                return id;
            }
        }

        throw new InvalidOperationException("Could not create an unused account id.");
    }

    /// <summary>Right after the last entry of <paramref name="kind"/>; the end of the list when there is none.</summary>
    public static int InsertIndex(IReadOnlyList<ProviderSettings> providers, string kind)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(kind);

        for (int i = providers.Count - 1; i >= 0; i--)
        {
            if (string.Equals(ProviderIds.KindOf(providers[i].Id ?? ""), kind, StringComparison.Ordinal))
            {
                return i + 1;
            }
        }

        return providers.Count;
    }

    /// <summary>
    /// Inserts the default entry (<see cref="ProviderDefaults.CreateDefault"/>: enabled, default lines) for
    /// <paramref name="accountId"/> after the last entry of its kind. An id already in the list is left where it is.
    /// Returns the entry in the list.
    /// </summary>
    public static ProviderSettings Add(List<ProviderSettings> providers, string accountId)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (!AccountIds.IsAccountId(accountId))
        {
            throw new ArgumentException($"Not an account id: '{accountId}'.", nameof(accountId));
        }

        if (providers.FirstOrDefault(p => string.Equals(p.Id, accountId, StringComparison.Ordinal)) is { } existing)
        {
            return existing;
        }

        ProviderSettings entry = ProviderDefaults.CreateDefault(accountId);
        providers.Insert(InsertIndex(providers, ProviderIds.KindOf(accountId)), entry);
        return entry;
    }

    /// <summary>Removes the entry of an extra account. The built-in panels cannot be removed. False when it was not there.</summary>
    public static bool Remove(List<ProviderSettings> providers, string accountId)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (!AccountIds.IsAccountId(accountId))
        {
            throw new ArgumentException($"Not an account id: '{accountId}'.", nameof(accountId));
        }

        return providers.RemoveAll(p => string.Equals(p.Id, accountId, StringComparison.Ordinal)) > 0;
    }

    /// <summary>How many of <paramref name="ids"/> are panels of <paramref name="kind"/> (the built-in one and its accounts).</summary>
    public static int CountOfKind(IEnumerable<string> ids, string kind)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(kind);

        return ids.Count(id => id is not null && string.Equals(ProviderIds.KindOf(id), kind, StringComparison.Ordinal));
    }
}
