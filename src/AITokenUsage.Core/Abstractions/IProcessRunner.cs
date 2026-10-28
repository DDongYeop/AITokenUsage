namespace AITokenUsage.Core.Abstractions;

/// <param name="FileName">Executable path or name.</param>
/// <param name="Arguments">Each element is passed as one argument.</param>
/// <param name="EnvironmentOverrides">Variables set for the child process. A null value removes the variable.</param>
/// <param name="Timeout">
/// RunAsync: kill the process tree after this long (default 30 s).
/// StartInteractive: kill the process tree after this long (default: no limit).
/// </param>
public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?>? EnvironmentOverrides = null,
    TimeSpan? Timeout = null);

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    // Records print every member by default; stdout may hold a token (for example "gh auth token").
    public override string ToString() =>
        $"ProcessResult {{ ExitCode = {ExitCode}, TimedOut = {TimedOut}, StdOutLength = {StdOut.Length}, StdErrLength = {StdErr.Length} }}";
}

public interface IInteractiveProcess : IAsyncDisposable
{
    Task WriteLineAsync(string line, CancellationToken ct);

    /// <summary>Returns null at end of stream (stdout closed or the process exited).</summary>
    Task<string?> ReadLineAsync(CancellationToken ct);
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct);

    IInteractiveProcess StartInteractive(ProcessSpec spec);

    /// <summary>Full path resolved through PATH and PATHEXT, or null.</summary>
    string? FindOnPath(string commandName);
}
