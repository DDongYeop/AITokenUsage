using System.Text.RegularExpressions;
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Providers.Codex;

/// <summary>Codex CLI file locations (CODEX_HOME) and the ChatGPT backend usage URL.</summary>
public sealed class CodexPaths
{
    public const string DefaultBaseUrl = "https://chatgpt.com/backend-api/";

    // Top-level `chatgpt_base_url = "..."` or `chatgpt_base_url = '...'` (TOML basic or literal string).
    private static readonly Regex BaseUrlLine = new(
        @"^\s*chatgpt_base_url\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')",
        RegexOptions.CultureInvariant);

    public CodexPaths(string home)
    {
        Home = home;
    }

    public string Home { get; }
    public string AuthFile => Path.Combine(Home, "auth.json");
    public string ConfigFile => Path.Combine(Home, "config.toml");
    public string SessionsDirectory => Path.Combine(Home, "sessions");

    /// <summary>CODEX_HOME when defined and non-blank, otherwise %USERPROFILE%\.codex.</summary>
    public static CodexPaths Resolve(IEnvironment env)
    {
        var configured = env.GetVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
            return new CodexPaths(configured.Trim());
        return new CodexPaths(Path.Combine(env.UserProfile, ".codex"));
    }

    /// <summary>Reads a top-level chatgpt_base_url. Keys inside [tables] (profiles) are ignored.</summary>
    public static string? ReadConfiguredBaseUrl(string? configToml)
    {
        if (string.IsNullOrEmpty(configToml)) return null;
        foreach (var rawLine in configToml.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.TrimStart().StartsWith('['))
                return null;
            var match = BaseUrlLine.Match(line);
            if (match.Success)
            {
                var value = match.Groups["v"].Value.Trim();
                return value.Length == 0 ? null : value;
            }
        }
        return null;
    }

    /// <summary>Strips trailing '/', and appends /backend-api to chatgpt.com hosts that lack it.</summary>
    public static string NormalizeBaseUrl(string? configured)
    {
        var url = string.IsNullOrWhiteSpace(configured) ? DefaultBaseUrl : configured.Trim();
        url = url.TrimEnd('/');
        var isChatGptHost = url.StartsWith("https://chatgpt.com", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://chat.openai.com", StringComparison.OrdinalIgnoreCase);
        if (isChatGptHost && !url.Contains("/backend-api", StringComparison.OrdinalIgnoreCase))
            url += "/backend-api";
        return url;
    }

    /// <summary>{base}/wham/usage when the base contains /backend-api, otherwise {base}/api/codex/usage.</summary>
    public static string BuildUsageUrl(string normalizedBaseUrl)
        => normalizedBaseUrl.Contains("/backend-api", StringComparison.OrdinalIgnoreCase)
            ? normalizedBaseUrl + "/wham/usage"
            : normalizedBaseUrl + "/api/codex/usage";

    /// <summary>Reads config.toml (shared read) and returns the usage URL. Unreadable config means default.</summary>
    public string ResolveUsageUrl(IFileSystem files)
    {
        string? toml = null;
        try
        {
            toml = files.ReadAllTextShared(ConfigFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            toml = null;
        }
        return BuildUsageUrl(NormalizeBaseUrl(ReadConfiguredBaseUrl(toml)));
    }
}
