using System.Globalization;
using System.Text.Json;
using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Util;

namespace AITokenUsage.Core.Providers.Codex;

public enum CodexAppServerOutcome { Ok, AuthRequired, Failed, TimedOut }

public sealed record CodexAppServerResult(CodexAppServerOutcome Outcome, string? PlanType, IReadOnlyList<UsageMeter> Meters)
{
    public static CodexAppServerResult Of(CodexAppServerOutcome outcome) => new(outcome, null, Array.Empty<UsageMeter>());
}

/// <summary>
/// Runs `codex -s read-only -a never app-server` and speaks newline-delimited JSON-RPC (no "jsonrpc" field):
/// initialize (8 s) → initialized → account/rateLimits/read {excludeResetCreditDetails:true} (3 s).
/// The app-server refreshes a stale token itself and saves auth.json under its own lock.
/// </summary>
public sealed class CodexAppServerClient
{
    public const string CommandName = "codex";
    public static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(20);

    private const int InitializeId = 1;
    private const int RateLimitsId = 2;
    private const string InitializedNotification = "{\"method\":\"initialized\",\"params\":{}}";
    private const string RateLimitsRequest =
        "{\"id\":2,\"method\":\"account/rateLimits/read\",\"params\":{\"excludeResetCreditDetails\":true}}";

    private static readonly string[] AppServerArguments = ["-s", "read-only", "-a", "never", "app-server"];

    private static readonly string InitializeRequest =
        "{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"ai-token-usage\",\"title\":\"AITokenUsage\",\"version\":\""
        + (typeof(CodexAppServerClient).Assembly.GetName().Version?.ToString(3) ?? "0.1.0")
        + "\"},\"capabilities\":null}}";

    private readonly IProcessRunner _processes;
    private readonly TimeProvider _time;
    private readonly IAppLog _log;

    public CodexAppServerClient(IProcessRunner processes, TimeProvider time, IAppLog log)
    {
        _processes = processes;
        _time = time;
        _log = log;
    }

