using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Resources;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Antigravity;

public sealed class AntigravityUsageProvider(ProviderContext ctx) : IUsageProvider
{
    // Earlier versions can interpret "/usage" as a model prompt in print mode.
    private static readonly Version MinimumVersion = new(1, 1, 11);

    public string Id => ProviderIds.Antigravity;
    public string DisplayName => "Antigravity";
    public TimeSpan MinInterval => TimeSpan.FromMinutes(2);
    public IReadOnlyList<string> WatchPaths => [];
    public IReadOnlyList<MeterOption> StaticMeterOptions =>
    [
        new("gemini-5h", $"Gemini · {Strings.CodexWindow5hLong}"),
        new("gemini-weekly", $"Gemini · {Strings.CodexWindowWeeklyLong}"),
    ];

    public async Task<UsageSnapshot> FetchAsync(CancellationToken ct)
    {
        string? cli = ctx.Processes.FindOnPath("agy");
        if (cli is null) return Status(ProviderStatus.NotInstalled);

        try
        {
            var version = await ctx.Processes.RunAsync(new ProcessSpec(cli, ["--version"],
                Timeout: TimeSpan.FromSeconds(5)), ct).ConfigureAwait(false);
            if (version.TimedOut || version.ExitCode != 0)
                return Status(ProviderStatus.Error, Strings.AntigravityQueryFailed);
            if (!Version.TryParse(version.StdOut.Trim().TrimStart('v'), out var installed) || installed < MinimumVersion)
                return Status(ProviderStatus.Unsupported, Strings.AntigravityUpdateCli);

            var result = await ctx.Processes.RunAsync(new ProcessSpec(cli, ["-p", "/usage", "--output-format", "json"]),
                ct).ConfigureAwait(false);
            if (result.TimedOut) return Status(ProviderStatus.Error, Strings.AntigravityQueryFailed);
            if (result.ExitCode != 0)
            {
                if (result.ExitCode == 2 && (result.StdErr.Contains("API key", StringComparison.OrdinalIgnoreCase)
                    || result.StdErr.Contains("GEMINI_API_KEY", StringComparison.Ordinal)))
                    return Status(ProviderStatus.Unsupported, Strings.AntigravityApiKey);
                if (result.StdErr.Contains("not authenticated", StringComparison.OrdinalIgnoreCase)
                    || result.StdErr.Contains("not logged in", StringComparison.OrdinalIgnoreCase))
                    return Status(ProviderStatus.SignedOut);
                return Status(ProviderStatus.Error, Strings.AntigravityQueryFailed);
            }

            var meters = ParseMeters(result.StdOut);
            if (meters.Count == 0) return Status(ProviderStatus.Error, Strings.StatusNoData);
            return new UsageSnapshot(Id, ProviderStatus.Ok, null, meters, ctx.Time.GetUtcNow());
        }
        catch (JsonException)
        {
            return Status(ProviderStatus.Error, Strings.MessageBadResponse);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3)
        {
            return Status(ProviderStatus.NotInstalled);
        }
        catch (Win32Exception)
        {
            return Status(ProviderStatus.Error, Strings.AntigravityQueryFailed);
        }
    }

    private UsageSnapshot Status(ProviderStatus status, string? message = null) =>
        UsageSnapshot.WithStatus(Id, status, ctx.Time.GetUtcNow(), message);

    private static List<UsageMeter> ParseMeters(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (Json.Str(doc.RootElement, "status") != "SUCCESS"
            || Json.Obj(doc.RootElement, "command") is not { } command || Json.Str(command, "name") != "usage"
            || Json.Obj(command, "data") is not { } data || Json.Arr(data, "groups") is not { } groups)
            throw new JsonException("Missing usage groups.");

        var meters = new List<UsageMeter>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups.EnumerateArray())
        {
            if (Json.Arr(group, "buckets") is not { } buckets) continue;
            string? groupName = Json.Str(group, "name");
            foreach (var bucket in buckets.EnumerateArray())
            {
                string? id = Json.Str(bucket, "id");
                string? name = Json.Str(bucket, "name");
                if (string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name)) id = $"{groupName}/{name}";
                if (string.IsNullOrWhiteSpace(id) || Json.Bool(bucket, "disabled") == true) continue;

                double? percent = null;
                string? remaining = null;
                if (Json.Num(bucket, "remaining_fraction") is double fraction && fraction is >= 0 and <= 1)
                    percent = Math.Round((1 - fraction) * 100, 2);
                else if (Json.Num(bucket, "remaining_amount") is double amount && amount >= 0)
                    remaining = string.Format(CultureInfo.CurrentCulture, Strings.AntigravityAmountRemaining, amount);
                else continue;
                if (!seen.Add(id)) continue;

                string? window = Json.Str(bucket, "window");
                var (shortLabel, label) = window switch
                {
                    "5h" => ("5h", Strings.CodexWindow5hLong),
                    "weekly" => (Strings.LabelWeekShort, Strings.CodexWindowWeeklyLong),
                    _ => (window ?? Strings.LabelPrimaryShort, name ?? id),
                };
                string longLabel = string.IsNullOrWhiteSpace(groupName) ? label : $"{groupName} · {label}";
                meters.Add(new UsageMeter(id, shortLabel, longLabel, percent, Json.Iso(bucket, "reset_time"),
                    TextOverride: remaining));
            }
        }
        return meters;
    }
}
