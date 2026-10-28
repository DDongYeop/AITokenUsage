using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Providers.Codex;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Codex;

public class CodexAppServerClientTests
{
    private const string NpmShim = @"C:\Users\test\AppData\Roaming\npm\codex.cmd";
    private const string InitializeOk = """{"id":1,"result":{"userAgent":"codex_cli_rs/0.142.4"}}""";

    private readonly FakeTimeProvider _time = new(CodexTestData.Now);
    private readonly FakeProcessRunner _processes = new();
    private readonly List<ProcessSpec> _started = new();

    private CodexAppServerClient CreateClient(FakeInteractiveProcess process)
    {
        _processes.OnInteractive(spec =>
        {
            _started.Add(spec);
            return process;
        });
        return new CodexAppServerClient(_processes, _time, new NullLog());
    }

    private static FakeInteractiveProcess ProcessWith(params string[] lines)
    {
        var process = new FakeInteractiveProcess();
        foreach (var line in lines) process.EnqueueOutput(line);
        return process;
    }

    private async Task<T> AdvanceUntilDone<T>(Task<T> task)
    {
        for (var i = 0; i < 60 && !task.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }
        return await task;
    }

    [Fact]
    public async Task ReadRateLimits_SkipsNotificationsAndNoise_AndMapsMeters()
    {
        var lines = Fixture.Read("codex/app-server-stdout.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r')).ToArray();
        var process = ProcessWith(lines);
        var client = CreateClient(process);

        var result = await client.ReadRateLimitsAsync(NpmShim, CancellationToken.None);

        Assert.Equal(CodexAppServerOutcome.Ok, result.Outcome);
        Assert.Equal("pro", result.PlanType);
        Assert.Equal(
            new[] { "5h", "weekly", "gpt_5_3_codex_spark:5h", "gpt_5_3_codex_spark:weekly" },
            result.Meters.Select(m => m.Key).ToArray());
        Assert.Equal(42, result.Meters[0].UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793163600), result.Meters[0].ResetsAt);
        Assert.Equal(5, result.Meters[1].UsedPercent);
        Assert.Equal("주·Sp", result.Meters[3].ShortLabel);
    }