    /// <summary>codex.exe/.com runs directly; codex.cmd (npm shim) or anything else runs through cmd.exe /d /c codex.</summary>
    public static ProcessSpec BuildSpec(string codexPath)
    {
        var extension = Path.GetExtension(codexPath);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".com", StringComparison.OrdinalIgnoreCase))
            return new ProcessSpec(codexPath, AppServerArguments, null, ProcessTimeout);
        return new ProcessSpec("cmd.exe", ["/d", "/c", CommandName, .. AppServerArguments], null, ProcessTimeout);
    }

    public async Task<CodexAppServerResult> ReadRateLimitsAsync(string codexPath, CancellationToken ct)
    {
        IInteractiveProcess process;
        try
        {
            process = _processes.StartInteractive(BuildSpec(codexPath));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn("Codex app-server could not be started: " + ex.GetType().Name);
            return CodexAppServerResult.Of(CodexAppServerOutcome.Failed);
        }

        try
        {
            await process.WriteLineAsync(InitializeRequest, ct).ConfigureAwait(false);
            var initialize = await ReadReplyAsync(process, InitializeId, InitializeTimeout, ct).ConfigureAwait(false);
            if (initialize.Outcome is not null) return CodexAppServerResult.Of(initialize.Outcome.Value);

            await process.WriteLineAsync(InitializedNotification, ct).ConfigureAwait(false);
            await process.WriteLineAsync(RateLimitsRequest, ct).ConfigureAwait(false);
            var rateLimits = await ReadReplyAsync(process, RateLimitsId, RequestTimeout, ct).ConfigureAwait(false);
            if (rateLimits.Outcome is not null) return CodexAppServerResult.Of(rateLimits.Outcome.Value);

            return MapRateLimits(rateLimits.Result!.Value);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _log.Warn("Codex app-server I/O failed: " + ex.GetType().Name);
            return CodexAppServerResult.Of(CodexAppServerOutcome.Failed);
        }
        finally
        {
            try
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn("Codex app-server shutdown failed: " + ex.GetType().Name);
            }
        }
    }

    /// <summary>Maps an account/rateLimits/read result: the "codex" family → main meters, other families → additional meters.</summary>
    public static CodexAppServerResult MapRateLimits(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return CodexAppServerResult.Of(CodexAppServerOutcome.Failed);

        var byLimitId = Json.Obj(result, "rateLimitsByLimitId");
        var main = (byLimitId is { } map ? Json.Obj(map, "codex") : null) ?? Json.Obj(result, "rateLimits");
        var mainId = (main is { } m0 ? Json.Str(m0, "limitId") : null) ?? "codex";

        var meters = new List<UsageMeter>();
        string? planType = null;
        if (main is { } snapshot)
        {
            planType = Json.Str(snapshot, "planType");
            meters.AddRange(CodexWindowLabeler.ToMeters(Window(snapshot, "primary"), Window(snapshot, "secondary")));
        }

        if (byLimitId is { } families)
        {
            foreach (var family in families.EnumerateObject())
            {
                if (family.Name == "codex" || family.Name == mainId || family.Value.ValueKind != JsonValueKind.Object) continue;
                var name = Json.Str(family.Value, "limitName");
                meters.AddRange(CodexWindowLabeler.ToMeters(
                    Window(family.Value, "primary"),
                    Window(family.Value, "secondary"),
                    family.Name,
                    string.IsNullOrWhiteSpace(name) ? family.Name : name));
            }
        }

        return new CodexAppServerResult(
            CodexAppServerOutcome.Ok, string.IsNullOrWhiteSpace(planType) ? null : planType, meters);
    }

    private static CodexWindow? Window(JsonElement snapshot, string name)
    {
        if (Json.Obj(snapshot, name) is not { } window) return null;
        var used = Json.Num(window, "usedPercent");
        if (used is null) return null;
        long? minutes = Json.Num(window, "windowDurationMins") is double mins && mins > 0 ? (long)Math.Round(mins) : null;
        // Out-of-range values (e.g. a millisecond epoch) degrade to "no reset time"; the meter is kept.
        DateTimeOffset? resetsAt = Json.Num(window, "resetsAt") is > 0 ? Json.EpochSeconds(window, "resetsAt") : null;
        return new CodexWindow(used, minutes, resetsAt);
    }

    private async Task<Reply> ReadReplyAsync(IInteractiveProcess process, int id, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        while (true)
        {
            string? line;
            try
            {
                line = await process.ReadLineAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Reply.Fail(CodexAppServerOutcome.TimedOut);
            }

            if (line is null)
            {
                return Reply.Fail(timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested
                    ? CodexAppServerOutcome.TimedOut
                    : CodexAppServerOutcome.Failed); // stdout closed: the process exited
            }

            if (TryMatch(line, id) is { } reply) return reply;
        }
    }

    // Lines without our id (notifications, server requests, log noise) are skipped.
    private Reply? TryMatch(string line, int id)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("method", out _)) return null;
            if (!root.TryGetProperty("id", out var idElement)) return null;
            var matches = idElement.ValueKind switch
            {
                JsonValueKind.Number => idElement.TryGetInt32(out var number) && number == id,
                JsonValueKind.String => idElement.GetString() == id.ToString(CultureInfo.InvariantCulture),
                _ => false,
            };
            if (!matches) return null;

            if (Json.Obj(root, "error") is { } error)
            {
                var message = Json.Str(error, "message") ?? "";
                _log.Warn($"Codex app-server returned an error for request {id}.");
                return Reply.Fail(message.Contains("authentication required", StringComparison.OrdinalIgnoreCase)
                    ? CodexAppServerOutcome.AuthRequired
                    : CodexAppServerOutcome.Failed);
            }

            return root.TryGetProperty("result", out var result)
                ? new Reply(result.Clone(), null)
                : Reply.Fail(CodexAppServerOutcome.Failed);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private readonly record struct Reply(JsonElement? Result, CodexAppServerOutcome? Outcome)
    {
        public static Reply Fail(CodexAppServerOutcome outcome) => new(null, outcome);
    }
}
