using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Polling;
using Xunit;

namespace AITokenUsage.Core.Tests.Polling;

public sealed class SnapshotCacheTests : IDisposable
{
    private static readonly DateTimeOffset FetchedAt = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AITokenUsage.Tests", Guid.NewGuid().ToString("N"));
    private readonly CapturingLog _log = new();

    private string CachePath => Path.Combine(_dir, "nested", "cache.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static UsageSnapshot ClaudeSnapshot() => new(
        "claude",
        ProviderStatus.Ok,
        "Team · Max 5x",
        [
            new UsageMeter("session", "5h", "현재 세션 (5시간)", 14.5, new DateTimeOffset(2026, 10, 28, 6, 50, 0, TimeSpan.Zero), Severity.Normal),
            new UsageMeter("weekly", "주", "주간 · 모든 모델", 50, new DateTimeOffset(2026, 10, 30, 5, 0, 0, TimeSpan.Zero), null),
            new UsageMeter("weekly:fable", "주·Fa", "주간 · Fable", 91, null, Severity.Critical, null),
        ],
        FetchedAt,
        "로그 기준 · 12:00",
        null,
        "추가 사용 $12.40 / $50.00");

    private static UsageSnapshot CopilotSnapshot() => new(
        "copilot",
        ProviderStatus.Ok,
        "Copilot Pro+",
        [new UsageMeter("reset", "↻", "리셋", null, null, null, "↻ 11/1")],
        FetchedAt);

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var cache = new SnapshotCache(CachePath, _log);

        Assert.Empty(cache.Load());
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        var cache = new SnapshotCache(CachePath, _log);
        var claude = ClaudeSnapshot();
        var copilot = CopilotSnapshot();

        cache.Save(new Dictionary<string, UsageSnapshot> { ["claude"] = claude, ["copilot"] = copilot });
        var loaded = new SnapshotCache(CachePath, _log).Load();

