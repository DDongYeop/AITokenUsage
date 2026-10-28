using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace AITokenUsage.Core.Abstractions;

/// <summary>
/// Thread-safe text log at &lt;directory&gt;\app.log. When app.log reaches 1 MB it is rotated to
/// app.1.log (and app.1.log to app.2.log), so at most 3 files exist. Logging never throws.
/// Info/Warn/Error only format the line and enqueue it, so they never block the caller (for example
/// the UI thread) on disk I/O. One background writer creates the folder, rotates and appends, in order.
/// Call <see cref="Flush"/> or <see cref="Dispose"/> on shutdown so queued lines reach the disk.
/// </summary>
public sealed class FileAppLog : IAppLog, IDisposable
{
    public const long DefaultMaxBytes = 1024 * 1024;
    public const int RetainedFiles = 3;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Upper bound for Flush/Dispose so a hung disk can never hang shutdown.</summary>
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(5);

    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    // Guards the two counters below. Held only for a counter update or a Monitor.Wait, never during I/O.
    private readonly object _progressGate = new();
    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly TimeProvider _time;
    private long _enqueued;
    private long _processed;
    private bool _writerStopped;
    private int _disposed;

    public FileAppLog(string directory)
        : this(directory, DefaultMaxBytes, TimeProvider.System)
    {
    }

    internal FileAppLog(string directory, long maxBytes, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        _directory = directory;
        _maxBytes = maxBytes;
        _time = time;
        _ = Task.Run(WriteLoopAsync);
    }

    public string FilePath => Path.Combine(_directory, "app.log");

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    public void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    /// <summary>
    /// Blocks until every line enqueued before this call has been written (or dropped after an IO error),
    /// at most 5 seconds. Safe to call from any thread, repeatedly, and after Dispose.
    /// </summary>
    public void Flush()
    {
        long deadline = Environment.TickCount64 + (long)FlushTimeout.TotalMilliseconds;
        lock (_progressGate)
        {
            long target = _enqueued;
            while (_processed < target && !_writerStopped)
            {
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0 || !Monitor.Wait(_progressGate, TimeSpan.FromMilliseconds(remaining)))
                {
                    return;
                }
            }
        }
    }

    /// <summary>Stops accepting lines, then flushes what is queued. Later Info/Warn/Error calls are ignored.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_progressGate)
        {
            _queue.Writer.TryComplete();
        }

        Flush();
    }

    private void Write(string level, string message, Exception? ex)
    {
        var entry = new StringBuilder();
        entry.Append(_time.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
        entry.Append(" [").Append(level).Append("] ").Append(message);
        if (ex is not null)
        {
            entry.AppendLine();
            entry.Append("    ").Append(ex.ToString().ReplaceLineEndings(Environment.NewLine + "    "));
        }

        entry.AppendLine();

        // TryWrite on an unbounded channel never blocks; it only fails after Dispose (line dropped).
        // Counting under the gate keeps Flush's target consistent with what is really in the queue.
        lock (_progressGate)
        {
            if (_queue.Writer.TryWrite(entry.ToString()))
            {
                _enqueued++;
            }
        }
    }

    private async Task WriteLoopAsync()
    {
        ChannelReader<string> reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out string? entry))
                {
                    Append(entry);
                    lock (_progressGate)
                    {
                        _processed++;
                        Monitor.PulseAll(_progressGate);
                    }
                }
            }
        }
        finally
        {
            lock (_progressGate)
            {
                _writerStopped = true;
                Monitor.PulseAll(_progressGate);
            }
        }
    }

    private void Append(string entry)
    {
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception)
        {
            // Logging must never take the app down; the append below fails too and drops the line.
        }

        try
        {
            RotateIfNeeded();
        }
        catch (Exception)
        {
            // A locked archive must not stop logging: append anyway. Losing the size cap
            // temporarily is better than dropping every later line.
        }

        try
        {
            File.AppendAllText(FilePath, entry, Utf8NoBom);
        }
        catch (Exception)
        {
            // Logging must never take the app down, and the writer must survive any failure
            // (IOException, UnauthorizedAccessException, an invalid path, ...). The line is dropped.
        }
    }

    private void RotateIfNeeded()
    {
        var current = new FileInfo(FilePath);
        if (!current.Exists || current.Length < _maxBytes)
        {
            return;
        }

        // app.1.log -> app.2.log, then app.log -> app.1.log. The oldest archive is overwritten.
        for (int index = RetainedFiles - 1; index >= 1; index--)
        {
            string source = index == 1 ? FilePath : ArchivePath(index - 1);
            if (File.Exists(source))
            {
                File.Move(source, ArchivePath(index), overwrite: true);
            }
        }
    }

    private string ArchivePath(int index) => Path.Combine(_directory, $"app.{index}.log");
}
