using AITokenUsage.Core.Settings;

namespace AITokenUsage.Core.Tests.Settings;

public class StartupSyncTests
{
    private const string ExePath = @"C:\A\AITokenUsage.exe";

    [Fact]
    public void Decide_QuotedValueMatchingProcessPath_AdoptsRegistry()
    {
        Assert.Equal(StartupSyncDecision.AdoptRegistryEnabled,
            StartupSync.Decide("\"C:\\A\\AITokenUsage.exe\"", ExePath, false));
    }

    [Fact]
    public void Decide_CaseOnlyDifference_AdoptsRegistry()
    {
        Assert.Equal(StartupSyncDecision.AdoptRegistryEnabled,
            StartupSync.Decide("\"c:\\a\\aitokenusage.EXE\"", ExePath, false));
    }

    [Fact]
    public void Decide_UnquotedValue_AdoptsRegistry()
    {
        Assert.Equal(StartupSyncDecision.AdoptRegistryEnabled, StartupSync.Decide(ExePath, ExePath, false));
    }

    [Fact]
    public void Decide_DifferentPath_AppliesSetting()
    {
        Assert.Equal(StartupSyncDecision.ApplySetting,
            StartupSync.Decide("\"C:\\B\\AITokenUsage.exe\"", ExePath, false));
    }

    [Fact]
    public void Decide_NullValue_AppliesSetting()
    {
        Assert.Equal(StartupSyncDecision.ApplySetting, StartupSync.Decide(null, ExePath, false));
    }

    [Fact]
    public void Decide_SettingAlreadyEnabled_AppliesSetting()
    {
        Assert.Equal(StartupSyncDecision.ApplySetting,
            StartupSync.Decide("\"C:\\A\\AITokenUsage.exe\"", ExePath, true));
    }

    [Fact]
    public void Decide_NullProcessPath_AppliesSetting()
    {
        Assert.Equal(StartupSyncDecision.ApplySetting, StartupSync.Decide("\"C:\\A\\AITokenUsage.exe\"", null, false));
    }
}
