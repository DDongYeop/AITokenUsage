using AITokenUsage.Core.Models;
using AITokenUsage.Core.Polling;
using Xunit;

namespace AITokenUsage.Core.Tests.Fakes;

/// <summary>Records UsageService.ViewChanged events (raised on background threads) and lets tests await them in order.</summary>
public sealed class ViewRecorder
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly List<ProviderView> _views = [];
    private readonly SemaphoreSlim _available = new(0);
    private int _consumed;

    public ViewRecorder(UsageService service)
    {
        service.ViewChanged += (_, view) =>
        {
            lock (_gate)
            {
                _views.Add(view);
            }

            _available.Release();
        };
    }

    public int Count
    {
        get { lock (_gate) { return _views.Count; } }
    }

    /// <summary>Returns the next unconsumed event, waiting up to 5 s (real time) for it.</summary>
    public async Task<ProviderView> NextAsync()
    {
        if (!await _available.WaitAsync(DefaultTimeout))
        {
            throw new TimeoutException("ViewChanged was not raised within 5 seconds.");
        }

        lock (_gate)
        {
            return _views[_consumed++];
        }
    }

    /// <summary>Asserts that no further event arrives within <paramref name="milliseconds"/> of real time.</summary>
    public async Task AssertNoneAsync(int milliseconds = 300)
    {
        bool raised = await _available.WaitAsync(milliseconds);
        Assert.False(raised, "ViewChanged was raised unexpectedly.");
    }
}
