using AITokenUsage.Core.Settings;

namespace AITokenUsage.Core.Tests.Settings;

public class AppSettingsTests
{
    private static string[] Ids(AppSettings settings) => settings.Providers.Select(p => p.Id).ToArray();

    [Fact]
    public void Defaults_MatchTheSpec()
    {
        var settings = new AppSettings();

        Assert.Equal(1, settings.Version);
        Assert.Equal(["claude", "codex", "gemini", "copilot"], Ids(settings));
        Assert.All(settings.Providers, p => Assert.True(p.Enabled));
        Assert.True(settings.RotationEnabled);
        Assert.Equal(5, settings.RotationSeconds);
        Assert.True(settings.PauseOnHover);
        Assert.True(settings.SkipUnavailable);
        Assert.Null(settings.MonitorDeviceName);
        Assert.Equal(WidgetPlacement.LeftEdge, settings.Placement);
        Assert.Equal(ValueMode.Used, settings.ValueMode);
        Assert.Equal(TextColorMode.Auto, settings.TextColor);
        Assert.Equal(5, settings.RefreshMinutes);
        Assert.False(settings.StartWithWindows);
        Assert.True(settings.ShowTrayIcon);
    }

    [Fact]
    public void Normalize_AppendsMissingProvidersInCanonicalOrder_KeepingUserOrderFirst()
    {
        var settings = new AppSettings
        {
            Providers = [new ProviderSettings { Id = "copilot", Enabled = false, Line1Key = "monthly", Line2Key = "completions" }],
        };

        settings.Normalize();

        Assert.Equal(["copilot", "claude", "codex", "gemini"], Ids(settings));
        Assert.False(settings.Providers[0].Enabled);
        Assert.Equal("completions", settings.Providers[0].Line2Key);
        Assert.Equal(("session", "weekly"), (settings.Providers[1].Line1Key, settings.Providers[1].Line2Key));
        Assert.True(settings.Providers[1].Enabled);
    }

    [Fact]
    public void Normalize_RemovesUnknownIds()
    {
        var settings = new AppSettings
        {
            Providers =
            [
                new ProviderSettings { Id = "antigravity", Line1Key = "x", Line2Key = "y" },
                new ProviderSettings { Id = "", Line1Key = "x", Line2Key = "y" },
                new ProviderSettings { Id = "gemini", Line1Key = "flash-lite", Line2Key = "pro" },
            ],
        };

        settings.Normalize();

        Assert.Equal(["gemini", "claude", "codex", "copilot"], Ids(settings));
        Assert.Equal("flash-lite", settings.Providers[0].Line1Key);
    }

    [Fact]
    public void Normalize_RemovesDuplicates_FirstWins_IdsAreCaseInsensitive()
    {
        var settings = new AppSettings
        {
            Providers =
            [
                new ProviderSettings { Id = " Claude ", Enabled = false, Line1Key = "weekly:fable", Line2Key = "weekly" },
                new ProviderSettings { Id = "claude", Enabled = true, Line1Key = "session", Line2Key = "session" },
                new ProviderSettings { Id = "CODEX", Line1Key = "5h", Line2Key = "weekly" },
            ],
        };

        settings.Normalize();

        Assert.Equal(["claude", "codex", "gemini", "copilot"], Ids(settings));
        Assert.False(settings.Providers[0].Enabled);
        Assert.Equal("weekly:fable", settings.Providers[0].Line1Key);
    }

