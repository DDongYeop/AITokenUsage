using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Accounts;
using AITokenUsage.Core.Localization;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Polling;
using AITokenUsage.Core.Presentation;
using AITokenUsage.Core.Providers;
using AITokenUsage.Core.Providers.Copilot;
using AITokenUsage.Core.Settings;

namespace AITokenUsage.Agent;

/// <summary>
/// aitokenusage-agent [--once] [--language ko|en]
///   --once      refresh every AI once, print one state line and exit (handy in a terminal).
///   --language  the macOS UI language, used when the "language" setting is "auto".
/// Otherwise runs until stdin closes or "quit" arrives, printing a state line whenever something changes and every
/// 30 s (so "resets in" texts stay current). Commands, one per line on stdin:
///   refresh [id] | set-enabled &lt;id&gt; true|false | set-lines &lt;id&gt; &lt;key1&gt; &lt;key2&gt; | move &lt;id&gt; up|down
///   | set-nickname &lt;id&gt; [nickname including spaces] (omit the nickname to clear it)
///   | set-value-mode used|remaining | set-rotation &lt;seconds&gt;|off | set-rotation-enabled true|false
///   | set-rotation-seconds &lt;n&gt; | set-skip-unavailable true|false | set-pause-on-hover true|false
///   | set-show-reset-time true|false | set-widget-font-family [base64-encoded UTF-8 family name]
///   | set-refresh &lt;minutes&gt; | set-language auto|ko|en | copilot-signin | copilot-cancel | copilot-signout | quit
/// Besides "state" lines, Copilot sign-in progress is written as {"type":"copilotSignIn","phase":...} lines.
/// </summary>
public static class Program
{
    private static readonly TimeSpan PeriodicEmit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OnceTimeout = TimeSpan.FromSeconds(45);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly object Gate = new();

