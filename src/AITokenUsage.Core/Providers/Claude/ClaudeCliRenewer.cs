using System.Globalization;
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Providers.Claude;

/// <summary>
/// Has Claude Code renew the login of an extra Claude account (spec 2026-11-16 §2.3). Runs a hidden
/// "claude mcp get aitokenusage-renew" with the account folder as CLAUDE_CONFIG_DIR and as working directory: Claude Code
/// renews an expired login when it starts and saves it under its own lock, and a server name nobody uses starts no
/// server (exit code 1 is normal). When that did not renew the login, "claude mcp list" is tried once.
/// Success is judged by the credentials file, not the exit code. The widget never writes that file and never calls the
/// token endpoint. Logs the command, exit code, timeout, duration and whether it renewed; never the command's output,
/// which names the account's claude.ai connectors.
/// </summary>
public sealed class ClaudeCliRenewer
{
    public const string CommandName = "claude";

    /// <summary>A server name nobody uses, so "mcp get" connects to nothing.</summary>
    public const string ProbeServerName = "aitokenusage-renew";

    public static readonly TimeSpan GetTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Removed from the command's environment: with any of them Claude Code would use another login and leave this one as it is.</summary>
    private static readonly string[] RemovedVariables =
    [
        ClaudePaths.SecureStorageDirVariable,
        "CLAUDE_CODE_OAUTH_TOKEN",
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_AUTH_TOKEN",
    ];

    private static readonly string[] GetArguments = ["mcp", "get", ProbeServerName];
    private static readonly string[] ListArguments = ["mcp", "list"];

    private readonly IProcessRunner _processes;
    private readonly ClaudeCredentialReader _reader;
    private readonly TimeProvider _time;
    private readonly IAppLog _log;

    public ClaudeCliRenewer(IProcessRunner processes, IFileSystem files, TimeProvider time, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _processes = processes;
        _reader = new ClaudeCredentialReader(files);
        _time = time;
        _log = log;
    }

    /// <summary>
    /// True when the login can be used without renewing: Ok with more than <see cref="ClaudeUsageProvider.RefreshMargin"/>
    /// left, or without an expiry time (Claude Code always writes one).
    /// </summary>
    public static bool IsFresh(ClaudeCredentialReadResult read, DateTimeOffset now) =>
        read is { State: ClaudeCredentialState.Ok, Credentials: { } credentials }
        && (credentials.ExpiresAt is not { } expiresAt || expiresAt - now > ClaudeUsageProvider.RefreshMargin);

    /// <summary>
    /// Runs the commands for the account folder <paramref name="accountDirectory"/> and returns the login read after the
    /// last one; null when nothing ran because claude is not on PATH. A command that cannot start counts as not renewed.
    /// Throws only when <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task<ClaudeCredentialReadResult?> RenewAsync(string accountDirectory, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountDirectory);

        string? claudePath = _processes.FindOnPath(CommandName);
        if (claudePath is null)
        {
            _log.Warn("Claude account renewal: claude is not on PATH; not renewed.");
            return null;
        }

        string credentialsPath = Path.Combine(accountDirectory, ClaudePaths.CredentialsFileName);
        ClaudeCredentialReadResult read = await RunAsync(
            "claude mcp get", BuildSpec(claudePath, accountDirectory, GetArguments, GetTimeout), credentialsPath, ct).ConfigureAwait(false);
        if (IsFresh(read, _time.GetUtcNow()))
        {
            return read;
        }

        // In case another Claude Code version renews only on a fuller start. It reaches every connector, so it comes second.
        return await RunAsync(
            "claude mcp list", BuildSpec(claudePath, accountDirectory, ListArguments, ListTimeout), credentialsPath, ct).ConfigureAwait(false);
    }

    /// <summary>claude.exe/.com runs directly; claude.cmd (npm shim) or anything else runs through cmd.exe /d /c claude.</summary>
    private static ProcessSpec BuildSpec(string claudePath, string accountDirectory, string[] arguments, TimeSpan timeout)
    {
        // Windows environment variable names ignore case.
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ClaudePaths.ConfigDirVariable] = accountDirectory,
        };
        foreach (string name in RemovedVariables)
        {
            environment[name] = null;
        }

        string extension = Path.GetExtension(claudePath);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".com", StringComparison.OrdinalIgnoreCase))
        {
            return new ProcessSpec(claudePath, arguments, environment, timeout, accountDirectory);
        }

        return new ProcessSpec("cmd.exe", ["/d", "/c", CommandName, .. arguments], environment, timeout, accountDirectory);
    }

    private async Task<ClaudeCredentialReadResult> RunAsync(string command, ProcessSpec spec, string credentialsPath, CancellationToken ct)
    {
        long started = _time.GetTimestamp();
        ProcessResult? result = null;
        string? startFailure = null;
        try
        {
            result = await _processes.RunAsync(spec, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Only the type: the message can hold a path.
            startFailure = ex.GetType().Name;
        }

        string seconds = _time.GetElapsedTime(started).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        ClaudeCredentialReadResult read = await _reader.ReadAsync(credentialsPath, ct).ConfigureAwait(false);
        bool renewed = IsFresh(read, _time.GetUtcNow());
        string outcome = renewed ? "renewed" : "not renewed";

        // The output is never logged: it names the account's connectors.
        string line = result is null
            ? $"Claude account renewal: {command} could not be started ({startFailure}), {seconds} s, {outcome}."
            : $"Claude account renewal: {command}: exit {result.ExitCode}, timed out {(result.TimedOut ? "yes" : "no")}, {seconds} s, {outcome}.";
        if (renewed)
        {
            _log.Info(line);
        }
        else
        {
            _log.Warn(line);
        }

        return read;
    }
}
