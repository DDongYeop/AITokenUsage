namespace AITokenUsage.Core.Abstractions;

public sealed class RealEnvironment : IEnvironment
{
    public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);

    public string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
}