    public static async Task<int> Main(string[] args)
    {
        // The Swift client exchanges UTF-8 lines regardless of the host's console code page.
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        bool once = args.Contains("--once");
        string? osLanguage = ArgumentAfter(args, "--language");

        AugmentPath();
        Directory.CreateDirectory(MacPaths.DataDirectory);
        using var log = new FileAppLog(MacPaths.LogDirectory);
        log.Info($"aitokenusage-agent {typeof(Program).Assembly.GetName().Version} starting (pid {Environment.ProcessId})");

        var settingsStore = new SettingsStore(MacPaths.SettingsFile, log);
        AppSettings settings = settingsStore.Load();
        CultureInfo culture = ApplyLanguage(settings.Language, osLanguage);

        using HttpClient http = HttpClientFactory.Create();
        var ctx = new ProviderContext(
            http,
            TimeProvider.System,
            new RealFileSystem(),
            new RealEnvironment(),
            new RealProcessRunner(),
            OperatingSystem.IsMacOS() ? new MacKeychainCredentialStore(log) : new WindowsCredentialStore(),
            OperatingSystem.IsWindows() ? new DpapiSecretStore(MacPaths.SecretsDirectory) : new UnixFileSecretStore(MacPaths.SecretsDirectory),
            log);

        IReadOnlyList<IUsageProvider> providers = ProviderCatalog.CreateAll(
            ctx,
            settings.Providers.Select(p => p.Id).Where(AccountIds.IsAccountId),
            MacPaths.AccountsDirectory);

        int refreshMinutes = settings.RefreshMinutes;
        await using var usage = new UsageService(
            providers,
            () => TimeSpan.FromMinutes(Volatile.Read(ref refreshMinutes)),
            new SnapshotCache(MacPaths.CacheFile, log),
            TimeProvider.System,
            log);

        var emitter = new StateEmitter(usage, providers, settings, culture);
        var copilotSignIn = new CopilotSignIn(
            providers.OfType<CopilotUsageProvider>().First(), new GitHubDeviceFlow(http, TimeProvider.System), usage, log);

        if (once)
        {
            usage.Start();
            Task refresh = usage.RefreshNowAsync();
            await Task.WhenAny(refresh, Task.Delay(OnceTimeout)).ConfigureAwait(false);
            emitter.Emit();
            return 0;
        }

        usage.ViewChanged += (_, _) => emitter.EmitSoon();
        usage.Start();
        emitter.Emit();

        using var periodic = new PeriodicTimer(PeriodicEmit);
        using var stop = new CancellationTokenSource();
        Task ticker = Task.Run(async () =>
        {
            try
            {
                while (await periodic.WaitForNextTickAsync(stop.Token).ConfigureAwait(false)) emitter.Emit();
            }
            catch (OperationCanceledException)
            {
            }
        });

        string? line;
        while ((line = await Console.In.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            // A nickname occupies the rest of the line, including any interior spaces.
            int maxParts = line.TrimStart().StartsWith("set-nickname ", StringComparison.Ordinal) ? 3 : int.MaxValue;
            string[] parts = line.Split(' ', maxParts, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) continue;
            if (parts[0] == "quit") break;
            if (copilotSignIn.TryHandle(parts[0])) continue;

            try
            {
                if (HandleCommand(parts, usage, settings, settingsStore, ref refreshMinutes, log))
                {
                    emitter.Emit();
                }
            }
            catch (Exception ex)
            {
                log.Warn($"command '{parts[0]}' failed", ex);
            }
        }

        copilotSignIn.Cancel();
        stop.Cancel();
        await ticker.ConfigureAwait(false);
        log.Info("aitokenusage-agent stopping");
        return 0;
    }

    /// <summary>Applies one command; true when the state should be re-emitted.</summary>
    private static bool HandleCommand(string[] parts, UsageService usage, AppSettings settings, SettingsStore store,
        ref int refreshMinutes, IAppLog log)
    {
        switch (parts[0])
        {
            case "refresh":
                _ = usage.RefreshNowAsync(parts.Length > 1 ? parts[1] : null);
                return false;

            case "set-enabled" when parts.Length == 3 && bool.TryParse(parts[2], out bool enabled):
                lock (Gate)
                {
                    if (settings.Providers.FirstOrDefault(p => p.Id == parts[1]) is not { } provider) return false;
                    provider.Enabled = enabled;
                }

                break;

            case "set-nickname" when parts.Length is 2 or 3:
                lock (Gate)
                {
                    if (settings.Providers.FirstOrDefault(p => p.Id == parts[1]) is not { } provider) return false;
                    provider.Nickname = parts.Length == 3 ? parts[2] : null;
                }

                break;

            case "set-value-mode" when parts.Length == 2:
                lock (Gate) settings.ValueMode = parts[1] == "remaining" ? ValueMode.Remaining : ValueMode.Used;
                break;

            case "set-lines" when parts.Length == 4:
                lock (Gate)
                {
                    if (settings.Providers.FirstOrDefault(p => p.Id == parts[1]) is not { } provider) return false;
                    provider.Line1Key = parts[2];
                    provider.Line2Key = parts[3];
                }

                break;

            case "move" when parts.Length == 3:
                lock (Gate)
                {
                    int index = settings.Providers.FindIndex(p => p.Id == parts[1]);
                    int target = parts[2] == "up" ? index - 1 : index + 1;
                    if (index < 0 || target < 0 || target >= settings.Providers.Count) return false;
                    (settings.Providers[index], settings.Providers[target]) = (settings.Providers[target], settings.Providers[index]);
                }

                break;

            case "set-rotation-enabled" when parts.Length == 2 && bool.TryParse(parts[1], out bool rotate):
                lock (Gate) settings.RotationEnabled = rotate;
                break;

            case "set-rotation-seconds" when parts.Length == 2
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rotationSeconds):
                lock (Gate) settings.RotationSeconds = rotationSeconds;
                break;

            case "set-skip-unavailable" when parts.Length == 2 && bool.TryParse(parts[1], out bool skip):
                lock (Gate) settings.SkipUnavailable = skip;
                break;

            case "set-pause-on-hover" when parts.Length == 2 && bool.TryParse(parts[1], out bool pause):
                lock (Gate) settings.PauseOnHover = pause;
                break;

            case "set-widget-font-family" when parts.Length <= 2:
                string? family = parts.Length == 1 ? null : System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
                lock (Gate) settings.WidgetFontFamily = family;
                break;

            case "set-show-reset-time" when parts.Length == 2 && bool.TryParse(parts[1], out bool showReset):
                lock (Gate) settings.ShowResetTime = showReset;
                break;

            case "set-rotation" when parts.Length == 2:
                lock (Gate)
                {
                    if (parts[1] == "off")
                    {
                        settings.RotationEnabled = false;
                    }
                    else if (int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
                    {
                        settings.RotationEnabled = true;
                        settings.RotationSeconds = seconds;
                    }
                }

                break;

            case "set-refresh" when parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes):
                lock (Gate) settings.RefreshMinutes = minutes;
                break;

            case "set-language" when parts.Length == 2:
                // Takes effect when the Mac app restarts the agent: Core strings are built in the startup culture.
                lock (Gate) settings.Language = parts[1];
                break;

            default:
                log.Info($"unknown command '{parts[0]}'");
                return false;
        }

        AppSettings snapshot;
        lock (Gate)
        {
            settings.Normalize();
            int previous = Volatile.Read(ref refreshMinutes);
            Volatile.Write(ref refreshMinutes, settings.RefreshMinutes);
            if (previous != settings.RefreshMinutes) usage.RescheduleAll();
            snapshot = settings.Clone();
        }

        store.Save(snapshot);
        return true;
    }