    [Fact]
    public void Normalize_ReplacesBlankLineKeysWithDefaults_AndTrimsOthers()
    {
        var settings = new AppSettings
        {
            Providers =
            [
                new ProviderSettings { Id = "claude", Line1Key = "", Line2Key = "  " },
                new ProviderSettings { Id = "codex", Line1Key = null!, Line2Key = " daily " },
                new ProviderSettings { Id = "gemini", Line1Key = "flash", Line2Key = "" },
                new ProviderSettings { Id = "copilot", Line1Key = "", Line2Key = "" },
            ],
        };

        settings.Normalize();

        Assert.Equal(("session", "weekly"), (settings.Providers[0].Line1Key, settings.Providers[0].Line2Key));
        Assert.Equal(("5h", "daily"), (settings.Providers[1].Line1Key, settings.Providers[1].Line2Key));
        Assert.Equal(("flash", "flash"), (settings.Providers[2].Line1Key, settings.Providers[2].Line2Key));
        Assert.Equal(("monthly", "reset"), (settings.Providers[3].Line1Key, settings.Providers[3].Line2Key));
    }

    [Fact]
    public void Normalize_HandlesNullListAndNullEntries()
    {
        var settings = new AppSettings { Providers = null! };
        settings.Normalize();
        Assert.Equal(["claude", "codex", "gemini", "copilot"], Ids(settings));

        var withNulls = new AppSettings { Providers = [null!, new ProviderSettings { Id = "gemini" }, null!] };
        withNulls.Normalize();
        Assert.Equal(["gemini", "claude", "codex", "copilot"], Ids(withNulls));
        Assert.Equal(("pro", "flash"), (withNulls.Providers[0].Line1Key, withNulls.Providers[0].Line2Key));
    }

