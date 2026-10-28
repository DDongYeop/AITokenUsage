using AITokenUsage.Core.Abstractions;

namespace AITokenUsage.Core.Tests.Abstractions;

public class RealProcessRunnerTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    private readonly RealProcessRunner _runner = new();

    [Fact]
    public async Task RunAsync_CapturesStdoutStderrAndExitCode()
    {
        var spec = new ProcessSpec(Cmd, ["/d", "/c", "echo out-line& echo err-line 1>&2& exit /b 3"]);

        ProcessResult result = await _runner.RunAsync(spec, CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("out-line", result.StdOut.Trim());
        Assert.Equal("err-line", result.StdErr.Trim());
    }

    [Fact]
    public async Task RunAsync_AppliesAndRemovesEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("AITU_TEST_REMOVE", "inherited");
        try
        {
            var spec = new ProcessSpec(Cmd, ["/d", "/c", "echo [%AITU_TEST_SET%][%AITU_TEST_REMOVE%]"],
                new Dictionary<string, string?> { ["AITU_TEST_SET"] = "value-1", ["AITU_TEST_REMOVE"] = null });

            ProcessResult result = await _runner.RunAsync(spec, CancellationToken.None);

            // cmd leaves an undefined %VAR% untouched.
            Assert.Equal("[value-1][%AITU_TEST_REMOVE%]", result.StdOut.Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("AITU_TEST_REMOVE", null);
        }
    }

    [Fact]
    public async Task RunAsync_KillsTreeAndReportsTimeout()
    {
        var spec = new ProcessSpec(Cmd, ["/d", "/c", "ping -n 30 127.0.0.1 >nul"], Timeout: TimeSpan.FromMilliseconds(500));
        var started = DateTime.UtcNow;

        ProcessResult result = await _runner.RunAsync(spec, CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task RunAsync_ThrowsWhenCallerCancels()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var spec = new ProcessSpec(Cmd, ["/d", "/c", "ping -n 30 127.0.0.1 >nul"]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _runner.RunAsync(spec, cts.Token));
    }

    [Fact]
    public async Task StartInteractive_ExchangesLines()
    {
        var spec = new ProcessSpec(PowerShell,
            ["-NoProfile", "-NonInteractive", "-Command", "$l = [Console]::In.ReadLine(); [Console]::Out.WriteLine('echo:' + $l)"],
            Timeout: TimeSpan.FromSeconds(30));

        await using IInteractiveProcess process = _runner.StartInteractive(spec);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await process.WriteLineAsync("""{"id":1,"method":"initialize"}""", cts.Token);
        string? reply = await process.ReadLineAsync(cts.Token);
        string? end = await process.ReadLineAsync(cts.Token);

        Assert.Equal("""echo:{"id":1,"method":"initialize"}""", reply);
        Assert.Null(end);
    }

    [Fact]
    public async Task StartInteractive_ReadLineHonoursCancellation()
    {
        var spec = new ProcessSpec(Cmd, ["/d", "/c", "ping -n 30 127.0.0.1 >nul"]);
        await using IInteractiveProcess process = _runner.StartInteractive(spec);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.ReadLineAsync(cts.Token));
    }

    [Fact]
    public void FindOnPath_ResolvesWithPathExt()
    {
        string? found = _runner.FindOnPath("cmd");

        Assert.NotNull(found);
        Assert.Equal("cmd.exe", Path.GetFileName(found), ignoreCase: true);
    }

    [Fact]
    public void FindOnPath_ReturnsNull_ForUnknownCommand()
    {
        Assert.Null(_runner.FindOnPath("aitokenusage-no-such-command-xyz"));
    }
}