    /// <summary>Writes one JSON line; shared by every kind of message so lines never interleave.</summary>
    internal static void WriteLine(object message)
    {
        string json = JsonSerializer.Serialize(message, JsonOptions);
        lock (Console.Out)
        {
            Console.Out.WriteLine(json);
            Console.Out.Flush();
        }
    }

    private static CultureInfo ApplyLanguage(string? setting, string? osLanguage)
    {
        CultureInfo os = CultureInfo.InstalledUICulture;
        if (!string.IsNullOrWhiteSpace(osLanguage))
        {
            try
            {
                os = CultureInfo.GetCultureInfo(osLanguage);
            }
            catch (CultureNotFoundException)
            {
            }
        }

        CultureInfo culture = LanguageResolver.Resolve(setting, os);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        return culture;
    }

    /// <summary>
    /// A menu bar app started from Finder gets PATH=/usr/bin:/bin:/usr/sbin:/sbin. Add the folders where Homebrew, npm
    /// and the CLI installers live so claude, codex, gemini, gh and the node that runs npm shims can be found and started.
    /// </summary>
    private static void AugmentPath()
    {
        if (OperatingSystem.IsWindows()) return;
        string current = Environment.GetEnvironmentVariable("PATH") ?? "";
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string augmented = string.Join(':', RealProcessRunner.UnixSearchDirectories(current, home).Where(Directory.Exists));
        Environment.SetEnvironmentVariable("PATH", augmented);
    }

