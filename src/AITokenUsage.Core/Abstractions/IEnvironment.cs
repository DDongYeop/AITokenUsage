namespace AITokenUsage.Core.Abstractions;

public interface IEnvironment
{
    /// <summary>Returns null when the variable is not defined and "" when it is defined but empty.</summary>
    string? GetVariable(string name);

    string UserProfile { get; }

    string LocalAppData { get; }

    string RoamingAppData { get; }
}
