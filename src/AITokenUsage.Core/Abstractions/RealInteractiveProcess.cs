using System.Diagnostics;

namespace AITokenUsage.Core.Abstractions;

/// <summary>A running child process driven line by line over stdin/stdout (JSON-RPC style).</summary>
internal sealed class RealInteractiveProcess : IInteractiveProcess
{
    private static readonly TimeSpan ExitGracePeriod = TimeSpan.FromSeconds(2);

    private readonly Process _process;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _stderrDrain;
    private Task<string?>? _pendingRead;
    private bool _disposed;

    public RealInteractiveProcess(Process process, TimeSpan? timeout)
    {
        _process = process;
        _process.StandardInput.NewLine = "\n";
        _process.StandardInput.AutoFlush = true;

        // Keep stderr flowing so the child never blocks on a full pipe.
        _stderrDrain = _process.StandardError.ReadToEndAsync(CancellationToken.None);

        _lifetime.Token.Register(() => RealProcessRunner.KillTree(_process));
        if (timeout is { } limit)
        {
            _lifetime.CancelAfter(limit);
        }
    }

    public async Task WriteLineAsync(string line, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
    }

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Pipe reads do not observe cancellation reliably, so the read itself is never cancelled.
        // A read abandoned by a cancelled caller is reused by the next call instead of being lost.
        _pendingRead ??= _process.StandardOutput.ReadLineAsync(CancellationToken.None).AsTask();
        string? line = await _pendingRead.WaitAsync(ct).ConfigureAwait(false);
        _pendingRead = null;
        return line;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The pipe is already broken because the child exited.
        }

        try
        {
            using var grace = new CancellationTokenSource(ExitGracePeriod);
            await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RealProcessRunner.KillTree(_process);
        }

        try
        {
            await _stderrDrain.WaitAsync(ExitGracePeriod).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException)
        {
            // Nothing useful to do with a stuck stderr pipe.
        }

        _lifetime.Dispose();
        _process.Dispose();
    }
}
