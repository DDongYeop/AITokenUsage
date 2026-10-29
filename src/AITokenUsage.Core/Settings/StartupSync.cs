namespace AITokenUsage.Core.Settings;

/// <summary>What the app does with the Windows "Run" registry value at startup.</summary>
public enum StartupSyncDecision
{
    /// <summary>Register or remove the Run value according to the stored setting (v1.0 behavior).</summary>
    ApplySetting,

    /// <summary>The installer already registered this exe: turn the setting on instead.</summary>
    AdoptRegistryEnabled,
}

/// <summary>Reconciles the Run registry value with the stored setting (spec §5.3).</summary>
public static class StartupSync
{
    /// <summary>
    /// <see cref="StartupSyncDecision.AdoptRegistryEnabled"/> only when the setting is off and the Run value
    /// (surrounding quotes removed) points at <paramref name="processPath"/>, compared case-insensitively.
    /// </summary>
    public static StartupSyncDecision Decide(string? runValue, string? processPath, bool settingEnabled)
    {
        if (settingEnabled || string.IsNullOrWhiteSpace(runValue) || string.IsNullOrWhiteSpace(processPath))
        {
            return StartupSyncDecision.ApplySetting;
        }

        string registered = runValue.Trim().Trim('"');
        return string.Equals(registered, processPath, StringComparison.OrdinalIgnoreCase)
            ? StartupSyncDecision.AdoptRegistryEnabled
            : StartupSyncDecision.ApplySetting;
    }
}
