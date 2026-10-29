namespace AITokenUsage.UI.Settings;

/// <summary>A left-navigation entry of the settings window: Segoe Fluent Icons glyph, label (also the page title) and page factory.</summary>
public sealed record SettingsNavItem(string Glyph, string Label, Func<AppHost, SettingsPage> CreatePage);
