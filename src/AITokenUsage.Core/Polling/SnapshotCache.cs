using System.Text.Json;
using System.Text.Json.Serialization;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Polling;

/// <summary>
/// Persists the last good (Ok) snapshot of each provider to cache.json.
/// Only display data is stored: never tokens, headers or raw API bodies.
/// Never throws: a missing or corrupt file loads as an empty cache, and write failures are logged.
/// Uses a source-generated serializer context, so it also works where reflection-based JSON is disabled (trim/AOT).
/// </summary>
public sealed partial class SnapshotCache
{
    public const int FormatVersion = 1;

    private readonly string _filePath;
    private readonly IAppLog _log;
    private readonly object _writeGate = new();

    public SnapshotCache(string filePath, IAppLog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(log);
        _filePath = filePath;
        _log = log;
    }

    public IReadOnlyDictionary<string, UsageSnapshot> Load()
    {
        var result = new Dictionary<string, UsageSnapshot>(StringComparer.Ordinal);
        CacheFileDto? file;
        try
        {
            if (!File.Exists(_filePath))
            {
                return result;
            }

            string json = File.ReadAllText(_filePath);
            file = JsonSerializer.Deserialize(json, CacheJsonContext.Default.CacheFileDto);
        }
        catch (JsonException ex)
        {
            // Log only the exception type: never echo file content.
            _log.Warn($"Snapshot cache is corrupt and was ignored ({ex.GetType().Name}).");
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.Warn("Snapshot cache could not be read.", ex);
            return result;
        }

        if (file is null || file.Version != FormatVersion || file.Providers is null)
        {
            return result;
        }

        foreach (var (providerId, dto) in file.Providers)
        {
            if (string.IsNullOrWhiteSpace(providerId) || dto is null)
            {
                continue;
            }

            result[providerId] = ToSnapshot(providerId, dto);
        }

        return result;
    }

    public void Save(IReadOnlyDictionary<string, UsageSnapshot> lastGood)
    {
        ArgumentNullException.ThrowIfNull(lastGood);

        lock (_writeGate)
        {
            string tempPath = _filePath + ".tmp";
            try
            {
                var file = new CacheFileDto
                {
                    Version = FormatVersion,
                    Providers = lastGood.ToDictionary(kv => kv.Key, kv => ToDto(kv.Value), StringComparer.Ordinal),
                };

                string? directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(tempPath, JsonSerializer.Serialize(file, CacheJsonContext.Default.CacheFileDto));
                File.Move(tempPath, _filePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _log.Warn("Snapshot cache could not be written.", ex);
            }
            catch (Exception ex)
            {
                // Save must never throw. Log only the exception type: the message could echo cached content.
                _log.Warn($"Snapshot cache could not be written ({ex.GetType().Name}).");
            }
        }
    }

    private static UsageSnapshot ToSnapshot(string providerId, CachedSnapshotDto dto)
    {
        var meters = new List<UsageMeter>();
        foreach (CachedMeterDto m in dto.Meters ?? [])
        {
            if (m is null || string.IsNullOrEmpty(m.Key))
            {
                continue;
            }

            meters.Add(new UsageMeter(
                m.Key,
                m.ShortLabel ?? "",
                m.LongLabel ?? "",
                m.UsedPercent,
                m.ResetsAt,
                ParseSeverity(m.ServerSeverity),
                m.TextOverride));
        }

        // Only Ok snapshots are ever cached, so the restored status is always Ok.
        return new UsageSnapshot(providerId, ProviderStatus.Ok, dto.PlanLabel, meters, dto.FetchedAt,
            dto.StatusMessage, RetryAfter: null, dto.ExtraLine);
    }

    private static CachedSnapshotDto ToDto(UsageSnapshot s) => new()
    {
        PlanLabel = s.PlanLabel,
        FetchedAt = s.FetchedAt,
        StatusMessage = s.StatusMessage,
        ExtraLine = s.ExtraLine,
        Meters = s.Meters.Select(m => new CachedMeterDto
        {
            Key = m.Key,
            ShortLabel = m.ShortLabel,
            LongLabel = m.LongLabel,
            UsedPercent = m.UsedPercent is { } percent && double.IsFinite(percent) ? percent : null, // NaN/Infinity are not valid JSON
            ResetsAt = m.ResetsAt,
            ServerSeverity = FormatSeverity(m.ServerSeverity),
            TextOverride = m.TextOverride,
        }).ToList(),
    };

    private static string? FormatSeverity(Severity? severity) => severity switch
    {
        Severity.Normal => "normal",
        Severity.Warning => "warning",
        Severity.Critical => "critical",
        _ => null,
    };

    // Unknown strings become null (the meter falls back to percent-based severity) instead of discarding the cache.
    private static Severity? ParseSeverity(string? value) => value switch
    {
        "normal" => Severity.Normal,
        "warning" => Severity.Warning,
        "critical" => Severity.Critical,
        _ => null,
    };

    private sealed class CacheFileDto
    {
        public int Version { get; set; }
        public Dictionary<string, CachedSnapshotDto>? Providers { get; set; }
    }

    private sealed class CachedSnapshotDto
    {
        public string? PlanLabel { get; set; }
        public DateTimeOffset FetchedAt { get; set; }
        public string? StatusMessage { get; set; }
        public string? ExtraLine { get; set; }
        public List<CachedMeterDto>? Meters { get; set; }
    }

    private sealed class CachedMeterDto
    {
        public string? Key { get; set; }
        public string? ShortLabel { get; set; }
        public string? LongLabel { get; set; }
        public double? UsedPercent { get; set; }
        public DateTimeOffset? ResetsAt { get; set; }
        public string? ServerSeverity { get; set; }
        public string? TextOverride { get; set; }
    }

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true)]
    [JsonSerializable(typeof(CacheFileDto))]
    private sealed partial class CacheJsonContext : JsonSerializerContext
    {
    }
}
