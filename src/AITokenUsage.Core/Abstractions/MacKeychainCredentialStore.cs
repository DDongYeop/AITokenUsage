using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AITokenUsage.Core.Abstractions;

/// <summary>
/// macOS counterpart of <see cref="WindowsCredentialStore"/>: reads generic passwords from the login keychain through
/// <c>/usr/bin/security</c>. The "target name" is the keychain item's service name (for example
/// "Claude Code-credentials", which Claude Code writes on macOS instead of .credentials.json).
/// The first read of another app's item shows the macOS "allow access" prompt; "Always Allow" stops it repeating.
/// </summary>
public sealed class MacKeychainCredentialStore : ICredentialStore
{
    public const string SecurityTool = "/usr/bin/security";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly IAppLog _log;

    public MacKeychainCredentialStore(IAppLog? log = null)
    {
        _log = log ?? NullAppLog.Instance;
    }

    public string? ReadGeneric(string targetName)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetName);
        string? secret = Run(["find-generic-password", "-s", targetName, "-w"]);

        // keytar names Windows entries "service/account"; on macOS the same login is service + account.
        int slash = targetName.IndexOf('/');
        if (secret is null && slash > 0 && slash < targetName.Length - 1)
        {
            secret = Run(["find-generic-password", "-s", targetName[..slash], "-a", targetName[(slash + 1)..], "-w"]);
        }

        if (secret is null) return null;

        // `security -w` prints the password followed by a newline.
        secret = secret.TrimEnd('\n', '\r');
        return secret.Length == 0 ? null : secret;
    }

    /// <summary>
    /// The keychain has no prefix search, so "name*" reads the single item whose service is exactly "name".
    /// </summary>
    public IReadOnlyList<StoredCredential> EnumerateGeneric(string filter)
    {
        ArgumentException.ThrowIfNullOrEmpty(filter);
        string service = filter.TrimEnd('*');
        if (service.Length == 0) return [];

        string? secret = ReadGeneric(service);
        return secret is null ? [] : [new StoredCredential(service, null, secret)];
    }

    private string? Run(IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(SecurityTool)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return null;

            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                RealProcessRunner.KillTree(process);
                _log.Warn("keychain: security timed out (access prompt left open?)");
                return null;
            }

            // 44 = errSecItemNotFound; anything else (denied, locked) is logged and treated as missing.
            if (process.ExitCode != 0)
            {
                if (process.ExitCode != 44)
                {
                    _log.Info($"keychain: security exited {process.ExitCode}: {stderr.Result.Trim()}");
                }

                return null;
            }

            return stdout.Result;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            _log.Warn("keychain: could not run security", ex);
            return null;
        }
    }
}
