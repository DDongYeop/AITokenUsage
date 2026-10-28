using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Codex;

/// <summary>The newest usable token_count rate_limits found in the rollout logs.</summary>
public sealed record CodexRolloutSnapshot(DateTimeOffset Timestamp, string? PlanType, IReadOnlyList<UsageMeter> Meters);

/// <summary>
/// Offline fallback: scans %CODEX_HOME%\sessions\**\rollout-*.jsonl (newest LastWriteTimeUtc first,
/// lines from the end) for the last token_count event whose limit_id is "codex" or absent.
/// Supports rate_limits variants A (v0.40), B (v0.41-0.47), C (v0.48+), D (current). Ignores *.jsonl.zst.
/// </summary>
public sealed class CodexRolloutReader
{
    public const int MaxFilesToScan = 10;

    private readonly IFileSystem _files;

    public CodexRolloutReader(IFileSystem files)
    {
        _files = files;
    }

    public CodexRolloutSnapshot? ReadLatest(string sessionsDirectory, DateTimeOffset now)
    {
        List<string> candidates;
        try
        {
            if (!_files.DirectoryExists(sessionsDirectory)) return null;
            candidates = _files.EnumerateFiles(sessionsDirectory, "*", recursive: true)
                .Where(IsPlainRollout)
                .Select(path => (FilePath: path, LastWrite: _files.GetLastWriteTimeUtc(path)))
                .OrderByDescending(file => file.LastWrite)
                .Take(MaxFilesToScan)
                .Select(file => file.FilePath)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var path in candidates)
        {
            try
            {
                foreach (var line in _files.ReadLinesReverse(path))
                {
                    var snapshot = ParseLine(line, now);
                    if (snapshot is not null) return snapshot;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // File vanished or is locked; try the next older one.
            }
        }
        return null;
    }

    /// <summary>Returns a snapshot for a usable codex token_count line, otherwise null.</summary>
    public static CodexRolloutSnapshot? ParseLine(string line, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.Contains("\"token_count\"", StringComparison.Ordinal))
            return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Json.Str(root, "type") != "event_msg") return null;
            if (Json.Obj(root, "payload") is not { } payload || Json.Str(payload, "type") != "token_count") return null;
            if (Json.Obj(payload, "rate_limits") is not { } limits) return null;

            var limitId = Json.Str(limits, "limit_id");
            if (limitId is not null && limitId != "codex") return null;

            if (CodexAuthReader.ParseTimestamp(Json.Str(root, "timestamp")) is not { } timestamp) return null;

            CodexWindow? primary;
            CodexWindow? secondary;
            if (Json.Num(limits, "primary_used_percent") is not null || Json.Num(limits, "secondary_used_percent") is not null)
            {
                // Variant A: flat fields, no reset information.
                primary = FlatWindow(limits, "primary");
                secondary = FlatWindow(limits, "secondary");
            }
            else
            {
                // Variants B, C, D: nested windows.
                primary = NestedWindow(Json.Obj(limits, "primary"), timestamp, now);
                secondary = NestedWindow(Json.Obj(limits, "secondary"), timestamp, now);
            }

            var meters = CodexWindowLabeler.ToMeters(primary, secondary);
            if (meters.Count == 0) return null;
            var planType = Json.Str(limits, "plan_type");
            return new CodexRolloutSnapshot(timestamp, string.IsNullOrWhiteSpace(planType) ? null : planType, meters);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsPlainRollout(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
    }

    private static CodexWindow? FlatWindow(JsonElement limits, string prefix)
    {
        var used = Json.Num(limits, prefix + "_used_percent");
        if (used is null) return null;
        return new CodexWindow(used, ToMinutes(Json.Num(limits, prefix + "_window_minutes")), null);
    }

    private static CodexWindow? NestedWindow(JsonElement? element, DateTimeOffset eventTime, DateTimeOffset now)
    {
        if (element is not { } window) return null;
        var used = Json.Num(window, "used_percent");
        if (used is null) return null;
        var minutes = ToMinutes(Json.Num(window, "window_minutes"));

        DateTimeOffset? resetsAt = null;
        if (Json.Num(window, "resets_at") is double epoch && epoch > 0)
            resetsAt = DateTimeOffset.FromUnixTimeSeconds((long)epoch);                     // C, D
        else if (Json.Num(window, "resets_in_seconds") is double relative && relative >= 0)
            resetsAt = eventTime.AddSeconds(relative);                                     // B

        if (resetsAt is { } reset && reset <= now)
            return new CodexWindow(0, minutes, null); // the window reset since the log line was written
        return new CodexWindow(used, minutes, resetsAt);
    }

    private static long? ToMinutes(double? minutes)
        => minutes is double value && value > 0 ? (long)Math.Round(value) : null;
}
