using AITokenUsage.Core.Models;
using AITokenUsage.Core.Polling;
using AITokenUsage.Core.Providers;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AITokenUsage.Core.Tests.Polling;

/// <summary>
/// Timing rule for these tests: advance the fake clock only after the previous fetch was observed
/// (ViewRecorder.NextAsync or an awaited RefreshNowAsync), and never past more than one due time per Advance.
/// UsageService arms its next timer before raising ViewChanged, so this keeps every schedule deterministic.
/// </summary>
public sealed class UsageServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);
    private const string CredentialsPath = @"C:\Users\test\.claude\.credentials.json";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AITokenUsage.Tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(T0);
    private readonly List<UsageService> _services = [];
    private TimeSpan _refreshInterval = TimeSpan.FromMinutes(5);
    private double _random = 0.5;

    private string CachePath => Path.Combine(_dir, "cache.json");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var service in _services)
        {
            await service.DisposeAsync();
        }

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private UsageService CreateService(params IUsageProvider[] providers)
    {
        var service = new UsageService(providers, () => _refreshInterval, new SnapshotCache(CachePath, new NullLog()),
            _time, new NullLog(), () => _random);
        _services.Add(service);
        return service;
    }

    private FakeUsageProvider Provider(string id = "claude", TimeSpan? minInterval = null) =>
        new(id, _time, minInterval, [CredentialsPath]);

    [Fact]
    public void Constructor_ExposesLoadingViewsInRegistrationOrder()
    {
        var service = CreateService(Provider("codex"), Provider("claude"));

        Assert.Equal(new[] { "codex", "claude" }, service.Views.Select(v => v.ProviderId));
        var view = service.GetView("claude")!;
        Assert.Equal("Fake claude", view.DisplayName);
        Assert.Equal(ProviderStatus.Ok, view.Current.Status);
        Assert.Equal(UsageService.LoadingMessage, view.Current.StatusMessage);
        Assert.Null(view.LastGood);
        Assert.Null(service.GetView("gemini"));
    }

    [Fact]
    public void Constructor_DuplicateIds_Throws()
    {
        Assert.Throws<ArgumentException>(() => CreateService(Provider("claude"), Provider("claude")));
    }

    [Fact]
    public async Task Start_FetchesImmediately_ThenEveryInterval()
    {
        var provider = Provider().ThenOk(10).ThenOk(20).ThenOk(30);
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        var first = await views.NextAsync();
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(10, first.Current.Meters[0].UsedPercent);
        Assert.Same(first.Current, first.LastGood);

        _time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        await views.AssertNoneAsync();
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(20, (await views.NextAsync()).Current.Meters[0].UsedPercent);

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(30, (await views.NextAsync()).Current.Meters[0].UsedPercent);
        Assert.Equal(3, provider.CallCount);
    }

    [Fact]
    public async Task Interval_UsesProviderMinIntervalWhenLarger()
    {
        _refreshInterval = TimeSpan.FromMinutes(1);
        var provider = Provider(minInterval: TimeSpan.FromMinutes(2));
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        await views.AssertNoneAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        await views.NextAsync();
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task Interval_ReadsRefreshIntervalEveryCycle()
    {
        var provider = Provider();
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();
        _refreshInterval = TimeSpan.FromMinutes(10);
        _time.Advance(TimeSpan.FromMinutes(5));
        await views.NextAsync();

        _time.Advance(TimeSpan.FromMinutes(5));
        await views.AssertNoneAsync();
        _time.Advance(TimeSpan.FromMinutes(5));
        await views.NextAsync();
        Assert.Equal(3, provider.CallCount);
    }

    [Theory]
    [InlineData(0.0, 270)]
    [InlineData(1.0, 330)]
    public async Task Interval_AppliesJitter(double random, int expectedSeconds)
    {
        _random = random;
        var provider = Provider();
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();
        _time.Advance(TimeSpan.FromSeconds(expectedSeconds - 1));
        await views.AssertNoneAsync();
        _time.Advance(TimeSpan.FromSeconds(1));
        await views.NextAsync();
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task Interval_NegativeJitterNeverGoesBelowMinInterval()
    {
        _random = 0.0;
        _refreshInterval = TimeSpan.FromMinutes(2);
        var provider = Provider(minInterval: TimeSpan.FromMinutes(2));
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();
        _time.Advance(TimeSpan.FromSeconds(108)); // 0.9 x 2 min: too early
        await views.AssertNoneAsync();
        _time.Advance(TimeSpan.FromSeconds(12));
        await views.NextAsync();
    }

    [Fact]
    public async Task RescheduleAll_ShorterInterval_RearmsFromLastFetchTime()
    {
        _refreshInterval = TimeSpan.FromMinutes(15);
        var provider = Provider();
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();                  // fetched at T0, next automatic fetch at T0+15 min
        _time.Advance(TimeSpan.FromMinutes(1));
        await views.AssertNoneAsync();

        _refreshInterval = TimeSpan.FromMinutes(2);
        service.RescheduleAll();                  // re-armed for T0+2 min (last fetch + 2 min), not now+2 or T0+15

        _time.Advance(TimeSpan.FromSeconds(48));  // T0+1.8 min: too early
        await views.AssertNoneAsync();
        Assert.Equal(1, provider.CallCount);
        _time.Advance(TimeSpan.FromSeconds(24));  // T0+2.2 min
        await views.NextAsync();
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task RescheduleAll_DueTimeAlreadyPassed_FetchesImmediately()
    {
        _refreshInterval = TimeSpan.FromMinutes(15);
        var provider = Provider();
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();
        _time.Advance(TimeSpan.FromMinutes(5));
        await views.AssertNoneAsync();

        _refreshInterval = TimeSpan.FromMinutes(2);
        service.RescheduleAll();                  // T0+2 min is already past: fetch now without moving the clock
        await views.NextAsync();
        Assert.Equal(2, provider.CallCount);

        _time.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
        await views.AssertNoneAsync();
        _time.Advance(TimeSpan.FromSeconds(1));
        await views.NextAsync();
        Assert.Equal(3, provider.CallCount);
    }

    [Fact]
    public async Task RescheduleAll_RateLimitedProvider_IsNotMovedEarlier()
    {
        _random = 0.0;
        var provider = Provider().ThenStatus(ProviderStatus.RateLimited).ThenOk(1);
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        Assert.Equal(TimeSpan.FromMinutes(5), (await views.NextAsync()).Current.RetryAfter);

        _refreshInterval = TimeSpan.FromMinutes(1);
        service.RescheduleAll();

        _time.Advance(TimeSpan.FromMinutes(1));   // where a rescheduled 1-minute timer would fire
        await views.AssertNoneAsync();
        _time.Advance(TimeSpan.FromSeconds(210)); // T0+270 s: where a jitter-shortened block would end
        await views.AssertNoneAsync();
        Assert.Equal(1, provider.CallCount);
        _time.Advance(TimeSpan.FromSeconds(30));  // T0+5 min: the block ends
        Assert.Equal(ProviderStatus.Ok, (await views.NextAsync()).Current.Status);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task RescheduleAll_DuringInFlightFetch_DoesNotOverlapOrAddFetches()
    {
        var provider = new BlockingProvider("claude", _time);
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        await provider.WaitUntilFetchingAsync();
        provider.ReleaseOne();
        await views.NextAsync();                  // fetch #1 at T0, next at T0+5 min

        _time.Advance(TimeSpan.FromMinutes(5));
        await provider.WaitUntilFetchingAsync(); // fetch #2 is in flight at T0+5 min
        _refreshInterval = TimeSpan.FromMinutes(2);
        service.RescheduleAll();                  // T0 + 2 min is already past: a naive implementation would fetch again now
        service.RescheduleAll();
        await views.AssertNoneAsync();
        Assert.Equal(2, provider.CallCount);

        provider.ReleaseOne();
        await views.NextAsync();                  // fetch #2 published; it armed T0+5 min + 2 min itself
        await views.AssertNoneAsync();
        Assert.Equal(2, provider.CallCount);      // the reschedule calls did not queue an extra fetch

        _time.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
        await views.AssertNoneAsync();
        Assert.Equal(2, provider.CallCount);
        _time.Advance(TimeSpan.FromSeconds(1));
        await provider.WaitUntilFetchingAsync();
        provider.ReleaseOne();
        await views.NextAsync();
        Assert.Equal(3, provider.CallCount);
        Assert.Equal(1, provider.MaxConcurrentFetches);
    }

    [Fact]
    public async Task Error_BacksOffExponentially_AndOkResetsToBaseInterval()
    {
        var provider = Provider()
            .ThenStatus(ProviderStatus.Error, message: "네트워크 오류")
            .ThenStatus(ProviderStatus.Error, message: "네트워크 오류")
            .ThenOk(40)
            .ThenOk(41);
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        var first = await views.NextAsync();
        Assert.Equal(ProviderStatus.Error, first.Current.Status);
        Assert.Equal(TimeSpan.FromMinutes(5), first.Current.RetryAfter);

        _time.Advance(TimeSpan.FromMinutes(5));
        var second = await views.NextAsync();
        Assert.Equal(TimeSpan.FromMinutes(10), second.Current.RetryAfter);

        _time.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        await views.AssertNoneAsync();
        _time.Advance(TimeSpan.FromSeconds(1));
        var third = await views.NextAsync();
        Assert.Equal(ProviderStatus.Ok, third.Current.Status);
        Assert.Null(third.Current.RetryAfter);

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(41, (await views.NextAsync()).Current.Meters[0].UsedPercent);
    }

    [Fact]
    public async Task RateLimited_BlocksManualAndPathRefreshUntilBackoffElapses()
    {
        var provider = Provider()
            .ThenStatus(ProviderStatus.RateLimited, retryAfter: TimeSpan.Zero)
            .ThenOk(15)
            .ThenOk(16);
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        var limited = await views.NextAsync();
        Assert.Equal(ProviderStatus.RateLimited, limited.Current.Status);
        Assert.Equal(TimeSpan.FromMinutes(5), limited.Current.RetryAfter);

        await service.RefreshNowAsync();
        Assert.Equal(1, provider.CallCount);

        service.NotifyPathChanged(CredentialsPath);
        _time.Advance(UsageService.DebounceDelay);
        await views.AssertNoneAsync();
        Assert.Equal(1, provider.CallCount);

        _time.Advance(TimeSpan.FromMinutes(5) - UsageService.DebounceDelay);
        Assert.Equal(ProviderStatus.Ok, (await views.NextAsync()).Current.Status);

        await service.RefreshNowAsync();
        Assert.Equal(3, provider.CallCount);
    }

    [Fact]
    public async Task RateLimited_RepeatedBlocksDouble()
    {
        var provider = Provider().ThenStatus(ProviderStatus.RateLimited);
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        Assert.Equal(TimeSpan.FromMinutes(5), (await views.NextAsync()).Current.RetryAfter);
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.FromMinutes(10), (await views.NextAsync()).Current.RetryAfter);
        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(20), (await views.NextAsync()).Current.RetryAfter);
    }

    [Fact]
    public async Task RateLimited_NegativeJitterDoesNotShortenBlock()
    {
        _random = 0.0;
        var provider = Provider().ThenStatus(ProviderStatus.RateLimited).ThenOk(1);
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        Assert.Equal(TimeSpan.FromMinutes(5), (await views.NextAsync()).Current.RetryAfter);
        _time.Advance(TimeSpan.FromSeconds(270));
        await views.AssertNoneAsync();
        _time.Advance(TimeSpan.FromSeconds(30));
        await views.NextAsync();
    }

    [Fact]
    public async Task Merge_LastGoodChangesOnlyOnOk_CurrentIsAlwaysLatest()
    {
        var provider = Provider()
            .ThenOk(10)
            .ThenStatus(ProviderStatus.WaitingForCliRefresh, message: "갱신 대기")
            .ThenStatus(ProviderStatus.Error)
            .ThenOk(25);
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        var ok = await views.NextAsync();

        _time.Advance(TimeSpan.FromMinutes(5));
        var waiting = await views.NextAsync();
        Assert.Equal(ProviderStatus.WaitingForCliRefresh, waiting.Current.Status);
        Assert.Same(ok.Current, waiting.LastGood);
        Assert.True(waiting.IsStale);
        Assert.Equal(10, waiting.DisplayMeters[0].UsedPercent);

        _time.Advance(TimeSpan.FromMinutes(5));
        var error = await views.NextAsync();
        Assert.Equal(ProviderStatus.Error, error.Current.Status);
        Assert.Same(ok.Current, error.LastGood);

        _time.Advance(TimeSpan.FromMinutes(5));
        var recovered = await views.NextAsync();
        Assert.False(recovered.IsStale);
        Assert.Equal(25, recovered.LastGood!.Meters[0].UsedPercent);
        Assert.Same(recovered, service.GetView("claude"));
    }

    [Fact]
    public async Task ProviderException_BecomesErrorSnapshot_AndLoopKeepsRunning()
    {
        var provider = Provider()
            .ThenOk(10)
            .ThenThrow(new InvalidOperationException("boom"))
            .ThenOk(12);
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();
        _time.Advance(TimeSpan.FromMinutes(5));
        var failed = await views.NextAsync();
        Assert.Equal(ProviderStatus.Error, failed.Current.Status);
        Assert.Equal("알 수 없는 오류", failed.Current.StatusMessage);
        Assert.Equal(10, failed.LastGood!.Meters[0].UsedPercent);

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(ProviderStatus.Ok, (await views.NextAsync()).Current.Status);
    }

    [Fact]
    public async Task RefreshNow_FetchesAllProviders_AndRestartsTheirTimers()
    {
        var claude = Provider("claude");
        var codex = Provider("codex");
        var service = CreateService(claude, codex);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();
        await views.NextAsync();

        _time.Advance(TimeSpan.FromMinutes(2));
        await service.RefreshNowAsync();
        Assert.Equal(2, claude.CallCount);
        Assert.Equal(2, codex.CallCount);
        await views.NextAsync();
        await views.NextAsync();

        _time.Advance(TimeSpan.FromMinutes(3)); // T0+5: the old schedule, now cancelled
        await views.AssertNoneAsync();
        _time.Advance(TimeSpan.FromMinutes(2)); // T0+7 = refresh + 5 min
        await views.NextAsync();
        await views.NextAsync();
        Assert.Equal(3, claude.CallCount);
        Assert.Equal(3, codex.CallCount);
    }

    [Fact]
    public async Task RefreshNow_WithProviderId_FetchesOnlyThatProvider()
    {
        var claude = Provider("claude");
        var codex = Provider("codex");
        var service = CreateService(claude, codex);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();
        await views.NextAsync();
        await service.RefreshNowAsync("codex");

        Assert.Equal(1, claude.CallCount);
        Assert.Equal(2, codex.CallCount);
    }

    [Fact]
    public async Task RefreshNow_BeforeStart_IsNoOp()
    {
        var provider = Provider();
        var service = CreateService(provider);

        await service.RefreshNowAsync();

        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task NotifyPathChanged_MatchingPath_FetchesAfterDebounce()
    {
        var provider = new FakeUsageProvider("claude", _time, null, [@"C:\Users\test\.claude"]);
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();

        service.NotifyPathChanged(@"C:\USERS\TEST\.claude\.credentials.json");
        _time.Advance(TimeSpan.FromSeconds(1));
        await views.AssertNoneAsync();
        _time.Advance(TimeSpan.FromSeconds(0.5));
        await views.NextAsync();
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task NotifyPathChanged_BurstOfChanges_IsDebouncedIntoOneFetch()
    {
        var provider = Provider();
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();

        service.NotifyPathChanged(CredentialsPath);
        _time.Advance(TimeSpan.FromSeconds(1));
        service.NotifyPathChanged(CredentialsPath);
        _time.Advance(TimeSpan.FromSeconds(1));
        await views.AssertNoneAsync();
        _time.Advance(TimeSpan.FromSeconds(0.5));
        await views.NextAsync();
        _time.Advance(TimeSpan.FromSeconds(5));
        await views.AssertNoneAsync();
        Assert.Equal(2, provider.CallCount);
    }

    [Theory]
    [InlineData(@"C:\Users\test\.claude.json")]
    [InlineData(@"C:\Users\test\.claude\.credentials.json.bak")]
    [InlineData(@"C:\Users\test\.codex\auth.json")]
    public async Task NotifyPathChanged_UnrelatedPath_DoesNothing(string path)
    {
        var provider = Provider();
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();
        service.NotifyPathChanged(path);
        _time.Advance(TimeSpan.FromSeconds(2));

        await views.AssertNoneAsync();
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task Cache_IsSavedAfterOk_AndNotOverwrittenByFailures()
    {
        var provider = Provider().ThenOk(33).ThenStatus(ProviderStatus.Error);
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();
        var saved = new SnapshotCache(CachePath, new NullLog()).Load();
        Assert.Equal(33, saved["claude"].Meters[0].UsedPercent);

        _time.Advance(TimeSpan.FromMinutes(5));
        await views.NextAsync();
        var afterError = new SnapshotCache(CachePath, new NullLog()).Load();
        Assert.Equal(ProviderStatus.Ok, afterError["claude"].Status);
        Assert.Equal(33, afterError["claude"].Meters[0].UsedPercent);
    }

    [Fact]
    public async Task Start_RestoresCachedSnapshotBeforeFirstFetch()
    {
        var cachedAt = T0.AddHours(-1);
        new SnapshotCache(CachePath, new NullLog()).Save(new Dictionary<string, UsageSnapshot>
        {
            ["claude"] = FakeUsageProvider.OkSnapshot("claude", 77, cachedAt, "Max 5x"),
            ["retired-provider"] = FakeUsageProvider.OkSnapshot("retired-provider", 1, cachedAt),
        });
        var provider = Provider().ThenStatus(ProviderStatus.RateLimited);
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        var restored = await views.NextAsync();
        Assert.Equal(ProviderStatus.Ok, restored.Current.Status);
        Assert.Equal(cachedAt, restored.Current.FetchedAt);
        Assert.Equal(77, restored.LastGood!.Meters[0].UsedPercent);

        var limited = await views.NextAsync();
        Assert.Equal(ProviderStatus.RateLimited, limited.Current.Status);
        Assert.True(limited.IsStale);
        Assert.Equal(77, limited.DisplayMeters[0].UsedPercent);
        Assert.Equal("Max 5x", limited.DisplayPlan);
        Assert.Single(service.Views);
    }

    [Fact]
    public async Task ViewChangedHandlerException_DoesNotStopPolling()
    {
        var provider = Provider();
        var service = CreateService(provider);
        service.ViewChanged += (_, _) => throw new InvalidOperationException("handler bug");
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();
        _time.Advance(TimeSpan.FromMinutes(5));
        await views.NextAsync();
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task Dispose_StopsPolling_AndLaterCallsAreNoOps()
    {
        var provider = Provider();
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();
        await service.DisposeAsync();

        _time.Advance(TimeSpan.FromMinutes(30));
        await service.RefreshNowAsync();
        service.NotifyPathChanged(CredentialsPath);
        service.RescheduleAll();
        _time.Advance(TimeSpan.FromSeconds(2));

        await views.AssertNoneAsync();
        Assert.Equal(1, provider.CallCount);
        Assert.Throws<ObjectDisposedException>(() => service.Start());
    }

    [Fact]
    public async Task NonFiniteUsedPercent_DoesNotBreakCacheOrLoop()
    {
        var provider = Provider().ThenOk(double.NaN).ThenOk(double.PositiveInfinity).ThenOk(5);
        var service = CreateService(provider);
        var views = new ViewRecorder(service);

        service.Start();
        var nan = await views.NextAsync();
        Assert.True(double.IsNaN(nan.Current.Meters[0].UsedPercent!.Value));
        var saved = new SnapshotCache(CachePath, new NullLog()).Load();
        Assert.Null(saved["claude"].Meters[0].UsedPercent);

        await service.RefreshNowAsync().WaitAsync(TimeSpan.FromSeconds(5)); // must complete, not hang
        Assert.True(double.IsPositiveInfinity((await views.NextAsync()).Current.Meters[0].UsedPercent!.Value));
        Assert.Null(new SnapshotCache(CachePath, new NullLog()).Load()["claude"].Meters[0].UsedPercent);

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(5, (await views.NextAsync()).Current.Meters[0].UsedPercent);
        Assert.Equal(5, new SnapshotCache(CachePath, new NullLog()).Load()["claude"].Meters[0].UsedPercent);
        Assert.Equal(3, provider.CallCount);
    }

    [Fact]
    public async Task ResultHandlingFailure_IsLogged_CompletesRequest_AndLoopContinues()
    {
        // The second jitter draw (inside Apply for the manual refresh) throws; every other draw is fine.
        int draws = 0;
        double NextRandom() => Interlocked.Increment(ref draws) == 2 ? throw new InvalidOperationException("boom") : 0.5;
        var provider = Provider();
        var service = new UsageService([provider], () => _refreshInterval, new SnapshotCache(CachePath, new NullLog()),
            _time, new NullLog(), NextRandom);
        _services.Add(service);
        var views = new ViewRecorder(service);

        service.Start();
        await views.NextAsync();                                                // fetch #1 published normally

        await service.RefreshNowAsync().WaitAsync(TimeSpan.FromSeconds(5));     // fetch #2: Apply throws; request still completes
        await views.AssertNoneAsync();
        Assert.Equal(2, provider.CallCount);                                    // no hot retry loop

        _time.Advance(UsageService.DefaultRefreshInterval);                     // fallback timer armed after the failure
        await views.NextAsync();
        Assert.Equal(3, provider.CallCount);

        await service.RefreshNowAsync().WaitAsync(TimeSpan.FromSeconds(5));     // loop is alive and serving requests
        await views.NextAsync();
        Assert.Equal(4, provider.CallCount);
    }

    /// <summary>
    /// Provider whose every fetch waits until the test calls ReleaseOne, so a test can act while a fetch is in flight.
    /// Records the highest number of fetches that were in flight at the same time.
    /// </summary>
    private sealed class BlockingProvider(string id, TimeProvider time) : IUsageProvider
    {
        private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
        private readonly object _gate = new();
        private readonly SemaphoreSlim _entered = new(0);
        private readonly SemaphoreSlim _release = new(0);
        private int _inFlight;
        private int _maxInFlight;
        private int _callCount;

        public string Id { get; } = id;
        public string DisplayName => "Blocking " + Id;
        public TimeSpan MinInterval => TimeSpan.FromMinutes(1);
        public IReadOnlyList<string> WatchPaths { get; } = [];
        public IReadOnlyList<MeterOption> StaticMeterOptions { get; } = [];

        public int CallCount
        {
            get { lock (_gate) { return _callCount; } }
        }

        public int MaxConcurrentFetches
        {
            get { lock (_gate) { return _maxInFlight; } }
        }

        public async Task<UsageSnapshot> FetchAsync(CancellationToken ct)
        {
            lock (_gate)
            {
                _callCount++;
                _inFlight++;
                _maxInFlight = Math.Max(_maxInFlight, _inFlight);
            }

            _entered.Release();
            try
            {
                await _release.WaitAsync(ct);
                return FakeUsageProvider.OkSnapshot(Id, 0, time.GetUtcNow());
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight--;
                }
            }
        }

        /// <summary>Waits (real time, up to 5 s) until the next fetch has started.</summary>
        public async Task WaitUntilFetchingAsync()
        {
            if (!await _entered.WaitAsync(WaitTimeout))
            {
                throw new TimeoutException("FetchAsync was not called within 5 seconds.");
            }
        }

        public void ReleaseOne() => _release.Release();
    }
}
