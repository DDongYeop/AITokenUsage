using AITokenUsage.Core.Localization;
using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Settings;

/// <summary>Persisted user settings (%APPDATA%\AITokenUsage\settings.json).</summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;
    public const int MinRotationSeconds = 2;
    public const int MaxRotationSeconds = 60;

    public const string LanguageAuto = "auto";
    public const string LanguageKorean = "ko";
    public const string LanguageEnglish = "en";

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Display order and per-provider options.</summary>
    public List<ProviderSettings> Providers { get; set; } = ProviderDefaults.CreateDefaultProviders();

    public bool RotationEnabled { get; set; } = true;

    /// <summary>2..60 seconds.</summary>
    public int RotationSeconds { get; set; } = 5;

    public bool PauseOnHover { get; set; } = true;

    public bool SkipUnavailable { get; set; } = true;

    /// <summary>null = primary monitor; otherwise a device name such as @"\\.\DISPLAY2".</summary>
    public string? MonitorDeviceName { get; set; }

    public WidgetPlacement Placement { get; set; } = WidgetPlacement.LeftEdge;

    public ValueMode ValueMode { get; set; } = ValueMode.Used;

    public TextColorMode TextColor { get; set; } = TextColorMode.Auto;

    /// <summary>One of ProviderDefaults.RefreshChoicesMinutes (2, 5, 10, 15).</summary>
    public int RefreshMinutes { get; set; } = 5;

    public bool StartWithWindows { get; set; }

    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>auto | ko | en</summary>
    public string Language { get; set; } = LanguageAuto;

    /// <summary>위젯에 초기화까지 남은 시간 열 표시</summary>
    public bool ShowResetTime { get; set; } = true;

    /// <summary>
    /// Repairs this instance in place and returns it:
    /// unknown/duplicate provider ids are dropped (first one wins, ids are lower-cased),
    /// missing providers are appended with defaults in canonical order, blank line keys get the
    /// provider defaults, RotationSeconds is clamped to 2..60, RefreshMinutes snaps to the nearest
    /// of 2/5/10/15, undefined enum values fall back to their defaults, a blank monitor name becomes null
    /// and an unknown Language becomes "auto".
    /// Existing ProviderSettings instances are kept (UI bindings stay valid).
    /// </summary>
    public AppSettings Normalize()
    {
        Version = CurrentVersion;
        Providers = NormalizeProviders(Providers);
        RotationSeconds = Math.Clamp(RotationSeconds, MinRotationSeconds, MaxRotationSeconds);
        RefreshMinutes = NearestRefreshChoice(RefreshMinutes);
        Language = LanguageResolver.Normalize(Language);

        if (!Enum.IsDefined(Placement))
        {
            Placement = WidgetPlacement.LeftEdge;
        }

        if (!Enum.IsDefined(ValueMode))
        {
            ValueMode = ValueMode.Used;
        }

        if (!Enum.IsDefined(TextColor))
        {
            TextColor = TextColorMode.Auto;
        }

        if (string.IsNullOrWhiteSpace(MonitorDeviceName))
        {
            MonitorDeviceName = null;
        }

        return this;
    }

    /// <summary>
    /// Deep copy: a new Providers list holding new ProviderSettings instances; every other property is copied.
    /// Does not normalize. Used to hand an immutable-in-practice snapshot to a background save.
    /// </summary>
    public AppSettings Clone() => new()
    {
        Version = Version,
        Providers = Providers?.Select(p => p is null
            ? null!
            : new ProviderSettings { Id = p.Id, Enabled = p.Enabled, Line1Key = p.Line1Key, Line2Key = p.Line2Key }).ToList()!,
        RotationEnabled = RotationEnabled,
        RotationSeconds = RotationSeconds,
        PauseOnHover = PauseOnHover,
        SkipUnavailable = SkipUnavailable,
        MonitorDeviceName = MonitorDeviceName,
        Placement = Placement,
        ValueMode = ValueMode,
        TextColor = TextColor,
        RefreshMinutes = RefreshMinutes,
        StartWithWindows = StartWithWindows,
        ShowTrayIcon = ShowTrayIcon,
        Language = Language,
        ShowResetTime = ShowResetTime,
    };

    private static List<ProviderSettings> NormalizeProviders(List<ProviderSettings>? providers)
    {
        var result = new List<ProviderSettings>(ProviderIds.All.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (ProviderSettings? provider in providers ?? [])
        {
            if (provider is null)
            {
                continue;
            }

            string id = (provider.Id ?? "").Trim().ToLowerInvariant();
            if (!ProviderIds.All.Contains(id) || !seen.Add(id))
            {
                continue;
            }

            (string line1, string line2) = ProviderDefaults.DefaultLines(id);
            provider.Id = id;
            provider.Line1Key = string.IsNullOrWhiteSpace(provider.Line1Key) ? line1 : provider.Line1Key.Trim();
            provider.Line2Key = string.IsNullOrWhiteSpace(provider.Line2Key) ? line2 : provider.Line2Key.Trim();
            result.Add(provider);
        }

        foreach (string id in ProviderIds.All)
        {
            if (seen.Add(id))
            {
                result.Add(ProviderDefaults.CreateDefault(id));
            }
        }

        return result;
    }

    private static int NearestRefreshChoice(int minutes) =>
        ProviderDefaults.RefreshChoicesMinutes.MinBy(choice => Math.Abs((long)choice - minutes));
}