        Assert.Equal(2, loaded.Count);
        var c = loaded["claude"];
        Assert.Equal("claude", c.ProviderId);
        Assert.Equal(ProviderStatus.Ok, c.Status);
        Assert.Equal("Team · Max 5x", c.PlanLabel);
        Assert.Equal(FetchedAt, c.FetchedAt);
        Assert.Equal("로그 기준 · 12:00", c.StatusMessage);
        Assert.Equal("추가 사용 $12.40 / $50.00", c.ExtraLine);
        Assert.Null(c.RetryAfter);
        Assert.Equal(claude.Meters, c.Meters);
        Assert.Equal("↻ 11/1", Assert.Single(loaded["copilot"].Meters).TextOverride);
    }

    [Fact]
    public void Save_WritesDocumentedJsonShape()
    {
        var cache = new SnapshotCache(CachePath, _log);

        cache.Save(new Dictionary<string, UsageSnapshot> { ["claude"] = ClaudeSnapshot() });

        using var doc = JsonDocument.Parse(File.ReadAllText(CachePath));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        var claude = root.GetProperty("providers").GetProperty("claude");
        Assert.Equal("Team · Max 5x", claude.GetProperty("planLabel").GetString());
        Assert.Equal(FetchedAt, claude.GetProperty("fetchedAt").GetDateTimeOffset());
        var meter = claude.GetProperty("meters")[0];
        Assert.Equal("session", meter.GetProperty("key").GetString());
        Assert.Equal("5h", meter.GetProperty("shortLabel").GetString());
        Assert.Equal(14.5, meter.GetProperty("usedPercent").GetDouble());
        Assert.Equal("normal", meter.GetProperty("serverSeverity").GetString());
        Assert.False(claude.TryGetProperty("status", out _));
        Assert.False(File.Exists(CachePath + ".tmp"));
    }

    [Fact]
    public void Save_OverwritesPreviousContent()
    {
        var cache = new SnapshotCache(CachePath, _log);
        cache.Save(new Dictionary<string, UsageSnapshot> { ["claude"] = ClaudeSnapshot() });

        cache.Save(new Dictionary<string, UsageSnapshot> { ["copilot"] = CopilotSnapshot() });

        var loaded = cache.Load();
        Assert.Equal("copilot", Assert.Single(loaded).Key);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"version\":\"one-secret-looking-value\"}")]
    [InlineData("{\"version\":1,\"providers\":{\"claude\":{\"fetchedAt\":\"not a date\"}}}")]
    public void Load_CorruptFile_ReturnsEmptyAndWarnsWithoutContent(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        File.WriteAllText(CachePath, content);

        var loaded = new SnapshotCache(CachePath, _log).Load();

        Assert.Empty(loaded);
        string warning = Assert.Single(_log.Warnings);
        Assert.DoesNotContain("one-secret-looking-value", warning);
        Assert.DoesNotContain("not a date", warning);
    }

    [Fact]
    public void Load_UnknownSeverity_KeepsMeterWithNullSeverity()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        File.WriteAllText(CachePath,
            "{\"version\":1,\"providers\":{\"claude\":{\"fetchedAt\":\"2026-10-28T03:00:00+00:00\",\"meters\":[{\"key\":\"session\",\"serverSeverity\":\"bogus\"},{\"key\":\"weekly\",\"serverSeverity\":\"warning\"}]}}}");

        var meters = new SnapshotCache(CachePath, _log).Load()["claude"].Meters;

        Assert.Equal(2, meters.Count);
        Assert.Null(meters[0].ServerSeverity);
        Assert.Equal(Severity.Warning, meters[1].ServerSeverity);
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Load_UnknownVersion_ReturnsEmpty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        File.WriteAllText(CachePath, "{\"version\":99,\"providers\":{\"claude\":{\"fetchedAt\":\"2026-10-28T03:00:00+00:00\",\"meters\":[]}}}");

        Assert.Empty(new SnapshotCache(CachePath, _log).Load());
    }

    [Fact]
    public void Load_SkipsNullEntriesAndMetersWithoutKey()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        File.WriteAllText(CachePath, """
            {
              "version": 1,
              "providers": {
                "claude": {
                  "planLabel": "Pro",
                  "fetchedAt": "2026-10-28T03:00:00+00:00",
                  "meters": [
                    null,
                    { "shortLabel": "x" },
                    { "key": "weekly", "shortLabel": "주", "longLabel": "주간 · 모든 모델", "usedPercent": 50, "futureField": true }
                  ]
                },
                "codex": null
              },
              "unknownTopLevel": 1
            }
            """);

        var loaded = new SnapshotCache(CachePath, _log).Load();

        var claude = Assert.Single(loaded).Value;
        var meter = Assert.Single(claude.Meters);
        Assert.Equal("weekly", meter.Key);
        Assert.Equal(50, meter.UsedPercent);
        Assert.Null(meter.ResetsAt);
    }

    [Fact]
    public void Save_NonFiniteUsedPercent_IsStoredAsNullAndDoesNotThrow()
    {
        var cache = new SnapshotCache(CachePath, _log);
        var snapshot = new UsageSnapshot("claude", ProviderStatus.Ok, "Pro",
            [
                new UsageMeter("nan", "a", "A", double.NaN, null),
                new UsageMeter("posinf", "b", "B", double.PositiveInfinity, null),
                new UsageMeter("neginf", "c", "C", double.NegativeInfinity, null),
                new UsageMeter("ok", "d", "D", 42, null),
            ],
            FetchedAt);

        cache.Save(new Dictionary<string, UsageSnapshot> { ["claude"] = snapshot });

        Assert.Empty(_log.Warnings);
        var meters = cache.Load()["claude"].Meters;
        Assert.Equal(4, meters.Count);
        Assert.Null(meters[0].UsedPercent);
        Assert.Null(meters[1].UsedPercent);
        Assert.Null(meters[2].UsedPercent);
        Assert.Equal(42, meters[3].UsedPercent);
        Assert.False(File.Exists(CachePath + ".tmp"));
    }

    [Fact]
    public void Save_UnwritableLocation_DoesNotThrowAndWarns()
    {
        Directory.CreateDirectory(_dir);
        string blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "file, not a directory");
        var cache = new SnapshotCache(Path.Combine(blocker, "cache.json"), _log);

        cache.Save(new Dictionary<string, UsageSnapshot> { ["claude"] = ClaudeSnapshot() });

        Assert.Single(_log.Warnings);
    }

    private sealed class CapturingLog : IAppLog
    {
        public List<string> Warnings { get; } = [];

        public void Info(string message) { }

        public void Warn(string message, Exception? ex = null) =>
            Warnings.Add(ex is null ? message : message + " " + ex.Message);

        public void Error(string message, Exception? ex = null) =>
            Warnings.Add(ex is null ? message : message + " " + ex.Message);
    }
}
