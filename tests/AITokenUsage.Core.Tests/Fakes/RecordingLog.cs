using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Tests.Fakes;

public sealed record LogEntry(string Level, string Message, Exception? Exception);

/// <summary>Captures log calls so tests can assert on warnings and on the absence of secrets.</summary>
public sealed class RecordingLog : IAppLog
{
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }
    }

    /// <summary>Every message and exception text joined together, for "must not contain the token" assertions.</summary>
    public string AllText => string.Join("\n", Entries.Select(e => $"{e.Level} {e.Message} {e.Exception}"));

    public void Info(string message) => Add("INFO", message, null);

    public void Warn(string message, Exception? ex = null) => Add("WARN", message, ex);

    public void Error(string message, Exception? ex = null) => Add("ERROR", message, ex);

    private void Add(string level, string message, Exception? ex)
    {
        lock (_entries)
        {
            _entries.Add(new LogEntry(level, message, ex));
        }
    }
}