    private static string? ArgumentAfter(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>Serializes the panels and writes them as one line; EmitSoon coalesces bursts of ViewChanged.</summary>
    private sealed class StateEmitter(UsageService usage, IReadOnlyList<IUsageProvider> providers, AppSettings settings, CultureInfo culture)
    {
        private static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(250);
        private int _scheduled;

        public void EmitSoon()
        {
            if (Interlocked.Exchange(ref _scheduled, 1) == 1) return;
            _ = Task.Delay(Coalesce).ContinueWith(_ =>
            {
                Volatile.Write(ref _scheduled, 0);
                Emit();
            }, TaskScheduler.Default);
        }

        public void Emit()
        {
            // Keep a pending poll from writing an older snapshot after a settings command.
            lock (Gate)
            {
                WriteLine(Build());
            }
        }

        private AgentState Build()
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeZoneInfo zone = TimeZoneInfo.Local;

            var views = new List<ProviderView>();
            foreach (ProviderSettings provider in settings.Providers)
            {
                if (usage.GetView(provider.Id) is ProviderView view) views.Add(view);
            }

            var byId = settings.Providers.ToDictionary(p => p.Id);
            // Settings can distinguish every account, including disabled ones. The widget tags only visible accounts.
            var all = AccountTags.Apply(views).Select(v => ToEntry(byId[v.ProviderId], v, now, zone)).ToArray();
            var enabled = views.Where(v => byId[v.ProviderId].Enabled).ToList();
            IReadOnlyList<ProviderView> tagged = AccountTags.Apply(enabled, settings.Providers);
            var panels = new List<PanelState>(tagged.Count);
            for (int i = 0; i < tagged.Count; i++)
            {
                ProviderView view = tagged[i];
                ProviderSettings lines = byId[view.ProviderId];
                WidgetPanelModel widget = WidgetPanelBuilder.Build(
                    view, lines.Line1Key, lines.Line2Key, settings.ValueMode, tagged.Count, i, now);
                DetailCardModel card = DetailCardBuilder.Build(view, settings.ValueMode, now, zone);
                panels.Add(new PanelState(
                    view.ProviderId,
                    ProviderIds.KindOf(view.ProviderId),
                    view.Title,
                    widget.DisplayName,
                    view.Current.Status,
                    view.IsStale,
                    view.Current.Status == ProviderStatus.Ok || view.HasAnyData,
                    ToRow(widget.Row1),
                    widget.Row2 is null ? null : ToRow(widget.Row2),
                    new CardState(card.Plan, card.Rows.Select(ToRow).ToArray(), card.ExtraLine, card.StatusLines)));
            }

            return new AgentState(
                "state",
                now,
                culture.TwoLetterISOLanguageName,
                settings.Language,
                settings.ValueMode,
                settings.RotationEnabled,
                settings.RotationSeconds,
                settings.SkipUnavailable,
                settings.PauseOnHover,
                settings.WidgetFontFamily,
                settings.ShowResetTime,
                settings.RefreshMinutes,
                typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "",
                MacPaths.SettingsFile,
                MacPaths.LogDirectory,
                panels,
                all);
        }

        private ProviderEntry ToEntry(ProviderSettings provider, ProviderView view, DateTimeOffset now, TimeZoneInfo zone)
        {
            IUsageProvider? source = providers.FirstOrDefault(p => p.Id == provider.Id);
            IReadOnlyList<MeterOption> options = MeterOptionList.Build(
                source?.StaticMeterOptions ?? [], view.DisplayMeters, [provider.Line1Key, provider.Line2Key]);
            ConnectionGuide? guide = ConnectionGuideBuilder.Build(view.ProviderId, view.Current.Status, view.Current.StatusMessage);
            return new ProviderEntry(
                view.ProviderId,
                ProviderIds.KindOf(view.ProviderId),
                view.IdentityTitle,
                provider.Nickname,
                provider.Enabled,
                view.Current.Status,
                ProviderStatusText.ForSettings(view, now, zone),
                provider.Line1Key,
                provider.Line2Key,
                options.Select(o => new OptionState(o.Key, o.Label)).ToArray(),
                guide is null
                    ? null
                    : new GuideState(
                        guide.Steps.Select(g => new GuideStepState(g.Text, g.Command, g.Action, g.TerminalCommand)).ToArray(),
                        guide.Footnote));
        }

        private static RowState ToRow(WidgetRow row) =>
            new(row.Kind, row.Label, row.Fraction, row.Value, row.Severity, row.Dimmed, row.ResetText);

        private static CardRowState ToRow(CardRow row) =>
            new(row.Label, row.Fraction, row.Value, row.ResetText, row.Severity, row.IsText, row.Dimmed);
    }

    private sealed record AgentState(
        string Type,
        DateTimeOffset GeneratedAt,
        string Language,
        string LanguageSetting,
        ValueMode ValueMode,
        bool RotationEnabled,
        int RotationSeconds,
        bool SkipUnavailable,
        bool PauseOnHover,
        string? WidgetFontFamily,
        bool ShowResetTime,
        int RefreshMinutes,
        string Version,
        string SettingsFile,
        string LogDirectory,
        IReadOnlyList<PanelState> Panels,
        IReadOnlyList<ProviderEntry> Providers);

    /// <summary>One card of the "AI 연결" settings page, in settings order (enabled or not).</summary>
    private sealed record ProviderEntry(
        string Id,
        string Kind,
        string Title,
        string? Nickname,
        bool Enabled,
        ProviderStatus Status,
        string StatusText,
        string Line1Key,
        string Line2Key,
        IReadOnlyList<OptionState> MeterOptions,
        GuideState? Guide);

    private sealed record OptionState(string Key, string Label);

    private sealed record GuideState(IReadOnlyList<GuideStepState> Steps, string? Footnote);

    private sealed record GuideStepState(string Text, string? Command, GuideAction Action, string? TerminalCommand);

    private sealed record PanelState(
        string Id,
        string Kind,
        string Title,
        string ShortName,
        ProviderStatus Status,
        bool IsStale,
        bool Available,
        RowState Row1,
        RowState? Row2,
        CardState Card);

    private sealed record RowState(WidgetRowKind Kind, string Label, double? Fraction, string Value, Severity Severity, bool Dimmed, string ResetText);

    private sealed record CardState(string? Plan, IReadOnlyList<CardRowState> Rows, string? ExtraLine, IReadOnlyList<string> StatusLines);

    private sealed record CardRowState(string Label, double? Fraction, string Value, string ResetText, Severity Severity, bool IsText, bool Dimmed);
}
