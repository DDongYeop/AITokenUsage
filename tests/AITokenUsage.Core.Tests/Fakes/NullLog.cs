using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Tests.Fakes;

public sealed class NullLog : IAppLog
{
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
