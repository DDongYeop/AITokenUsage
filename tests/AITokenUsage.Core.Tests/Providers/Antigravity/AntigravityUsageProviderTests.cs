using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers.Antigravity;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace AITokenUsage.Core.Tests.Providers.Antigravity;

public sealed class AntigravityUsageProviderTests
{
    private readonly FakeProcessRunner _runner = new();

    private Task<UsageSnapshot> Fetch() =>
        new AntigravityUsageProvider(TestContexts.Create(new StubHttpHandler(), new FakeTimeProvider(), proc: _runner))
            .FetchAsync(CancellationToken.None);

    private void SetCli(ProcessResult usage, string version = "1.1.11") =>
        _runner.SetPath("agy", "/bin/agy").OnRun(spec => spec.Arguments.SequenceEqual(["--version"])
            ? new ProcessResult(0, version, "", false) : usage);

    [Fact]
    public async Task Uses_read_only_command_and_reports_each_available_bucket()
    {
        const string json = """
            {"status":"SUCCESS","command":{"name":"usage","data":{"groups":[
              {"name":"Gemini Models","buckets":[
                {"id":"gemini-weekly","window":"weekly","remaining_fraction":0,"reset_time":"2026-12-01T10:00:00Z"},
                {"id":"gemini-5h","window":"5h","remaining_fraction":0.75},
                {"id":"disabled","window":"5h","disabled":true,"remaining_fraction":0},
                {"id":"unknown","window":"5h"},{"id":"invalid","remaining_fraction":1.5}]},
              {"name":"Other models","buckets":[
                {"id":"other-5h","window":"5h","remaining_fraction":1},
                {"id":"gemini-5h","window":"5h","remaining_fraction":0}]}
            ]}}}
            """;
        SetCli(new ProcessResult(0, json, "", false));
        var snapshot = await Fetch();
        Assert.Equal(ProviderStatus.Ok, snapshot.Status);
        Assert.Equal(["-p", "/usage", "--output-format", "json"], _runner.Calls[1].Arguments.ToArray());
        Assert.Equal(["gemini-weekly", "gemini-5h", "other-5h"], snapshot.Meters.Select(m => m.Key).ToArray());
        Assert.Equal(new double?[] { 100, 25, 0 }, snapshot.Meters.Select(m => m.UsedPercent));
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero), snapshot.Meters[0].ResetsAt);
        Assert.Contains("Other models", snapshot.Meters[2].LongLabel);
    }

    [Fact]
    public async Task Amount_only_bucket_keeps_remaining_count_without_inventing_a_percentage()
    {
        const string json = """
            {"status":"SUCCESS","command":{"name":"usage","data":{"groups":[
              {"name":"Models","buckets":[{"name":"Allowance","remaining_amount":12}]}
            ]}}}
            """;
        SetCli(new ProcessResult(0, json, "", false));
        var meter = Assert.Single((await Fetch()).Meters);
        Assert.Equal("Models/Allowance", meter.Key);
        Assert.Null(meter.UsedPercent);
        Assert.Contains("12", meter.TextOverride);
    }

    [Fact]
    public async Task Missing_cli_does_not_launch_a_process()
    {
        Assert.Equal(ProviderStatus.NotInstalled, (await Fetch()).Status);
        Assert.Empty(_runner.Calls);
    }

    [Theory]
    [InlineData("1.1.10")]
    [InlineData("unknown")]
    public async Task Never_sends_usage_to_an_old_or_unknown_cli(string version)
    {
        SetCli(new ProcessResult(0, "", "", false), version);
        Assert.Equal(ProviderStatus.Unsupported, (await Fetch()).Status);
        Assert.Equal(["--version"], Assert.Single(_runner.Calls).Arguments.ToArray());
    }

    [Theory]
    [InlineData(0, "{", "", false, ProviderStatus.Error)]
    [InlineData(0, "{}", "", false, ProviderStatus.Error)]
    [InlineData(0, """{"status":"SUCCESS","command":{"name":"usage","data":{"groups":[]}}}""", "", false, ProviderStatus.Error)]
    [InlineData(-1, "", "", true, ProviderStatus.Error)]
    [InlineData(1, "", "not authenticated", false, ProviderStatus.SignedOut)]
    [InlineData(2, "", "no quota for API keys", false, ProviderStatus.Unsupported)]
    [InlineData(1, "", "private diagnostic", false, ProviderStatus.Error)]
    public async Task Failures_do_not_publish_quota_or_raw_diagnostics(int code, string output,
        string error, bool timedOut, ProviderStatus status)
    {
        SetCli(new ProcessResult(code, output, error, timedOut));
        var snapshot = await Fetch();
        Assert.Equal(status, snapshot.Status);
        Assert.Empty(snapshot.Meters);
        Assert.DoesNotContain("private diagnostic", snapshot.StatusMessage ?? "");
    }
}
