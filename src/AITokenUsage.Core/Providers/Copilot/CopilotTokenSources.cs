using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Copilot;

/// <summary>
/// Yields GitHub token candidates lazily, in spec order: saved device-flow token → env vars →
/// gh auth token → Copilot CLI Credential Manager entries → language-server apps.json/hosts.json.
/// Duplicate tokens are yielded once. Nothing here validates a token; the provider does that live.
/// </summary>
public sealed class CopilotTokenSources(ProviderContext ctx)
{
    public static readonly IReadOnlyList<string> EnvironmentVariables = ["COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN"];
    public static readonly TimeSpan GitHubCliTimeout = TimeSpan.FromSeconds(5);

    private static readonly string[] AcceptedEnvPrefixes = ["gho_", "ghu_", "github_pat_"];
    private static readonly string[] LanguageServerFiles = ["apps.json", "hosts.json"];

    /// <summary>Classic PATs (ghp_) and unknown prefixes are rejected, as the Copilot CLI does.</summary>
    public static bool HasAcceptedPrefix(string token) =>
        AcceptedEnvPrefixes.Any(prefix => token.StartsWith(prefix, StringComparison.Ordinal));

    public async IAsyncEnumerable<CopilotTokenCandidate> EnumerateAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var saved = CopilotSavedToken.Load(ctx.Secrets, ctx.Log);
        if (saved is not null && seen.Add(saved.Token))
        {
            yield return new CopilotTokenCandidate(CopilotTokenSourceKind.Saved, "saved", saved.Token, saved.Login);
        }

        foreach (var name in EnvironmentVariables)
        {
            var value = ctx.Env.GetVariable(name)?.Trim();
            if (!string.IsNullOrEmpty(value) && HasAcceptedPrefix(value) && seen.Add(value))
            {
                yield return new CopilotTokenCandidate(CopilotTokenSourceKind.Environment, $"env:{name}", value, null);
            }
        }

        var fromGitHubCli = await ReadGitHubCliAsync(ct).ConfigureAwait(false);
        if (fromGitHubCli is not null && seen.Add(fromGitHubCli.Token))
        {
            yield return fromGitHubCli;
        }

        foreach (var candidate in ReadCopilotCliCredentials())
        {
            if (seen.Add(candidate.Token))
            {
                yield return candidate;
            }
        }

        foreach (var fileName in LanguageServerFiles)
        {
            foreach (var candidate in ReadLanguageServerFile(fileName))
            {
                if (seen.Add(candidate.Token))
                {
                    yield return candidate;
                }
            }
        }
    }

    private async Task<CopilotTokenCandidate?> ReadGitHubCliAsync(CancellationToken ct)
    {
        var gh = ctx.Processes.FindOnPath("gh");
        if (gh is null)
        {
            return null;
        }

        // Remove GH_TOKEN/GITHUB_TOKEN so gh returns its stored login instead of echoing the env token.
        var spec = new ProcessSpec(
            gh,
            ["auth", "token", "--hostname", "github.com"],
            new Dictionary<string, string?> { ["GH_TOKEN"] = null, ["GITHUB_TOKEN"] = null },
            GitHubCliTimeout);

        ProcessResult result;
        try
        {
            result = await ctx.Processes.RunAsync(spec, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            ctx.Log.Warn("Copilot: could not run gh", ex);
            return null;
        }

        if (result.TimedOut || result.ExitCode != 0)
        {
            ctx.Log.Info($"Copilot: gh auth token unavailable (exit {result.ExitCode}, timed out {result.TimedOut})");
            return null;
        }

        var token = result.StdOut
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return string.IsNullOrEmpty(token)
            ? null
            : new CopilotTokenCandidate(CopilotTokenSourceKind.GitHubCli, "gh auth token", token, null);
    }

    private List<CopilotTokenCandidate> ReadCopilotCliCredentials()
    {
        IReadOnlyList<StoredCredential> items;
        try
        {
            items = ctx.Credentials.EnumerateGeneric("copilot-cli*");
        }
        catch (Win32Exception ex)
        {
            ctx.Log.Warn("Copilot: CredEnumerate failed", ex);
            return [];
        }

        var result = new List<CopilotTokenCandidate>();
        foreach (var item in items)
        {
            var token = item.Secret.Trim();
            if (token.Length > 0)
            {
                result.Add(new CopilotTokenCandidate(CopilotTokenSourceKind.CopilotCli, $"credman:{item.TargetName}",
                    token, LoginFromKeytarAccount(item.UserName)));
            }
        }

        return result;
    }

    private List<CopilotTokenCandidate> ReadLanguageServerFile(string fileName)
    {
        var path = Path.Combine(ctx.Env.LocalAppData, "github-copilot", fileName);
        string? text;
        try
        {
            text = ctx.Files.ReadAllTextShared(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ctx.Log.Warn($"Copilot: could not read {fileName}", ex);
            return [];
        }

        var result = new List<CopilotTokenCandidate>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(text, SharedJson.Lenient);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (!entry.Name.Contains("github.com", StringComparison.OrdinalIgnoreCase)
                    || entry.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var token = Json.Str(entry.Value, "oauth_token") ?? Json.Str(entry.Value, "access_token");
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                var login = Json.Str(entry.Value, "user") ?? Json.Str(entry.Value, "login");
                result.Add(new CopilotTokenCandidate(CopilotTokenSourceKind.LanguageServerFile, $"file:{fileName}", token.Trim(), login));
            }
        }
        catch (JsonException)
        {
            ctx.Log.Warn($"Copilot: {fileName} is not valid JSON");
        }

        return result;
    }

    /// <summary>keytar account "https://github.com:&lt;login&gt;" → login.</summary>
    private static string? LoginFromKeytarAccount(string? account)
    {
        if (string.IsNullOrEmpty(account))
        {
            return null;
        }

        var login = account[(account.LastIndexOf(':') + 1)..];
        return login.Length == 0 || login.Contains('/') ? null : login;
    }
}
