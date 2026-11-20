using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Settings;

public static class ProviderDefaults
{
    public static readonly int[] RefreshChoicesMinutes = [2, 5, 10, 15];

    /// <summary>All providers enabled, in canonical order, with their default lines. Returns a new list every call.</summary>
    public static List<ProviderSettings> CreateDefaultProviders() =>
        ProviderIds.All.Select(CreateDefault).ToList();

    /// <summary>Initial widget lines for each provider. Account ids ("claude#k3x9q2") use their kind's lines.</summary>
    public static (string Line1, string Line2) DefaultLines(string providerId) => ProviderIds.KindOf(providerId) switch
    {
        ProviderIds.Claude => ("session", "weekly"),
        ProviderIds.Codex => ("5h", "weekly"),
        ProviderIds.Gemini => ("pro", "flash"),
        ProviderIds.Copilot => ("monthly", "reset"),
        ProviderIds.Antigravity => ("gemini-5h", "gemini-weekly"),
        _ => throw new ArgumentOutOfRangeException(nameof(providerId), providerId, "Unknown provider id."),
    };

    public static ProviderSettings CreateDefault(string providerId)
    {
        (string line1, string line2) = DefaultLines(providerId);
        return new ProviderSettings { Id = providerId, Enabled = true, Line1Key = line1, Line2Key = line2 };
    }
}
