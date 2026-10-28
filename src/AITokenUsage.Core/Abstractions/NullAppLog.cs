namespace AITokenUsage.Core.Abstractions;

public sealed class NullAppLog : IAppLog
{
    public static readonly NullAppLog Instance = new();

    public void Info(string message)
    {
    }

    public void Warn(string message, Exception? ex = null)
    {
    }

    public void Error(string message, Exception? ex = null)
    {
    }
}
