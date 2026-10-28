using System.Text.Json;

namespace AITokenUsage.Core.Util;

/// <summary>Reads JWT claims without verifying the signature (only used for expiry and account hints).</summary>
public static class Jwt
{
    /// <summary>Decodes the base64url payload segment. Returns false unless it is a JSON object.</summary>
    public static bool TryReadPayload(string? jwt, out JsonElement payload)
    {
        payload = default;
        if (string.IsNullOrWhiteSpace(jwt))
        {
            return false;
        }

        string[] parts = jwt.Split('.');
        if (parts.Length < 2 || parts[1].Length == 0 || !TryDecodeBase64Url(parts[1], out byte[] bytes))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            payload = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The "exp" claim (Unix seconds), or null.</summary>
    public static DateTimeOffset? GetExpiry(string? jwt) =>
        TryReadPayload(jwt, out JsonElement payload) ? Json.EpochSeconds(payload, "exp") : null;

    private static bool TryDecodeBase64Url(string segment, out byte[] bytes)
    {
        string base64 = segment.Replace('-', '+').Replace('_', '/');
        switch (base64.Length % 4)
        {
            case 1:
                bytes = [];
                return false;
            case 2:
                base64 += "==";
                break;
            case 3:
                base64 += "=";
                break;
        }

        try
        {
            bytes = Convert.FromBase64String(base64);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
