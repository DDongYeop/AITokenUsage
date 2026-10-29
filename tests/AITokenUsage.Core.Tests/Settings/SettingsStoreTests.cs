using AITokenUsage.Core.Settings;
using AITokenUsage.Core.Tests.Fakes;

namespace AITokenUsage.Core.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLog _log = new();
    private readonly string _path;
    private readonly SettingsStore _store;

    public SettingsStoreTests()
    {
        _path = _temp.Combine("AITokenUsage", "settings.json");
        _store = new SettingsStore(_path, _log);
    }

    public void Dispose() => _temp.Dispose();

    private void WriteRaw(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, json);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults_WithoutCreatingIt()
    {
        AppSettings settings = _store.Load();

        Assert.Equal(["claude", "codex", "gemini", "copilot"], settings.Providers.Select(p => p.Id));
        Assert.Equal(5, settings.RotationSeconds);
        Assert.False(File.Exists(_path));
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEverySetting()
    {
        var original = new AppSettings
        {
            Providers =
            [
                new ProviderSettings { Id = "copilot", Enabled = true, Line1Key = "monthly", Line2Key = "completions" },
                new ProviderSettings { Id = "claude", Enabled = false, Line1Key = "weekly:fable", Line2Key = "session" },
                new ProviderSettings { Id = "gemini", Enabled = true, Line1Key = "flash-lite", Line2Key = "pro" },
                new ProviderSettings { Id = "codex", Enabled = true, Line1Key = "daily", Line2Key = "weekly" },
            ],
            RotationEnabled = false,
            RotationSeconds = 12,
            PauseOnHover = false,
            SkipUnavailable = false,
            MonitorDeviceName = @"\\.\DISPLAY2",
            Placement = WidgetPlacement.BeforeTray,
            ValueMode = ValueMode.Remaining,
            TextColor = TextColorMode.Light,
            RefreshMinutes = 10,
            StartWithWindows = true,
            ShowTrayIcon = false,
        };

        _store.Save(original);
        AppSettings loaded = new SettingsStore(_path, _log).Load();

        Assert.Equal(
            original.Providers.Select(p => (p.Id, p.Enabled, p.Line1Key, p.Line2Key)),
            loaded.Providers.Select(p => (p.Id, p.Enabled, p.Line1Key, p.Line2Key)));
        Assert.False(loaded.RotationEnabled);
        Assert.Equal(12, loaded.RotationSeconds);
        Assert.False(loaded.PauseOnHover);
        Assert.False(loaded.SkipUnavailable);
        Assert.Equal(@"\\.\DISPLAY2", loaded.MonitorDeviceName);
        Assert.Equal(WidgetPlacement.BeforeTray, loaded.Placement);
        Assert.Equal(ValueMode.Remaining, loaded.ValueMode);
        Assert.Equal(TextColorMode.Light, loaded.TextColor);
        Assert.Equal(10, loaded.RefreshMinutes);
        Assert.True(loaded.StartWithWindows);
        Assert.False(loaded.ShowTrayIcon);
    }

    [Fact]
    public void Save_WritesCamelCaseJsonWithStringEnums_AndLeavesNoTempFile()
    {
        _store.Save(new AppSettings { Placement = WidgetPlacement.BeforeStart, TextColor = TextColorMode.Dark });

        string json = File.ReadAllText(_path);
        Assert.Contains("\"version\": 1", json);
        Assert.Contains("\"placement\": \"beforeStart\"", json);
        Assert.Contains("\"textColor\": \"dark\"", json);
        Assert.Contains("\"line1Key\": \"session\"", json);
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public void Save_OverwritesExistingFile()
    {
        _store.Save(new AppSettings { RotationSeconds = 7 });
        _store.Save(new AppSettings { RotationSeconds = 9 });

        Assert.Equal(9, _store.Load().RotationSeconds);
        Assert.Equal(["settings.json"], Directory.GetFiles(Path.GetDirectoryName(_path)!).Select(f => Path.GetFileName(f)));
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"placement\": \"middle\"}")]
    public void Load_CorruptFile_BacksItUpAndReturnsDefaults(string corrupt)
    {
        WriteRaw(corrupt);

        AppSettings settings = _store.Load();

        Assert.Equal(5, settings.RotationSeconds);
        Assert.Equal(4, settings.Providers.Count);
        Assert.False(File.Exists(_path));
        Assert.Equal(corrupt, File.ReadAllText(_path + ".bak"));
        Assert.Equal(_path + ".bak", _store.BackupPath);
        Assert.Contains(_log.Entries, e => e.Level == "WARN" && e.Message.Contains("corrupt"));
    }

    [Fact]
    public void Load_IgnoresUnknownFields_AndKeepsDefaultsForMissingOnes()
    {
        WriteRaw("""
            {
              "version": 1,
              "futureFeature": { "enabled": true },
              "rotationSeconds": 20,
              "providers": [ { "id": "gemini", "enabled": false, "line1Key": "pro", "line2Key": "flash", "color": "red" } ]
            }
            """);

        AppSettings settings = _store.Load();

        Assert.Equal(20, settings.RotationSeconds);
        Assert.True(settings.ShowTrayIcon);
        Assert.Equal(["gemini", "claude", "codex", "copilot"], settings.Providers.Select(p => p.Id));
        Assert.False(settings.Providers[0].Enabled);
        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".bak"));
    }

    [Fact]
    public void Load_ToleratesCommentsTrailingCommasPascalCaseAndNumericEnums()
    {
        WriteRaw("""
            {
              // hand-edited
              "RotationSeconds": 500,
              "placement": 2,
              "valueMode": "remaining",
              "refreshMinutes": 11,
              "textColor": 7,
            }
            """);

        AppSettings settings = _store.Load();

        Assert.Equal(60, settings.RotationSeconds);
        Assert.Equal(WidgetPlacement.BeforeTray, settings.Placement);
        Assert.Equal(ValueMode.Remaining, settings.ValueMode);
        Assert.Equal(10, settings.RefreshMinutes);
        Assert.Equal(TextColorMode.Auto, settings.TextColor);
    }

    [Fact]
    public void Load_NormalizesProvidersFromDisk()
    {
        WriteRaw("""
            { "providers": [ { "id": "codex" }, { "id": "codex" }, { "id": "bard" }, null ] }
            """);

        AppSettings settings = _store.Load();

        Assert.Equal(["codex", "claude", "gemini", "copilot"], settings.Providers.Select(p => p.Id));
        Assert.Equal(("5h", "weekly"), (settings.Providers[0].Line1Key, settings.Providers[0].Line2Key));
    }

    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        string nested = _temp.Combine("a", "b", "settings.json");

        new SettingsStore(nested, _log).Save(new AppSettings());

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Load_JsonWithoutLanguageFields_UsesDefaults()
    {
        WriteRaw("""{ "version": 1, "rotationSeconds": 8 }""");

        AppSettings settings = _store.Load();

        Assert.Equal("auto", settings.Language);
        Assert.True(settings.ShowResetTime);
        Assert.True(settings.ShowDetailCard);
        Assert.Equal(8, settings.RotationSeconds);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsShowDetailCard()
    {
        _store.Save(new AppSettings { ShowDetailCard = false });

        AppSettings loaded = new SettingsStore(_path, _log).Load();

        Assert.False(loaded.ShowDetailCard);
    }

    [Fact]
    public void Load_JsonWithoutSystemMonitor_IsOff()
    {
        WriteRaw("""{ "version": 1, "rotationSeconds": 8 }""");

        Assert.Equal(SystemMonitorMode.Off, _store.Load().SystemMonitor);
    }

    [Theory]
    [InlineData(SystemMonitorMode.RotationPanel)]
    [InlineData(SystemMonitorMode.AlwaysBeside)]
    [InlineData(SystemMonitorMode.CardOnly)]
    public void SaveThenLoad_RoundTripsSystemMonitor(SystemMonitorMode mode)
    {
        _store.Save(new AppSettings { SystemMonitor = mode });

        AppSettings loaded = new SettingsStore(_path, _log).Load();

        Assert.Equal(mode, loaded.SystemMonitor);
    }

    [Fact]
    public void Load_UnknownSystemMonitorValue_FallsBackToOff()
    {
        WriteRaw("""{ "version": 1, "systemMonitor": 42 }""");

        Assert.Equal(SystemMonitorMode.Off, _store.Load().SystemMonitor);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsLanguageAndShowResetTime()
    {
        _store.Save(new AppSettings { Language = "ko", ShowResetTime = false });

        AppSettings loaded = new SettingsStore(_path, _log).Load();

        Assert.Equal("ko", loaded.Language);
        Assert.False(loaded.ShowResetTime);
    }
}
