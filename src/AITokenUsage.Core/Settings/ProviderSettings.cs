namespace AITokenUsage.Core.Settings;

public sealed class ProviderSettings
{
    /// <summary>One of ProviderIds (lower case).</summary>
    public string Id { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Meter key shown on the first widget line, e.g. "session".</summary>
    public string Line1Key { get; set; } = "";

    /// <summary>Meter key shown on the second widget line, e.g. "weekly".</summary>
    public string Line2Key { get; set; } = "";
}
