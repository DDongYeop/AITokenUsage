using AITokenUsage.Core.Providers.Codex;
using AITokenUsage.Core.Tests.Fakes;
using Xunit;

namespace AITokenUsage.Core.Tests.Providers.Codex;

public class CodexRolloutReaderTests
{
    private const string DayDir = CodexTestData.SessionsDir + @"\2026\10\28";
    private static readonly DateTimeOffset Now = CodexTestData.Now;

    private static (FakeFileSystem Files, CodexRolloutReader Reader) Setup()
    {
        var files = new FakeFileSystem();
        files.AddDirectory(CodexTestData.SessionsDir);
        return (files, new CodexRolloutReader(files));
    }

    private static string RolloutPath(string stamp, string thread = "00000000-0000-4000-8000-000000000001")
        => $@"{DayDir}\rollout-{stamp}-{thread}.jsonl";

    [Fact]
    public void VariantA_FlatFields_NoResetInfo()
    {
        var (files, reader) = Setup();
        files.AddFile(RolloutPath("2026-10-28T10-00-00"), Fixture.Read("codex/rollout-a.jsonl"), CodexTestData.FileTime);

        var snapshot = reader.ReadLatest(CodexTestData.SessionsDir, Now);

        Assert.NotNull(snapshot);
        Assert.Equal(new DateTimeOffset(2025, 9, 20, 1, 2, 1, TimeSpan.Zero), snapshot.Timestamp);
        Assert.Null(snapshot.PlanType);
        Assert.Equal(new[] { "5h", "weekly" }, snapshot.Meters.Select(m => m.Key).ToArray());
        Assert.Equal(12, snapshot.Meters[0].UsedPercent);
        Assert.Equal(30, snapshot.Meters[1].UsedPercent);
        Assert.All(snapshot.Meters, m => Assert.Null(m.ResetsAt));
    }

