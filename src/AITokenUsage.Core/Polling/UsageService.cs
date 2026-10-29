using AITokenUsage.Core.Abstractions;
using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers;
using AITokenUsage.Core.Resources;

namespace AITokenUsage.Core.Polling;

/// <summary>
/// Runs one background polling loop per provider and merges results into <see cref="ProviderView"/>s.
/// <list type="bullet">
/// <item>Interval: max(refreshInterval(), MinInterval) through <see cref="BackoffPolicy"/>, ±10% jitter, never below MinInterval.</item>
/// <item>LastGood changes only on Ok; Current is always the latest result.</item>
/// <item>For RateLimited/Error results, Current.RetryAfter is replaced by the actual delay until the next automatic fetch.</item>
/// <item>A RateLimited result blocks RefreshNowAsync/NotifyPathChanged until the backoff delay has passed.</item>
/// <item>RescheduleAll re-arms unblocked timers from the last fetch time so a new refresh interval applies at once.</item>
/// </list>
/// ViewChanged is raised on a background thread after every fetch and once per provider restored from the cache.
/// </summary>
public sealed class UsageService : IAsyncDisposable
{
    public static string LoadingMessage => Strings.StatusLoading;
    public static string UnknownErrorMessage => Strings.StatusUnknownError;
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan MinimumDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyList<ProviderWorker> _workers;
    private readonly Func<TimeSpan> _refreshInterval;
    private readonly SnapshotCache _cache;
    private readonly TimeProvider _time;
    private readonly IAppLog _log;
    private readonly Func<double> _random;
    private readonly BackoffPolicy _backoff = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _stopping = NewSignal();
    private readonly object _cacheGate = new();
    private Task _runTask = Task.CompletedTask;
    private int _started;
    private int _disposed;

    /// <param name="random">Returns a value in [0, 1) used for jitter. Defaults to Random.Shared.NextDouble; tests inject a constant.</param>
    public UsageService(IReadOnlyList<IUsageProvider> providers, Func<TimeSpan> refreshInterval,
        SnapshotCache cache, TimeProvider time, IAppLog log, Func<double>? random = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(refreshInterval);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);

        DateTimeOffset now = time.GetUtcNow();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var workers = new List<ProviderWorker>(providers.Count);
        foreach (IUsageProvider provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider, nameof(providers));
            if (!ids.Add(provider.Id))
            {
                throw new ArgumentException($"Duplicate provider id '{provider.Id}'.", nameof(providers));
            }

