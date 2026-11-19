using System.Globalization;

namespace AITokenUsage.Core.Settings;

public sealed class ProviderSettings
{
    public const int MaxNicknameLength = 32;

    /// <summary>Optional single-line display name for this exact provider or account ID.</summary>
    public string? Nickname
    {
        get;
        set => field = NormalizeNickname(value);
    }

    private static string? NormalizeNickname(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string name = value.ReplaceLineEndings(" ").Replace('\t', ' ').Trim();
        if (name.Length <= MaxNicknameLength)
        {
            return name;
        }

        // Keep complete text elements when cutting a pasted or hand-edited name to the UI limit.
        int end = 0;
        while (end < name.Length)
        {
            int next = end + StringInfo.GetNextTextElementLength(name.AsSpan(end));
            if (next > MaxNicknameLength)
            {
                break;
            }

            end = next;
        }

        return end == 0 ? null : name[..end].TrimEnd();
    }

    /// <summary>Built-in provider ID or full extra account ID (lower case).</summary>
    public string Id { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Meter key shown on the first widget line, e.g. "session".</summary>
    public string Line1Key { get; set; } = "";

    /// <summary>Meter key shown on the second widget line, e.g. "weekly".</summary>
    public string Line2Key { get; set; } = "";
}