    [Theory]
    [InlineData(-5, 2)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(5, 5)]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(61, 60)]
    [InlineData(int.MaxValue, 60)]
    public void Normalize_ClampsRotationSeconds(int input, int expected)
    {
        var settings = new AppSettings { RotationSeconds = input }.Normalize();

        Assert.Equal(expected, settings.RotationSeconds);
    }

    [Theory]
    [InlineData(int.MinValue, 2)]
    [InlineData(-3, 2)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 5)]
    [InlineData(5, 5)]
    [InlineData(7, 5)]
    [InlineData(8, 10)]
    [InlineData(10, 10)]
    [InlineData(12, 10)]
    [InlineData(13, 15)]
    [InlineData(15, 15)]
    [InlineData(100, 15)]
    [InlineData(int.MaxValue, 15)]
    public void Normalize_SnapsRefreshMinutesToNearestChoice(int input, int expected)
    {
        var settings = new AppSettings { RefreshMinutes = input }.Normalize();

        Assert.Equal(expected, settings.RefreshMinutes);
    }

    [Fact]
    public void Normalize_ResetsUndefinedEnumsVersionAndBlankMonitor()
    {
        var settings = new AppSettings
        {
            Version = 0,
            Placement = (WidgetPlacement)42,
            ValueMode = (ValueMode)(-1),
            TextColor = (TextColorMode)9,
            MonitorDeviceName = "   ",
        };

        settings.Normalize();

        Assert.Equal(AppSettings.CurrentVersion, settings.Version);
        Assert.Equal(WidgetPlacement.LeftEdge, settings.Placement);
        Assert.Equal(ValueMode.Used, settings.ValueMode);
        Assert.Equal(TextColorMode.Auto, settings.TextColor);
        Assert.Null(settings.MonitorDeviceName);
    }

    [Fact]
    public void Normalize_KeepsValidValuesAndInstances()
    {
        var settings = new AppSettings
        {
            Placement = WidgetPlacement.BeforeTray,
            ValueMode = ValueMode.Remaining,
            TextColor = TextColorMode.Dark,
            MonitorDeviceName = @"\\.\DISPLAY2",
            RotationEnabled = false,
            StartWithWindows = true,
        };
        ProviderSettings claude = settings.Providers[0];

        AppSettings returned = settings.Normalize();

        Assert.Same(settings, returned);
        Assert.Same(claude, settings.Providers[0]);
        Assert.Equal(WidgetPlacement.BeforeTray, settings.Placement);
        Assert.Equal(ValueMode.Remaining, settings.ValueMode);
        Assert.Equal(TextColorMode.Dark, settings.TextColor);
        Assert.Equal(@"\\.\DISPLAY2", settings.MonitorDeviceName);
        Assert.False(settings.RotationEnabled);
        Assert.True(settings.StartWithWindows);
    }

    [Fact]
    public void Clone_CopiesEverything_AndSharesNoMutableState()
    {
        var original = new AppSettings
        {
            Version = 1,
            Providers =
            [
                new ProviderSettings { Id = "gemini", Enabled = false, Line1Key = "flash-lite", Line2Key = "pro" },
                new ProviderSettings { Id = "claude", Enabled = true, Line1Key = "weekly:fable", Line2Key = "session" },
            ],
            RotationEnabled = false,
            RotationSeconds = 17,
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

        AppSettings clone = original.Clone();

        Assert.NotSame(original, clone);
        Assert.NotSame(original.Providers, clone.Providers);
        Assert.Equal(original.Providers.Count, clone.Providers.Count);
        for (int i = 0; i < original.Providers.Count; i++)
        {
            Assert.NotSame(original.Providers[i], clone.Providers[i]);
            Assert.Equal(
                (original.Providers[i].Id, original.Providers[i].Enabled, original.Providers[i].Line1Key, original.Providers[i].Line2Key),
                (clone.Providers[i].Id, clone.Providers[i].Enabled, clone.Providers[i].Line1Key, clone.Providers[i].Line2Key));
        }

        Assert.Equal(
            (original.Version, original.RotationEnabled, original.RotationSeconds, original.PauseOnHover, original.SkipUnavailable,
             original.MonitorDeviceName, original.Placement, original.ValueMode, original.TextColor, original.RefreshMinutes,
             original.StartWithWindows, original.ShowTrayIcon),
            (clone.Version, clone.RotationEnabled, clone.RotationSeconds, clone.PauseOnHover, clone.SkipUnavailable,
             clone.MonitorDeviceName, clone.Placement, clone.ValueMode, clone.TextColor, clone.RefreshMinutes,
             clone.StartWithWindows, clone.ShowTrayIcon));

        // Mutating the clone (list order, entries, scalars) must not touch the original.
        (clone.Providers[0], clone.Providers[1]) = (clone.Providers[1], clone.Providers[0]);
        clone.Providers[0].Enabled = false;
        clone.Providers[0].Line1Key = "session";
        clone.Providers.Add(new ProviderSettings { Id = "codex", Line1Key = "5h", Line2Key = "weekly" });
        clone.RotationSeconds = 3;

        Assert.Equal(["gemini", "claude"], Ids(original));
        Assert.True(original.Providers[1].Enabled);
        Assert.Equal("weekly:fable", original.Providers[1].Line1Key);
        Assert.Equal(17, original.RotationSeconds);
    }

    [Fact]
    public void LanguageAndShowResetTime_DefaultsAreAutoAndTrue()
    {
        var settings = new AppSettings();

        Assert.Equal(AppSettings.LanguageAuto, settings.Language);
        Assert.Equal("auto", settings.Language);
        Assert.True(settings.ShowResetTime);
    }

    [Fact]
    public void Normalize_UnknownLanguage_BecomesAuto()
    {
        var settings = new AppSettings { Language = "xx" };

        settings.Normalize();

        Assert.Equal("auto", settings.Language);
    }

    [Fact]
    public void Normalize_KnownLanguage_IsKeptLowerCased()
    {
        var settings = new AppSettings { Language = " EN " };

        settings.Normalize();

        Assert.Equal(AppSettings.LanguageEnglish, settings.Language);
    }

    [Fact]
    public void Clone_CopiesLanguageAndShowResetTime()
    {
        var original = new AppSettings { Language = "en", ShowResetTime = false };

        AppSettings clone = original.Clone();

        Assert.Equal("en", clone.Language);
        Assert.False(clone.ShowResetTime);
    }
}
