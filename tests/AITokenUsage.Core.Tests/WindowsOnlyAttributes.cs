namespace AITokenUsage.Core.Tests;

/// <summary>
/// A test that depends on Windows behaviour: Windows path fixtures ("C:\..."), Win32 APIs (DPAPI, Credential Manager),
/// cmd.exe, PATHEXT or Windows file-sharing rules. Runs on Windows, skipped elsewhere (the macOS build runs the rest).
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only";
    }
}

/// <inheritdoc cref="WindowsFactAttribute"/>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only";
    }
}
