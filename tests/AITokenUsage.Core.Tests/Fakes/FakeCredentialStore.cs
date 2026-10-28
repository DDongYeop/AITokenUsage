using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Tests.Fakes;

/// <summary>In-memory Credential Manager. Filters follow CredEnumerate: "prefix*" or an exact name.</summary>
public sealed class FakeCredentialStore : ICredentialStore
{
    private readonly List<StoredCredential> _items = [];

    public FakeCredentialStore Add(string targetName, string? userName, string secret)
    {
        _items.RemoveAll(c => string.Equals(c.TargetName, targetName, StringComparison.OrdinalIgnoreCase));
        _items.Add(new StoredCredential(targetName, userName, secret));
        return this;
    }

    public string? ReadGeneric(string targetName) =>
        _items.FirstOrDefault(c => string.Equals(c.TargetName, targetName, StringComparison.OrdinalIgnoreCase))?.Secret;

    public IReadOnlyList<StoredCredential> EnumerateGeneric(string filter) =>
        _items.Where(c => Matches(filter, c.TargetName)).ToList();

    private static bool Matches(string filter, string targetName)
    {
        if (filter.EndsWith('*'))
        {
            return targetName.StartsWith(filter[..^1], StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(filter, targetName, StringComparison.OrdinalIgnoreCase);
    }
}
