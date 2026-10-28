namespace AITokenUsage.Core.Abstractions;

/// <summary>
/// Application log. Messages are English and must never contain tokens,
/// Authorization headers or raw response bodies.
/// </summary>
public interface IAppLog
{
    void Info(string message);

    void Warn(string message, Exception? ex = null);

    void Error(string message, Exception? ex = null);
}
