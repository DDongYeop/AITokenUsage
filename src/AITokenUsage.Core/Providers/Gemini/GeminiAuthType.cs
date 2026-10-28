using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Gemini;

/// <summary>Reads the auth type selected in the Gemini CLI settings.json (JSONC).</summary>
public static class GeminiAuthType
{
    private static readonly HashSet<string> UnsupportedTypes =
        new(StringComparer.OrdinalIgnoreCase) { "gemini-api-key", "api-key", "vertex-ai", "gateway" };

    /// <summary>security.auth.selectedType, else legacy top-level selectedAuthType. Missing/broken file → null.</summary>
    public static string? Read(IFileSystem files, string settingsPath)
    {
        string? text;
        try
        {
            text = files.ReadAllTextShared(settingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text, SharedJson.Lenient);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (Json.Obj(root, "security") is { } security
                && Json.Obj(security, "auth") is { } auth
                && Json.Str(auth, "selectedType") is { Length: > 0 } selected)
            {
                return selected;
            }

            return Json.Str(root, "selectedAuthType");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool IsUnsupported(string? authType) => authType is not null && UnsupportedTypes.Contains(authType);
}
