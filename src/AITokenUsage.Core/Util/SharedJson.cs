using System.Text.Json;

namespace AITokenUsage.Core.Util;

public static class SharedJson
{
    /// <summary>For JSONC files such as ~/.gemini/settings.json: comments are skipped and trailing commas allowed.</summary>
    public static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