    [Fact]
    public void VariantB_RelativeResets_AddedToLineTimestamp_AndNullRateLimitsSkipped()
    {
        var (files, reader) = Setup();
        files.AddFile(RolloutPath("2026-10-28T10-00-00"), Fixture.Read("codex/rollout-b.jsonl"), CodexTestData.FileTime);

        var snapshot = reader.ReadLatest(CodexTestData.SessionsDir, Now);

        Assert.NotNull(snapshot);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 2, 30, 0, TimeSpan.Zero), snapshot.Timestamp);
        Assert.Equal(25, snapshot.Meters[0].UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 5, 0, 0, TimeSpan.Zero), snapshot.Meters[0].ResetsAt);
        Assert.Equal("weekly", snapshot.Meters[1].Key);
        Assert.Equal(22, snapshot.Meters[1].UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 2, 30, 0, TimeSpan.Zero), snapshot.Meters[1].ResetsAt);
    }

    [Fact]
    public void VariantC_EpochResets_AndPlanType()
    {
        var (files, reader) = Setup();
        files.AddFile(RolloutPath("2026-10-28T08-00-00"), Fixture.Read("codex/rollout-c.jsonl"), CodexTestData.FileTime);
        var beforeFiveHourReset = new DateTimeOffset(2026, 10, 28, 1, 0, 0, TimeSpan.Zero);

        var snapshot = reader.ReadLatest(CodexTestData.SessionsDir, beforeFiveHourReset);

        Assert.NotNull(snapshot);
        Assert.Equal("plus", snapshot.PlanType);
        Assert.Equal(64, snapshot.Meters[0].UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793152800), snapshot.Meters[0].ResetsAt);
        Assert.Equal(41, snapshot.Meters[1].UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793415600), snapshot.Meters[1].ResetsAt);
    }

    [Fact]
    public void ResetAlreadyPassed_ShowsZeroPercent()
    {
        var (files, reader) = Setup();
        files.AddFile(RolloutPath("2026-10-28T08-00-00"), Fixture.Read("codex/rollout-c.jsonl"), CodexTestData.FileTime);

        var snapshot = reader.ReadLatest(CodexTestData.SessionsDir, Now); // 03:00Z, 5h reset was 02:00Z

        Assert.NotNull(snapshot);
        Assert.Equal("5h", snapshot.Meters[0].Key);
        Assert.Equal(0, snapshot.Meters[0].UsedPercent);
        Assert.Null(snapshot.Meters[0].ResetsAt);
        Assert.Equal(41, snapshot.Meters[1].UsedPercent);
    }

    [Fact]
    public void VariantD_SkipsOtherLimitIds()
    {
        var (files, reader) = Setup();
        files.AddFile(RolloutPath("2026-10-28T11-40-00"), Fixture.Read("codex/rollout-d.jsonl"), CodexTestData.FileTime);

        var snapshot = reader.ReadLatest(CodexTestData.SessionsDir, Now);

        Assert.NotNull(snapshot);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 2, 50, 0, TimeSpan.Zero), snapshot.Timestamp);
        Assert.Equal("pro", snapshot.PlanType);
        Assert.Equal(new[] { "5h", "weekly" }, snapshot.Meters.Select(m => m.Key).ToArray());
        Assert.Equal(42, snapshot.Meters[0].UsedPercent);
        Assert.Equal(5, snapshot.Meters[1].UsedPercent);
    }

    [Fact]
    public void MissingWindow_OnlyPresentWindowBecomesMeter()
    {
        var (files, reader) = Setup();
        const string line = """{"timestamp":"2026-10-28T02:58:00.000Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"limit_id":"codex","limit_name":null,"secondary":{"used_percent":9.0,"window_minutes":10080,"resets_at":1793415600},"credits":null,"plan_type":"plus"}}}""";
        files.AddFile(RolloutPath("2026-10-28T11-50-00"), line + "\n", CodexTestData.FileTime);

        var snapshot = reader.ReadLatest(CodexTestData.SessionsDir, Now);

        var meter = Assert.Single(snapshot!.Meters);
        Assert.Equal("weekly", meter.Key);
        Assert.Equal(9, meter.UsedPercent);
    }

    [Fact]
    public void PicksNewestFileByLastWriteTime_NotByName()
    {
        var (files, reader) = Setup();
        files.AddFile(RolloutPath("2026-10-28T23-59-59"), Fixture.Read("codex/rollout-c.jsonl"), new DateTime(2026, 10, 28, 1, 0, 0, DateTimeKind.Utc));
        files.AddFile(RolloutPath("2026-10-28T00-00-01"), Fixture.Read("codex/rollout-d.jsonl"), new DateTime(2026, 10, 28, 2, 59, 0, DateTimeKind.Utc));

        var snapshot = reader.ReadLatest(CodexTestData.SessionsDir, Now);

        Assert.Equal("pro", snapshot!.PlanType); // rollout-d content
    }

    [Fact]
    public void FallsThroughToOlderFile_WhenNewestHasNoUsableLine()
    {
        var (files, reader) = Setup();
        files.AddFile(RolloutPath("2026-10-28T08-00-00"), Fixture.Read("codex/rollout-c.jsonl"), new DateTime(2026, 10, 28, 1, 0, 0, DateTimeKind.Utc));
        const string noLimits = """{"timestamp":"2026-10-28T02:59:00.000Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":null}}""";
        files.AddFile(RolloutPath("2026-10-28T11-59-00"), noLimits + "\n" + "not json at all\n", new DateTime(2026, 10, 28, 2, 59, 0, DateTimeKind.Utc));

        var snapshot = reader.ReadLatest(CodexTestData.SessionsDir, Now);

        Assert.Equal("plus", snapshot!.PlanType); // rollout-c content
    }

    [Fact]
    public void IgnoresCompressedRollouts()
    {
        var (files, reader) = Setup();
        files.AddFile(RolloutPath("2026-10-28T08-00-00"), Fixture.Read("codex/rollout-c.jsonl"), new DateTime(2026, 10, 28, 1, 0, 0, DateTimeKind.Utc));
        files.AddFile(RolloutPath("2026-10-28T11-40-00") + ".zst", Fixture.Read("codex/rollout-d.jsonl"), new DateTime(2026, 10, 28, 2, 59, 0, DateTimeKind.Utc));

        var snapshot = reader.ReadLatest(CodexTestData.SessionsDir, Now);

        Assert.Equal("plus", snapshot!.PlanType);
    }

    [Fact]
    public void ReturnsNull_WhenSessionsDirectoryMissingOrEmpty()
    {
        var files = new FakeFileSystem();
        var reader = new CodexRolloutReader(files);
        Assert.Null(reader.ReadLatest(CodexTestData.SessionsDir, Now));

        files.AddDirectory(CodexTestData.SessionsDir);
        Assert.Null(reader.ReadLatest(CodexTestData.SessionsDir, Now));
    }

    [Theory]
    [InlineData("""{"timestamp":"2026-10-28T02:00:00.000Z","type":"response_item","payload":{"type":"token_count"}}""")]
    [InlineData("""{"timestamp":"2026-10-28T02:00:00.000Z","type":"event_msg","payload":{"type":"agent_message","message":"\"token_count\""}}""")]
    [InlineData("""{"type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"primary":{"used_percent":1.0,"window_minutes":300,"resets_at":1793163600}}}}""")]
    [InlineData("""{"timestamp":"2026-10-28T02:00:00.000Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"limit_id":"codex","primary":null,"secondary":null}}}""")]
    [InlineData("{\"timestamp\":\"2026-10-28T02:00:00.000Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\"")]
    public void ParseLine_RejectsUnusableLines(string line)
    {
        Assert.Null(CodexRolloutReader.ParseLine(line, Now));
    }
}
