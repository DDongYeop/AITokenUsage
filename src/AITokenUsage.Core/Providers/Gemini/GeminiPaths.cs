using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Providers.Gemini;

/// <summary>
/// Gemini CLI file locations. GEMINI_CLI_HOME replaces the home directory itself
/// (it does not point at the .gemini folder).
/// </summary>
public sealed class GeminiPaths
{
    public GeminiPaths(IEnvironment env)
    {
        var overrideHome = env.GetVariable("GEMINI_CLI_HOME");
        Home = string.IsNullOrWhiteSpace(overrideHome) ? env.UserProfile : overrideHome.Trim();
        GeminiDir = Path.Combine(Home, ".gemini");
        OAuthCredsPath = Path.Combine(GeminiDir, "oauth_creds.json");
        SettingsPath = Path.Combine(GeminiDir, "settings.json");
        GeminiDotEnvPath = Path.Combine(GeminiDir, ".env");
        HomeDotEnvPath = Path.Combine(Home, ".env");
    }

    public string Home { get; }
    public string GeminiDir { get; }
    public string OAuthCredsPath { get; }
    public string SettingsPath { get; }
    public string GeminiDotEnvPath { get; }
    public string HomeDotEnvPath { get; }
}
