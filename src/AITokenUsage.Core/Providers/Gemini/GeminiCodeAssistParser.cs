using System.Text.Json;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Gemini;

public static class GeminiCodeAssistParser
{
    /// <summary>Parses a loadCodeAssist body. Throws JsonException when it is not a JSON object.</summary>
    public static GeminiCodeAssistInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("loadCodeAssist response is not a JSON object.");
        }

        var current = Json.Obj(root, "currentTier");
        var paid = Json.Obj(root, "paidTier");
        var tierId = Blank(paid is { } p ? Json.Str(p, "id") : null)
                     ?? Blank(current is { } c ? Json.Str(c, "id") : null);
        var paidName = Blank(paid is { } pn ? Json.Str(pn, "name") : null);

        return new GeminiCodeAssistInfo(current is not null, tierId, paidName, ReadProject(root), HasUnsupportedClient(root));
    }

    /// <summary>paidTier.name, else standard-tier → Standard, free-tier + hd → Workspace, free-tier → Free, legacy-tier → Legacy.</summary>
    public static string? PlanLabel(GeminiCodeAssistInfo info, bool hasHostedDomain)
    {
        if (!string.IsNullOrEmpty(info.PaidTierName))
        {
            return info.PaidTierName;
        }

        return info.TierId switch
        {
            "standard-tier" => "Standard",
            "free-tier" => hasHostedDomain ? "Workspace" : "Free",
            "legacy-tier" => "Legacy",
            _ => null,
        };
    }

    /// <summary>True when the id_token carries an "hd" (Workspace hosted domain) claim.</summary>
    public static bool HasHostedDomain(string? idToken) =>
        Jwt.TryReadPayload(idToken, out var payload)
        && payload.ValueKind == JsonValueKind.Object
        && !string.IsNullOrEmpty(Json.Str(payload, "hd"));

    /// <summary>Google error body: error.details[].reason == <paramref name="reason"/>.</summary>
    public static bool HasErrorReason(string body, string reason)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || Json.Obj(doc.RootElement, "error") is not { } error
                || Json.Arr(error, "details") is not { } details)
            {
                return false;
            }

            foreach (var detail in details.EnumerateArray())
            {
                if (detail.ValueKind == JsonValueKind.Object
                    && string.Equals(Json.Str(detail, "reason"), reason, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Text markers CodexBar uses to recognise the consumer shutdown in non-200 bodies.</summary>
    public static bool LooksLikeShutdown(string body)
    {
        var text = body.ToLowerInvariant();
        return text.Contains("unsupported_client")
               || text.Contains("ineligibletiererror")
               || (text.Contains("no longer supported") && text.Contains("gemini code assist"))
               || (text.Contains("migrate") && text.Contains("antigravity") && text.Contains("gemini"));
    }

    private static string? ReadProject(JsonElement root)
    {
        if (!root.TryGetProperty("cloudaicompanionProject", out var project))
        {
            return null;
        }

        return project.ValueKind switch
        {
            JsonValueKind.String => Blank(project.GetString()),
            JsonValueKind.Object => Blank(Json.Str(project, "id")) ?? Blank(Json.Str(project, "projectId")),
            _ => null,
        };
    }

    private static bool HasUnsupportedClient(JsonElement root)
    {
        if (Json.Arr(root, "ineligibleTiers") is not { } tiers)
        {
            return false;
        }

        foreach (var tier in tiers.EnumerateArray())
        {
            if (tier.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (MentionsUnsupportedClient(Json.Str(tier, "reasonCode"))
                || MentionsUnsupportedClient(Json.Str(tier, "reasonMessage")))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MentionsUnsupportedClient(string? text) =>
        text?.Contains("UNSUPPORTED_CLIENT", StringComparison.OrdinalIgnoreCase) == true;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
