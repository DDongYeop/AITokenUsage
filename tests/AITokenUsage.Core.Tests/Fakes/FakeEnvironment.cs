using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Tests.Fakes;

public sealed class FakeEnvironment : IEnvironment
{
    private readonly Dictionary<string, string> _variables = new(StringComparer.OrdinalIgnoreCase);

    public FakeEnvironment(string userProfile = @"C:\Users\test", string? localAppData = null, string? roamingAppData = null)
    {
        UserProfile = userProfile;
        LocalAppData = localAppData ?? Path.Combine(userProfile, "AppData", "Local");
        RoamingAppData = roamingAppData ?? Path.Combine(userProfile, "AppData", "Roaming");
    }

    public string UserProfile { get; }

    public string LocalAppData { get; }

    public string RoamingAppData { get; }

    /// <summary>Defines a variable ("" = defined but empty). A null value removes it.</summary>
    public FakeEnvironment Set(string name, string? value)
    {
        if (value is null)
        {
            _variables.Remove(name);
        }
        else
        {
            _variables[name] = value;
        }

        return this;
    }

    public string? GetVariable(string name) => _variables.TryGetValue(name, out string? value) ? value : null;
}
