namespace AITokenUsage.UI.Settings;

/// <summary>An item of a settings ComboBox: the stored value (enum, int or string) and its localized label.</summary>
public sealed record SettingsChoice(object Value, string Label);
