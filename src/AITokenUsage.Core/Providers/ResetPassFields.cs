using System.Text.Json;

namespace AITokenUsage.Core.Providers;

internal static class ResetPassFields
{
    // Voucher dates identify instants. Partial dates and local times require guessing a timezone.
    public static DateTimeOffset? Timestamp(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            !value.TryGetDateTimeOffset(out var timestamp))
            return null;
        var text = value.GetString()!;
        var time = text.IndexOf('T');
        if (time < 0) return null;
        return text.EndsWith('Z') || text.AsSpan(time).IndexOfAny('+', '-') >= 0 ? timestamp : null;
    }
}