    [Fact]
    public async Task ReadRateLimits_WritesNewlineDelimitedJsonRpc_WithoutJsonrpcField()
    {
        var lines = Fixture.Read("codex/app-server-stdout.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r')).ToArray();
        var process = ProcessWith(lines);
        var client = CreateClient(process);

        await client.ReadRateLimitsAsync(NpmShim, CancellationToken.None);

        Assert.Equal(3, process.Written.Count);
        using var initialize = JsonDocument.Parse(process.Written[0]);
        using var initialized = JsonDocument.Parse(process.Written[1]);
        using var read = JsonDocument.Parse(process.Written[2]);
        Assert.All(process.Written, line => Assert.DoesNotContain("jsonrpc", line));
        Assert.All(process.Written, line => Assert.DoesNotContain("\n", line));

        Assert.Equal(1, initialize.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("initialize", initialize.RootElement.GetProperty("method").GetString());
        Assert.Equal("ai-token-usage", initialize.RootElement.GetProperty("params").GetProperty("clientInfo").GetProperty("name").GetString());

        Assert.False(initialized.RootElement.TryGetProperty("id", out _));
        Assert.Equal("initialized", initialized.RootElement.GetProperty("method").GetString());

        Assert.Equal(2, read.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("account/rateLimits/read", read.RootElement.GetProperty("method").GetString());
        var parameters = read.RootElement.GetProperty("params");
        Assert.True(parameters.GetProperty("excludeResetCreditDetails").GetBoolean());
        Assert.False(parameters.TryGetProperty("supportsLunaReserve", out _));
    }

    [Theory]
    [InlineData(@"C:\Users\test\AppData\Roaming\npm\codex.cmd", "cmd.exe", "/d|/c|codex|-s|read-only|-a|never|app-server")]
    [InlineData(@"C:\Users\test\AppData\Roaming\npm\codex", "cmd.exe", "/d|/c|codex|-s|read-only|-a|never|app-server")]
    [InlineData(@"C:\Tools\codex\codex.exe", @"C:\Tools\codex\codex.exe", "-s|read-only|-a|never|app-server")]
    public void BuildSpec_UsesCmdForShims_AndExeDirectly(string codexPath, string fileName, string arguments)
    {
        var spec = CodexAppServerClient.BuildSpec(codexPath);

        Assert.Equal(fileName, spec.FileName);
        Assert.Equal(arguments.Split('|'), spec.Arguments);
        Assert.Null(spec.EnvironmentOverrides);
    }

    [Fact]
    public async Task ReadRateLimits_StartsTheResolvedSpec()
    {
        var client = CreateClient(ProcessWith(InitializeOk, """{"id":2,"result":{"rateLimits":null}}"""));

        await client.ReadRateLimitsAsync(NpmShim, CancellationToken.None);

        var spec = Assert.Single(_started);
        Assert.Equal("cmd.exe", spec.FileName);
    }

    [Fact]
    public async Task ReadRateLimits_AuthError_IsAuthRequired()
    {
        var client = CreateClient(ProcessWith(
            InitializeOk,
            """{"id":2,"error":{"code":-32600,"message":"codex account authentication required to read rate limits"}}"""));

        var result = await client.ReadRateLimitsAsync(NpmShim, CancellationToken.None);

        Assert.Equal(CodexAppServerOutcome.AuthRequired, result.Outcome);
        Assert.Empty(result.Meters);
    }

    [Fact]
    public async Task ReadRateLimits_OtherError_IsFailed()
    {
        var client = CreateClient(ProcessWith(
            InitializeOk,
            """{"id":2,"error":{"code":-32603,"message":"failed to fetch codex rate limits: upstream 502"}}"""));

        var result = await client.ReadRateLimitsAsync(NpmShim, CancellationToken.None);

        Assert.Equal(CodexAppServerOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task ReadRateLimits_InitializeError_IsFailed()
    {
        var client = CreateClient(ProcessWith("""{"id":1,"error":{"code":-32600,"message":"bad clientInfo"}}"""));

        var result = await client.ReadRateLimitsAsync(NpmShim, CancellationToken.None);

        Assert.Equal(CodexAppServerOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task ReadRateLimits_InitializeTimesOutAfterEightSeconds()
    {
        var client = CreateClient(ProcessWith("""{"method":"codex/event/started","params":{}}"""));
        var started = _time.GetUtcNow();

        var result = await AdvanceUntilDone(client.ReadRateLimitsAsync(NpmShim, CancellationToken.None));

        Assert.Equal(CodexAppServerOutcome.TimedOut, result.Outcome);
        Assert.True(_time.GetUtcNow() - started >= CodexAppServerClient.InitializeTimeout);
    }

    [Fact]
    public async Task ReadRateLimits_RequestTimesOutAfterThreeSeconds()
    {
        var client = CreateClient(ProcessWith(InitializeOk));
        var started = _time.GetUtcNow();

        var result = await AdvanceUntilDone(client.ReadRateLimitsAsync(NpmShim, CancellationToken.None));

        Assert.Equal(CodexAppServerOutcome.TimedOut, result.Outcome);
        var elapsed = _time.GetUtcNow() - started;
        Assert.True(elapsed >= CodexAppServerClient.RequestTimeout);
        Assert.True(elapsed < CodexAppServerClient.InitializeTimeout);
    }

    [Fact]
    public async Task ReadRateLimits_StartFailure_IsFailed()
    {
        _processes.OnInteractive(_ => throw new System.ComponentModel.Win32Exception(2));
        var client = new CodexAppServerClient(_processes, _time, new NullLog());

        var result = await client.ReadRateLimitsAsync(NpmShim, CancellationToken.None);

        Assert.Equal(CodexAppServerOutcome.Failed, result.Outcome);
    }

    [Theory]
    [InlineData("1793163600000")]   // milliseconds epoch
    [InlineData("1e30")]
    public void MapRateLimits_OutOfRangeResetsAt_KeepsMeterWithNullReset(string resetsAt)
    {
        using var document = JsonDocument.Parse(
            "{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":11,\"windowDurationMins\":300,\"resetsAt\":" + resetsAt + "},\"secondary\":null,\"planType\":\"plus\"}}");

        var result = CodexAppServerClient.MapRateLimits(document.RootElement);

        var meter = Assert.Single(result.Meters);
        Assert.Equal(11, meter.UsedPercent);
        Assert.Null(meter.ResetsAt);
    }

    [Fact]
    public void MapRateLimits_FallsBackToRateLimits_WhenByLimitIdMissing()
    {
        using var document = JsonDocument.Parse("""
            {"rateLimits":{"limitId":"codex","primary":{"usedPercent":11,"windowDurationMins":43200,"resetsAt":1795748400},"secondary":null,"planType":"free"},
             "rateLimitsByLimitId":null}
            """);

        var result = CodexAppServerClient.MapRateLimits(document.RootElement);

        Assert.Equal("free", result.PlanType);
        var meter = Assert.Single(result.Meters);
        Assert.Equal("monthly", meter.Key);
        Assert.Equal(11, meter.UsedPercent);
    }
}
