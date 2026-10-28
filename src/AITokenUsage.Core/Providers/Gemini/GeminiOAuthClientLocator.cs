using System.Text.RegularExpressions;
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Providers.Gemini;

public sealed record GeminiOAuthClient(string ClientId, string ClientSecret)
{
    public override string ToString() => $"GeminiOAuthClient {{ ClientId = {ClientId} }}";
}

/// <summary>
/// Finds gemini-cli's public installed-app OAuth client at runtime so the values never live in this repository.
/// </summary>
public sealed partial class GeminiOAuthClientLocator(IEnvironment env, IFileSystem files, IProcessRunner processes)
{
    public const string EnvClientId = "GEMINI_OAUTH_CLIENT_ID";
    public const string EnvClientSecret = "GEMINI_OAUTH_CLIENT_SECRET";

    private static readonly string[] PackageNames = ["gemini-cli", "gemini-cli-core"];

    private readonly object _gate = new();
    private GeminiOAuthClient? _cached;

    public GeminiOAuthClient? Locate()
    {
        lock (_gate)
        {
            return _cached ??= LocateCore();
        }
    }

    public static GeminiOAuthClient? ParseScript(string js)
    {
        var id = ClientIdPattern().Match(js);
        var secret = ClientSecretPattern().Match(js);
        return id.Success && secret.Success ? new GeminiOAuthClient(id.Groups[1].Value, secret.Groups[1].Value) : null;
    }

    private GeminiOAuthClient? LocateCore()
    {
        var envId = env.GetVariable(EnvClientId);
        var envSecret = env.GetVariable(EnvClientSecret);
        if (!string.IsNullOrWhiteSpace(envId) && !string.IsNullOrWhiteSpace(envSecret))
        {
            return new GeminiOAuthClient(envId.Trim(), envSecret.Trim());
        }

        foreach (var script in CandidateScripts())
        {
            try
            {
                if (files.ReadAllTextShared(script) is { } js && ParseScript(js) is { } client)
                {
                    return client;
                }
            }
            catch (IOException)
            {
                // Unreadable bundle chunk: try the next candidate.
            }
        }

        return null;
    }

    private IEnumerable<string> CandidateScripts()
    {
        foreach (var nodeModules in NodeModulesRoots())
        {
            foreach (var package in PackageNames)
            {
                var root = Path.Combine(nodeModules, "@google", package);
                foreach (var chunk in files.EnumerateFiles(Path.Combine(root, "bundle"), "*.js", recursive: false))
                {
                    yield return chunk;
                }

                var oauth2 = Path.Combine(root, "dist", "src", "code_assist", "oauth2.js");
                if (files.FileExists(oauth2))
                {
                    yield return oauth2;
                }
            }
        }
    }

    private IEnumerable<string> NodeModulesRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (processes.FindOnPath("gemini") is { } shim && Path.GetDirectoryName(shim) is { } shimDir)
        {
            var fromPath = Path.Combine(shimDir, "node_modules");
            if (seen.Add(fromPath))
            {
                yield return fromPath;
            }
        }

        var npmGlobal = Path.Combine(env.RoamingAppData, "npm", "node_modules");
        if (seen.Add(npmGlobal))
        {
            yield return npmGlobal;
        }
    }

    [GeneratedRegex("""OAUTH_CLIENT_ID\s*=\s*['"]([\w\-\.]+)['"]""")]
    private static partial Regex ClientIdPattern();

    [GeneratedRegex("""OAUTH_CLIENT_SECRET\s*=\s*['"]([\w\-]+)['"]""")]
    private static partial Regex ClientSecretPattern();
}
