using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace AITokenUsage.Core.Tests.Abstractions;

public sealed class FileAppLogTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Writes_LevelMessageAndException()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 28, 3, 4, 5, TimeSpan.Zero));
        string logs = _temp.Combine("logs");
        using var log = new FileAppLog(logs, FileAppLog.DefaultMaxBytes, time);

        log.Info("started");
        log.Warn("claude fetch failed", new HttpRequestException("connection reset"));
        log.Error("boom");
        log.Flush();

        string text = File.ReadAllText(Path.Combine(logs, "app.log"));
        Assert.Contains("2026-10-28 03:04:05.000 +00:00 [INFO] started", text);
        Assert.Contains("[WARN] claude fetch failed", text);
        Assert.Contains("System.Net.Http.HttpRequestException: connection reset", text);
        Assert.Contains("[ERROR] boom", text);
    }

    [Fact]
    public void Rotates_AndKeepsAtMostThreeFiles()
    {
        using var log = new FileAppLog(_temp.Path, maxBytes: 200, TimeProvider.System);

        for (int i = 0; i < 40; i++)
        {
            log.Info($"message number {i:D3} with some padding to grow the file");
        }

        log.Flush();

        string[] files = Directory.GetFiles(_temp.Path).Select(f => Path.GetFileName(f)).Order().ToArray();
        Assert.Equal(["app.1.log", "app.2.log", "app.log"], files);
        Assert.Contains("message number 039", File.ReadAllText(_temp.Combine("app.log")));
        Assert.All(files, f => Assert.True(new FileInfo(_temp.Combine(f)).Length < 400));
    }

    [Fact]
    public void RotationFailure_DoesNotStopAppending()
    {
        // app.1.log is held open without FileShare.Delete, so moving it to app.2.log fails.
        using var holder = new FileStream(_temp.Combine("app.1.log"), FileMode.Create, FileAccess.Write, FileShare.Read);
        using var log = new FileAppLog(_temp.Path, maxBytes: 200, TimeProvider.System);

        for (int i = 0; i < 40; i++)
        {
            log.Info($"message number {i:D3} with some padding to grow the file");
        }

        log.Flush();

        string text = File.ReadAllText(_temp.Combine("app.log"));
        Assert.Contains("message number 000", text);
        Assert.Contains("message number 039", text);
        Assert.Equal(40, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void PublicConstructor_UsesAppLogFileName()
    {
        using var log = new FileAppLog(_temp.Path);

        log.Info("hello");
        log.Flush();

        Assert.Equal(_temp.Combine("app.log"), log.FilePath);
        Assert.True(File.Exists(log.FilePath));
    }

    [Fact]
    public void ConcurrentWrites_FromSeveralThreads_AllLandAfterFlush()
    {
        using var log = new FileAppLog(_temp.Path);
        const int threads = 4;
        const int perThread = 250;

        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            for (int i = 0; i < perThread; i++)
            {
                log.Info($"t{t}-m{i:D3}");
            }
        });
        log.Flush();

        string[] lines = File.ReadAllLines(log.FilePath);
        Assert.Equal(threads * perThread, lines.Length);
        for (int t = 0; t < threads; t++)
        {
            // Each writer's own lines keep their order (single background writer, FIFO queue).
            int[] order = lines
                .Where(l => l.Contains($"[INFO] t{t}-m", StringComparison.Ordinal))
                .Select(l => int.Parse(l[^3..], System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            Assert.Equal(Enumerable.Range(0, perThread), order);
        }
    }

    [Fact]
    public void Dispose_FlushesPendingLines_AndLaterWritesAreIgnoredWithoutThrowing()
    {
        var log = new FileAppLog(_temp.Path);
        for (int i = 0; i < 100; i++)
        {
            log.Info($"before dispose {i:D3}");
        }

        log.Dispose();
        string afterDispose = File.ReadAllText(log.FilePath);

        log.Info("after dispose");
        log.Flush();
        log.Dispose();

        Assert.Contains("before dispose 099", afterDispose);
        Assert.Equal(afterDispose, File.ReadAllText(log.FilePath));
    }

    [Fact]
    public void NullAppLog_IgnoresEverything()
    {
        NullAppLog.Instance.Info("x");
        NullAppLog.Instance.Warn("x", new InvalidOperationException());
        NullAppLog.Instance.Error("x");
    }
}
