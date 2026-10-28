using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Tests.Fakes;

public sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _items = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Items => _items;

    public void Save(string name, string secret) => _items[name] = secret;

    public string? Load(string name) => _items.TryGetValue(name, out string? secret) ? secret : null;

    public void Delete(string name) => _items.Remove(name);
}
