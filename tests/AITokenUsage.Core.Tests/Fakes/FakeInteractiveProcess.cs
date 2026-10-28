using System.Threading.Channels;
using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Tests.Fakes;

/// <summary>
/// Scripted stdin/stdout peer. ReadLineAsync returns queued lines in order, returns null after
/// CompleteOutput (EOF), and otherwise waits until the token is cancelled (an unresponsive child).
/// </summary>
public sealed class FakeInteractiveProcess : IInteractiveProcess
{
    private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
    private readonly List<string> _written = [];
    private Func<string, IEnumerable<string>>? _onWrite;

    /// <summary>Lines the code under test wrote to stdin, in order.</summary>
    public IReadOnlyList<string> Written
    {
        get
        {
            lock (_written)
            {
                return _written.ToArray();
            }
        }
    }

    public bool IsDisposed { get; private set; }

    public FakeInteractiveProcess EnqueueOutput(string line)
    {
        _output.Writer.TryWrite(line);
        return this;
    }

    /// <summary>Signals end of stdout: pending and later reads return null once the queue is empty.</summary>
    public FakeInteractiveProcess CompleteOutput()
    {
        _output.Writer.TryComplete();
        return this;
    }

    /// <summary>For every written line, the responder's lines are queued as stdout output.</summary>
    public FakeInteractiveProcess OnWrite(Func<string, IEnumerable<string>> responder)
    {
        _onWrite = responder;
        return this;
    }

    public Task WriteLineAsync(string line, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        lock (_written)
        {
            _written.Add(line);
        }

        if (_onWrite is not null)
        {
            foreach (string response in _onWrite(line))
            {
                _output.Writer.TryWrite(response);
            }
        }

        return Task.CompletedTask;
    }

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        while (await _output.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            if (_output.Reader.TryRead(out string? line))
            {
                return line;
            }
        }

        return null;
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        _output.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
