using AITokenUsage.Core.Models;
using AITokenUsage.Core.Providers;

namespace AITokenUsage.Core.Tests.Fakes;

/// <summary>
/// Scripted provider for UsageService tests. Each FetchAsync call consumes the next scripted step;
/// once the script is exhausted the last step repeats (an empty script returns Ok with 0%).
/// </summary>
public sealed class FakeUsageProvider : IUsageProvider
{
    private readonly object _gate = new();
    private readonly Queue<Func<DateTimeOffset, UsageSnapshot>> _script = new();
    private readonly TimeProvider _time;
    private Func<DateTimeOffset, UsageSnapshot>? _last;
    private int _callCount;

    public FakeUsageProvider(string id, TimeProvider time, TimeSpan? minInterval = null, IReadOnlyList<string>? watchPaths = null)
    {
        Id = id;
        _time = time;
        MinInterval = minInterval ?? TimeSpan.FromMinutes(1);
        WatchPaths = watchPaths ?? [];
    }

    public string Id { get; }
    public string DisplayName => "Fake " + Id;
    public TimeSpan MinInterval { get; }
    public IReadOnlyList<string> WatchPaths { get; }
    public IReadOnlyList<MeterOption> StaticMeterOptions { get; } = [new MeterOption("session", "세션")];
    public int CallCount => Volatile.Read(ref _callCount);

    public static UsageSnapshot OkSnapshot(string providerId, double usedPercent, DateTimeOffset now, string plan = "Pro") =>
        new(providerId, ProviderStatus.Ok, plan,
            [new UsageMeter("session", "5h", "현재 세션 (5시간)", usedPercent, now.AddHours(3), Severity.Normal)],
            now);

    public FakeUsageProvider ThenOk(double usedPercent, string plan = "Pro") =>
        Enqueue(now => OkSnapshot(Id, usedPercent, now, plan));

    public FakeUsageProvider ThenStatus(ProviderStatus status, TimeSpan? retryAfter = null, string? message = null) =>
        Enqueue(now => UsageSnapshot.WithStatus(Id, status, now, message, retryAfter));

    public FakeUsageProvider ThenThrow(Exception exception) =>
        Enqueue(_ => throw exception);

    public Task<UsageSnapshot> FetchAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Func<DateTimeOffset, UsageSnapshot> step;
        lock (_gate)
        {
            if (_script.Count > 0)
            {
                _last = _script.Dequeue();
            }

            step = _last ?? (now => OkSnapshot(Id, 0, now));
        }

        Interlocked.Increment(ref _callCount);
        return Task.FromResult(step(_time.GetUtcNow()));
    }

    private FakeUsageProvider Enqueue(Func<DateTimeOffset, UsageSnapshot> step)
    {
        lock (_gate)
        {
            _script.Enqueue(step);
        }

        return this;
    }
}
