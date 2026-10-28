using AITokenUsage.Core.Models;

namespace AITokenUsage.Core.Rotation;

/// <summary>
/// Decides which provider the widget shows. Not thread-safe: call every member from the UI thread
/// (the app's 250 ms DispatcherTimer calls <see cref="OnTick"/>).
/// </summary>
public sealed class RotationController
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time;
    private IReadOnlyList<ProviderView> _views = [];
    private IReadOnlyList<string> _order = [];
    private string[] _eligible = [];
    private bool _enabled = true;
    private TimeSpan _interval = DefaultInterval;
    private bool _skipUnavailable = true;
    private bool _hoverPaused;
    private DateTimeOffset _cycleStart;

    public RotationController(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _cycleStart = time.GetUtcNow();
    }

    public event EventHandler<RotationChangedEventArgs>? CurrentChanged;

    public IReadOnlyList<string> EligibleIds => _eligible;

    public string? CurrentId { get; private set; }

    /// <summary>Index of <see cref="CurrentId"/> in <see cref="EligibleIds"/>, or -1 when there is none.</summary>
    public int CurrentIndex => CurrentId is null ? -1 : Array.IndexOf(_eligible, CurrentId);

    public bool IsUserPaused { get; private set; }

    /// <summary>Applies rotation settings, re-evaluates eligibility with the last provider list, and restarts the timer.</summary>
    public void Configure(bool enabled, TimeSpan interval, bool skipUnavailable)
    {
        _enabled = enabled;
        _interval = interval < MinInterval ? MinInterval : interval;
        _skipUnavailable = skipUnavailable;
        Recompute(_views);
        ResetTimer();
    }

    /// <summary>
    /// Replaces the provider list (enabled providers in settings order). Keeps the current provider when it is still
    /// eligible; otherwise moves to the next eligible provider after it in the previous order and raises
    /// CurrentChanged with Direction 0.
    /// </summary>
    public void UpdateProviders(IReadOnlyList<ProviderView> enabledInOrder)
    {
        ArgumentNullException.ThrowIfNull(enabledInOrder);
        Recompute(enabledInOrder.ToArray());
    }

    /// <summary>Advances to the next provider once the interval has elapsed and nothing pauses rotation.</summary>
    public void OnTick()
    {
        DateTimeOffset now = _time.GetUtcNow();
        if (!_enabled || _hoverPaused || IsUserPaused || _eligible.Length < 2)
        {
            // While rotation cannot run, keep the cycle anchored to "now" so it restarts from zero when it can.
            _cycleStart = now;
            return;
        }

        if (now - _cycleStart >= _interval)
        {
            Move(+1);
        }
    }

    /// <summary>Manual "next" (click, wheel down). Works even when automatic rotation is disabled or paused.</summary>
    public void Next() => Move(+1);

    /// <summary>Manual "previous" (wheel up).</summary>
    public void Previous() => Move(-1);

    /// <summary>The app calls this only when the PauseOnHover setting is on. Releasing restarts the timer.</summary>
    public void SetHoverPaused(bool paused)
    {
        _hoverPaused = paused;
        if (!paused)
        {
            ResetTimer();
        }
    }

    /// <summary>"자동 전환 일시정지" menu item. Not persisted. Releasing restarts the timer.</summary>
    public void SetUserPaused(bool paused)
    {
        IsUserPaused = paused;
        if (!paused)
        {
            ResetTimer();
        }
    }

    private void Move(int direction)
    {
        ResetTimer();
        if (_eligible.Length < 2)
        {
            return;
        }

        int index = CurrentIndex;
        int next = index < 0
            ? 0
            : ((index + direction) % _eligible.Length + _eligible.Length) % _eligible.Length;
        SetCurrent(_eligible[next], direction);
    }

    private void Recompute(IReadOnlyList<ProviderView> enabledInOrder)
    {
        IReadOnlyList<string> previousOrder = _order;
        string? previousId = CurrentId;

        _views = enabledInOrder;
        _order = enabledInOrder.Select(v => v.ProviderId).ToArray();
        _eligible = ComputeEligible(enabledInOrder);

        string? nextId = previousId is not null && _eligible.Contains(previousId)
            ? previousId
            : PickReplacement(previousId, previousOrder);

        if (!string.Equals(nextId, previousId, StringComparison.Ordinal))
        {
            ResetTimer();
            SetCurrent(nextId, 0);
        }
    }

    private string[] ComputeEligible(IReadOnlyList<ProviderView> enabledInOrder)
    {
        if (enabledInOrder.Count == 0)
        {
            return [];
        }

        string[] eligible = enabledInOrder
            .Where(v => !_skipUnavailable || !IsUnavailable(v))
            .Select(v => v.ProviderId)
            .ToArray();

        // Nothing eligible: show the first enabled provider so its status message is visible.
        return eligible.Length > 0 ? eligible : [enabledInOrder[0].ProviderId];
    }

    private static bool IsUnavailable(ProviderView view) =>
        view.LastGood is null
        && view.Current.Status is ProviderStatus.NotInstalled or ProviderStatus.SignedOut or ProviderStatus.Unsupported;

    /// <summary>The first eligible id after <paramref name="previousId"/> in the previous order (wrapping), else the first eligible.</summary>
    private string? PickReplacement(string? previousId, IReadOnlyList<string> previousOrder)
    {
        if (_eligible.Length == 0)
        {
            return null;
        }

        int start = previousId is null ? -1 : IndexOf(previousOrder, previousId);
        if (start >= 0)
        {
            for (int step = 1; step < previousOrder.Count; step++)
            {
                string candidate = previousOrder[(start + step) % previousOrder.Count];
                if (_eligible.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        return _eligible[0];
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private void SetCurrent(string? id, int direction)
    {
        if (string.Equals(id, CurrentId, StringComparison.Ordinal))
        {
            return;
        }

        CurrentId = id;
        CurrentChanged?.Invoke(this, new RotationChangedEventArgs(id, direction));
    }

    private void ResetTimer() => _cycleStart = _time.GetUtcNow();
}
