using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Providers.Claude;

/// <summary>
/// Picks the version for "User-Agent: claude-code/&lt;ver&gt;":
/// running claude.exe file version, then the highest VS Code extension folder version, then <see cref="FallbackVersion"/>.
/// </summary>
public sealed partial class ClaudeVersionProbe
{
    public const string FallbackVersion = "2.1.311";

    private readonly IEnvironment _env;
    private readonly Func<string, IEnumerable<string>> _listExtensionDirectories;
    private readonly Func<IEnumerable<string?>> _runningClaudeVersions;

    /// <param name="env">Supplies %USERPROFILE%.</param>
    /// <param name="listExtensionDirectories">Given %USERPROFILE%\.vscode\extensions, returns sub-directory names or full paths.</param>
    /// <param name="runningClaudeVersions">Returns raw version strings (e.g. FileVersion "2.1.311.0") of running claude.exe processes.</param>
    public ClaudeVersionProbe(
        IEnvironment env,
        Func<string, IEnumerable<string>>? listExtensionDirectories = null,
        Func<IEnumerable<string?>>? runningClaudeVersions = null)
    {
        _env = env;
        _listExtensionDirectories = listExtensionDirectories ?? ListClaudeExtensionDirectories;
        _runningClaudeVersions = runningClaudeVersions ?? ReadRunningClaudeVersions;
    }

    public string GetVersion()
    {
        Version? best = Highest(Collect(_runningClaudeVersions));
        if (best is null)
        {
            string extensionsDirectory = ClaudePaths.VsCodeExtensionsDirectory(_env);
            best = Highest(Collect(() => _listExtensionDirectories(extensionsDirectory).Select(ExtensionFolderVersion)));
        }

        return best is null ? FallbackVersion : $"{best.Major}.{best.Minor}.{best.Build}";
    }

    /// <summary>Parses the leading "major.minor.patch" of strings like "2.1.311.0" or "2.1.200 (Claude Code)".</summary>
    public static bool TryParseVersion(string? text, [NotNullWhen(true)] out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        Match match = LeadingVersionRegex().Match(text);
        return match.Success && Version.TryParse(match.Groups["ver"].Value, out version);
    }

    /// <summary>
    /// Accepts only Claude Code's own binary (ProductName "Claude Code", FileVersion "2.1.311.0").
    /// Claude Desktop also ships a claude.exe (ProductName "Claude", FileVersion "4.29.0") and must be ignored.
    /// </summary>
    internal static Version? TryReadClaudeCodeVersion(string? productName, string? fileVersion)
    {
        if (productName is null || !productName.TrimStart().StartsWith("Claude Code", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return TryParseVersion(fileVersion, out Version? version) ? version : null;
    }

    private static Version? Highest(IEnumerable<string?> candidates)
    {
        Version? best = null;
        foreach (string? candidate in candidates)
        {
            if (TryParseVersion(candidate, out Version? version) && (best is null || version > best))
            {
                best = version;
            }
        }

        return best;
    }

    private static List<string?> Collect(Func<IEnumerable<string?>> source)
    {
        try
        {
            return source().ToList();
        }
        catch (Exception)
        {
            // Version detection is best-effort; any failure falls through to the next source.
            return [];
        }
    }

    private static string? ExtensionFolderVersion(string entry)
    {
        string name = Path.GetFileName(entry.TrimEnd('\\', '/'));
        Match match = ExtensionFolderRegex().Match(name);
        return match.Success ? match.Groups["ver"].Value : null;
    }

    private static IEnumerable<string> ListClaudeExtensionDirectories(string extensionsDirectory)
    {
        if (!Directory.Exists(extensionsDirectory))
        {
            return [];
        }

        try
        {
            return Directory.GetDirectories(extensionsDirectory, "anthropic.claude-code-*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string?> ReadRunningClaudeVersions()
    {
        var versions = new List<string?>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Process process in Process.GetProcessesByName("claude"))
        {
            using (process)
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (string.IsNullOrEmpty(path) || !seenPaths.Add(path))
                    {
                        continue;
                    }

                    FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                    if (TryReadClaudeCodeVersion(info.ProductName, info.FileVersion) is { } version)
                    {
                        versions.Add($"{version.Major}.{version.Minor}.{version.Build}");
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException
                                               or UnauthorizedAccessException or IOException)
                {
                    // The process exited or is not accessible; skip it.
                }
            }
        }

        return versions;
    }

    [GeneratedRegex(@"^\s*v?(?<ver>\d{1,9}\.\d{1,9}\.\d{1,9})", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingVersionRegex();

    [GeneratedRegex(@"^anthropic\.claude-code-(?<ver>\d{1,9}\.\d{1,9}\.\d{1,9})(?:-[A-Za-z0-9_-]+)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionFolderRegex();
}
