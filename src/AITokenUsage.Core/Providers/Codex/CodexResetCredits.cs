using System.Text.Json;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Codex;

internal static class CodexResetCredits
{
    // Backend GET uses RFC 3339 strings. App-server converts those to Unix seconds.
    public static ResetPassBalance? Read(JsonElement? summary, DateTimeOffset now, bool appServer = false)
    {
        if (summary is not { ValueKind: JsonValueKind.Object } obj ||
            !obj.TryGetProperty(appServer ? "availableCount" : "available_count", out var count) ||
            count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out int available) || available < 0)
            return null;

        var passes = new List<ResetPass>();
        if (available > 0 && Json.Arr(obj, "credits") is { } credits)
        {
            string expiryName = appServer ? "expiresAt" : "expires_at";
            foreach (var credit in credits.EnumerateArray())
            {
                if (Json.Str(credit, "status") != "available") continue;
                bool hasExpiry = credit.TryGetProperty(expiryName, out var expiry);
                bool noExpiry = hasExpiry && expiry.ValueKind == JsonValueKind.Null;
                DateTimeOffset? expiresAt = appServer
                    ? UnixExpiry(expiry)
                    : ResetPassFields.Timestamp(credit, expiryName);
                if (expiresAt <= now || (hasExpiry && !noExpiry && expiresAt is null)) continue;

                passes.Add(new ResetPass(Json.Str(credit, "title"), expiresAt, noExpiry));
                if (passes.Count == available) break;
            }
        }
        // The service can cap detail rows. Only the service count represents the whole inventory.
        return new ResetPassBalance(available, passes);
    }

    private static DateTimeOffset? UnixExpiry(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds) &&
        seconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds() && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
}