            var loading = UsageSnapshot.WithStatus(provider.Id, ProviderStatus.Ok, now, LoadingMessage);
            workers.Add(new ProviderWorker(provider, new ProviderView(provider.Id, provider.DisplayName, loading, null)));
        }

        _workers = workers;
        _refreshInterval = refreshInterval;
        _cache = cache;
        _time = time;
        _log = log;
        _random = random ?? Random.Shared.NextDouble;
    }

    /// <summary>Raised on a background thread. Subscribers marshal to the UI thread themselves.</summary>
    public event EventHandler<ProviderView>? ViewChanged;

    /// <summary>Current views in provider registration order.</summary>
    public IReadOnlyList<ProviderView> Views => _workers.Select(w => w.View).ToArray();

    public ProviderView? GetView(string providerId) => Find(providerId)?.View;

    /// <summary>Loads the cache, fetches every provider once immediately, then keeps polling. Returns at once.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        CancellationToken ct = _shutdown.Token;
        _runTask = Task.Run(() => RunAllAsync(ct));
    }

    /// <summary>
    /// Fetches the given provider (or all when null) now and completes once the result is published.
    /// Providers inside a rate-limit block are skipped. Before Start or after disposal this is a no-op.
    /// </summary>
    public Task RefreshNowAsync(string? providerId = null)
    {
        if (!IsRunning)
        {
            return Task.CompletedTask;
        }

        var requests = new List<Task>();
        foreach (ProviderWorker worker in _workers)
        {
            if (providerId is null || string.Equals(worker.Provider.Id, providerId, StringComparison.Ordinal))
            {
                requests.Add(RequestFetch(worker));
            }
        }

        return Task.WhenAll(requests);
    }

    /// <summary>
    /// Called by the app's FileSystemWatcher. When the path equals or lies under one of a provider's WatchPaths,
    /// that provider is fetched after a 1.5 s debounce (unless rate-limit blocked at that moment).
    /// </summary>
    public void NotifyPathChanged(string fullPath)
    {
        if (!IsRunning || string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        foreach (ProviderWorker worker in _workers)
        {
            if (worker.Provider.WatchPaths.Any(watch => WatchPathMatcher.Matches(fullPath, watch)))
            {
                ScheduleDebouncedFetch(worker);
            }
        }
    }

    /// <summary>
    /// Applies a changed refresh interval at once: re-arms each provider's automatic timer to
    /// last fetch time + a delay recomputed from the current refreshInterval() (backoff, jitter and floors as usual).
    /// A due time already in the past fetches immediately. Providers that are rate-limit blocked, mid-fetch
    /// (that fetch arms its next timer with the new interval) or never fetched are left alone.
    /// Views are not changed and no event is raised here. Thread-safe; a no-op before Start or after disposal.
    /// </summary>
    public void RescheduleAll()
    {
        if (!IsRunning)
        {
            return;
        }

        TimeSpan configured = ConfiguredInterval();
        DateTimeOffset now = _time.GetUtcNow();
        foreach (ProviderWorker worker in _workers)
        {
            lock (worker.Gate)
            {
                if (worker.IsShutDown || worker.IsFetching || worker.LastResult is not { } last || IsBlocked(worker))
                {
                    continue;
                }

                TimeSpan delay = ComputeDelay(worker, last, configured).Delay;
                ArmTimer(worker, worker.LastFetchAt + delay - now);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _shutdown.Cancel();
        _stopping.TrySetResult();
        try
        {
            await _runTask.WaitAsync(ShutdownTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (TimeoutException)
        {
            _log.Warn("Usage polling did not stop within the shutdown timeout.");
        }
        catch (Exception ex)
        {
            _log.Warn("Usage polling stopped with an error.", ex);
        }

        foreach (ProviderWorker worker in _workers)
        {
            worker.Shutdown();
        }
    }

    private bool IsRunning => Volatile.Read(ref _started) == 1 && Volatile.Read(ref _disposed) == 0;

    private ProviderWorker? Find(string providerId) =>
        _workers.FirstOrDefault(w => string.Equals(w.Provider.Id, providerId, StringComparison.Ordinal));

    private async Task RunAllAsync(CancellationToken ct)
    {
        RestoreFromCache();

        var loops = new Task[_workers.Count];
        for (int i = 0; i < _workers.Count; i++)
        {
            ProviderWorker worker = _workers[i];
            loops[i] = Task.Run(() => RunLoopAsync(worker, ct), CancellationToken.None);
        }

        await Task.WhenAll(loops).ConfigureAwait(false);
    }

    private void RestoreFromCache()
    {
        IReadOnlyDictionary<string, UsageSnapshot> cached = _cache.Load();
        foreach (ProviderWorker worker in _workers)
        {
            if (!cached.TryGetValue(worker.Provider.Id, out UsageSnapshot? snapshot))
            {
                continue;
            }

            var view = new ProviderView(worker.Provider.Id, worker.Provider.DisplayName, snapshot, snapshot);
            worker.View = view;
            Publish(view);
        }
    }

    private async Task RunLoopAsync(ProviderWorker worker, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TaskCompletionSource? served;
            lock (worker.Gate)
            {
                served = worker.Pending;
                worker.Pending = null;
                worker.Wake = NewSignal();
                worker.IsFetching = true;
            }

            UsageSnapshot result = await FetchSafelyAsync(worker.Provider, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                served?.TrySetResult();
                return;
            }

            // Whatever happens while handling the result, the loop must keep running and the waiting request must complete.
            try
            {
                HandleResult(worker, result);
            }
            finally
            {
                served?.TrySetResult();
            }

            await WaitForNextFetchAsync(worker, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Merges, schedules, caches and publishes one fetch result. Never throws: a failure is logged and the worker
    /// falls back to the default interval, so the loop neither faults nor spins.
    /// </summary>
    private void HandleResult(ProviderWorker worker, UsageSnapshot result)
    {
        ProviderView view;
        try
        {
            // Apply arms the next timer before we publish: whoever observes ViewChanged sees a fully scheduled loop.
            view = Apply(worker, result);
        }
        catch (Exception ex)
        {
            _log.Error($"Handling the fetch result failed for provider '{worker.Provider.Id}'.", ex);
            lock (worker.Gate)
            {
                worker.IsFetching = false;
                ArmTimer(worker, DefaultRefreshInterval);
            }

            return;
        }

        if (result.Status == ProviderStatus.Ok)
        {
            try
            {
                SaveCache();
            }
            catch (Exception ex)
            {
                _log.Warn("Saving the snapshot cache failed.", ex);
            }
        }

        Publish(view);
    }

    /// <summary>
    /// Returns when the automatic timer fires, when a manual/path request arrives outside a rate-limit block,
    /// or when shutdown starts. A timer superseded by RescheduleAll completes with false; we then wait on the new one.
    /// </summary>
    private async Task WaitForNextFetchAsync(ProviderWorker worker, CancellationToken ct)
    {
        while (true)
        {
            Task<bool> due;
            Task wake;
            lock (worker.Gate)
            {
                due = worker.Due.Task;
                wake = worker.Wake.Task;
            }

            Task winner = await Task.WhenAny(due, wake, _stopping.Task).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (winner == due)
            {
                if (await due.ConfigureAwait(false)) // already completed: true = timer fired
                {
                    return;
                }

                continue; // Re-armed by RescheduleAll.
            }

            if (!IsBlocked(worker))
            {
                return;
            }

            // A request queued while the last fetch was running, but that fetch started a rate-limit block: drop it.
            lock (worker.Gate)
            {
                worker.Pending?.TrySetResult();
                worker.Pending = null;
                worker.Wake = NewSignal();
            }
        }
    }

    private async Task<UsageSnapshot> FetchSafelyAsync(IUsageProvider provider, CancellationToken ct)
    {
        try
        {
            UsageSnapshot? snapshot = await provider.FetchAsync(ct).ConfigureAwait(false);
            return snapshot ?? UnknownError(provider.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return UnknownError(provider.Id);
        }
        catch (Exception ex)
        {
            _log.Warn($"Fetch failed for provider '{provider.Id}'.", ex);
            return UnknownError(provider.Id);
        }
    }

    private UsageSnapshot UnknownError(string providerId) =>
        UsageSnapshot.WithStatus(providerId, ProviderStatus.Error, _time.GetUtcNow(), UnknownErrorMessage);

    /// <summary>Merges a fetch result, updates failure/block state and arms the next automatic timer.</summary>
    private ProviderView Apply(ProviderWorker worker, UsageSnapshot result)
    {
        IUsageProvider provider = worker.Provider;
        DateTimeOffset now = _time.GetUtcNow();
        TimeSpan configured = ConfiguredInterval();
        bool failed = result.Status is ProviderStatus.RateLimited or ProviderStatus.Error;

        lock (worker.Gate)
        {
            worker.ConsecutiveFailures = failed ? worker.ConsecutiveFailures + 1 : 0;
            (TimeSpan delay, TimeSpan policyDelay) = ComputeDelay(worker, result, configured);
            worker.BlockedUntil = result.Status == ProviderStatus.RateLimited ? now + policyDelay : null;
            worker.LastResult = result;
            worker.LastFetchAt = now;
            worker.IsFetching = false;
            ArmTimer(worker, delay);

            UsageSnapshot current = failed ? result with { RetryAfter = delay } : result;
            UsageSnapshot? lastGood = result.Status == ProviderStatus.Ok ? result : worker.View.LastGood;
            var view = new ProviderView(provider.Id, provider.DisplayName, current, lastGood);
            worker.View = view;
            return view;
        }
    }

    /// <summary>
    /// Rules 2–3: delay after <paramref name="result"/> with the worker's current failure count. Draws a new jitter value.
    /// Caller holds worker.Gate.
    /// </summary>
    private (TimeSpan Delay, TimeSpan PolicyDelay) ComputeDelay(ProviderWorker worker, UsageSnapshot result, TimeSpan configured)
    {
        IUsageProvider provider = worker.Provider;
        TimeSpan baseInterval = Max(configured, provider.MinInterval);
        TimeSpan policyDelay = _backoff.NextDelay(result, baseInterval, worker.ConsecutiveFailures);
        TimeSpan delay = BackoffPolicy.Jitter(policyDelay, _random());

        // RateLimited: never retry before the block ends, so only the upward half of the jitter applies.
        delay = result.Status == ProviderStatus.RateLimited
            ? Max(delay, policyDelay)
            : Max(delay, provider.MinInterval);

        return (Max(delay, MinimumDelay), policyDelay);
    }

    /// <summary>
    /// Replaces the worker's automatic timer. The previous Due signal completes with false (superseded);
    /// the new one completes with true when <paramref name="delay"/> elapses, or at once if it is not positive.
    /// Caller holds worker.Gate. Signals continue asynchronously, so no loop work runs on the caller's thread.
    /// </summary>
    private void ArmTimer(ProviderWorker worker, TimeSpan delay)
    {
        if (worker.IsShutDown)
        {
            return; // A late result after shutdown must not leave a timer behind.
        }

        worker.Timer?.Dispose();
        worker.Timer = null;
        worker.Due.TrySetResult(false);

        TaskCompletionSource<bool> due = NewDueSignal();
        worker.Due = due;
        if (delay <= TimeSpan.Zero)
        {
            due.TrySetResult(true);
            return;
        }

        // A callback already in flight from a disposed timer hits a completed signal and does nothing.
        worker.Timer = _time.CreateTimer(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            due, delay, Timeout.InfiniteTimeSpan);
    }

    private TimeSpan ConfiguredInterval()
    {
        try
        {
            TimeSpan configured = _refreshInterval();
            return configured > TimeSpan.Zero ? configured : DefaultRefreshInterval;
        }
        catch (Exception ex)
        {
            _log.Warn("Refresh interval callback failed; using the default.", ex);
            return DefaultRefreshInterval;
        }
    }

    private void SaveCache()
    {
        lock (_cacheGate)
        {
            var lastGood = new Dictionary<string, UsageSnapshot>(StringComparer.Ordinal);
            foreach (ProviderWorker worker in _workers)
            {
                if (worker.View.LastGood is { } snapshot)
                {
                    lastGood[worker.Provider.Id] = snapshot;
                }
            }

            _cache.Save(lastGood);
        }
    }

    private void Publish(ProviderView view)
    {
        EventHandler<ProviderView>? handlers = ViewChanged;
        if (handlers is null)
        {
            return;
        }

        // Invoke subscribers one by one so a throwing handler cannot starve the others or kill the loop.
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<ProviderView>)handler)(this, view);
            }
            catch (Exception ex)
            {
                _log.Warn("A ViewChanged handler threw.", ex);
            }
        }
    }

    private Task RequestFetch(ProviderWorker worker)
    {
        lock (worker.Gate)
        {
            if (worker.IsShutDown || IsBlocked(worker))
            {
                return Task.CompletedTask;
            }

            worker.Pending ??= NewSignal();
            worker.Wake.TrySetResult();
            return worker.Pending.Task;
        }
    }

    private bool IsBlocked(ProviderWorker worker)
    {
        lock (worker.Gate)
        {
            return worker.BlockedUntil is { } until && _time.GetUtcNow() < until;
        }
    }

    private void ScheduleDebouncedFetch(ProviderWorker worker)
    {
        CancellationToken token;
        lock (worker.Gate)
        {
            if (worker.IsShutDown)
            {
                return;
            }

            worker.DebounceCts?.Cancel();
            worker.DebounceCts?.Dispose();
            worker.DebounceCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            token = worker.DebounceCts.Token;
        }

        _ = DebounceThenFetchAsync(worker, token);
    }

    private async Task DebounceThenFetchAsync(ProviderWorker worker, CancellationToken token)
    {
        try
        {
            await Task.Delay(DebounceDelay, _time, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // Superseded by a newer change, or shutting down.
        }

        _ = RequestFetch(worker);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<bool> NewDueSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    private sealed class ProviderWorker(IUsageProvider provider, ProviderView initialView)
    {
        private ProviderView _view = initialView;

        public object Gate { get; } = new();
        public IUsageProvider Provider { get; } = provider;

        public ProviderView View
        {
            get { lock (Gate) { return _view; } }
            set { lock (Gate) { _view = value; } }
        }

        // The fields below are guarded by Gate.
        public int ConsecutiveFailures;
        public DateTimeOffset? BlockedUntil;
        public UsageSnapshot? LastResult;      // raw result of the last fetch (null until the first one)
        public DateTimeOffset LastFetchAt;
        public bool IsFetching;
        public TaskCompletionSource? Pending;
        public TaskCompletionSource Wake = NewSignal();
        public TaskCompletionSource<bool> Due = NewDueSignal();
        public ITimer? Timer;
        public CancellationTokenSource? DebounceCts;
        public bool IsShutDown;

        public void Shutdown()
        {
            lock (Gate)
            {
                IsShutDown = true;
                Timer?.Dispose();
                Timer = null;
                Due.TrySetResult(false);
                Pending?.TrySetResult();
                Pending = null;
                DebounceCts?.Cancel();
                DebounceCts?.Dispose();
                DebounceCts = null;
            }
        }
    }
}
