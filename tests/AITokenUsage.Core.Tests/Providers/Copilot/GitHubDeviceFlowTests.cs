using System.Net;
using System.Text;
using AITokenUsage.Core.Providers.Copilot;
using AITokenUsage.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace AITokenUsage.Core.Tests.Providers.Copilot;

public sealed class GitHubDeviceFlowTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);

    /// <summary>Real-time bound for "the code under test registers a timer"; exceeding it fails the test.</summary>
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(15);

    private const int MaxTimersPerTest = 50;

    private readonly FakeTimeProvider _time = new(Start);
    private readonly ObservedClock _clock;
    private readonly StubHttpHandler _http = new();
    private readonly List<DateTimeOffset> _pollTimes = [];
    private readonly List<string> _pollBodies = [];

    public GitHubDeviceFlowTests()
    {
        _clock = new ObservedClock(_time);
    }

    [Fact]
    public async Task Start_posts_form_with_client_and_scope_and_parses_the_code()
    {
        string? body = null, accept = null, contentType = null, userAgent = null;
        _http.When(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsoluteUri == GitHubDeviceFlow.DeviceCodeUrl, r =>
        {
            body = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            accept = r.Headers.Accept.ToString();
            contentType = r.Content.Headers.ContentType?.MediaType;
            userAgent = r.Headers.UserAgent.ToString();
            return Reply(HttpStatusCode.OK, Fixture.Read("copilot/device-code.json"));
        });

        var code = await CreateFlow().StartAsync(CancellationToken.None);

        Assert.Equal("test-device-code", code.DeviceCode);
        Assert.Equal("WDJB-MJHT", code.UserCode);
        Assert.Equal(new Uri("https://github.com/login/device"), code.VerificationUri);
        Assert.Equal(TimeSpan.FromSeconds(900), code.ExpiresIn);
        Assert.Equal(TimeSpan.FromSeconds(5), code.Interval);
        Assert.Equal("client_id=Iv1.b507a08c87ecfe98&scope=read%3Auser", body);
        Assert.Contains("application/json", accept);
        Assert.Equal("application/x-www-form-urlencoded", contentType);
        Assert.Equal(GitHubApi.UserAgent, userAgent);
        Assert.DoesNotContain("test-device-code", code.ToString());
    }

    [Fact]
    public async Task Start_reports_disabled_device_flow_from_a_200_body()
    {
        _http.When(r => r.RequestUri!.AbsoluteUri == GitHubDeviceFlow.DeviceCodeUrl,
            _ => Reply(HttpStatusCode.OK, Fixture.Read("copilot/device-code-disabled.json")));

        var ex = await Assert.ThrowsAsync<DeviceFlowException>(() => CreateFlow().StartAsync(CancellationToken.None));

        Assert.Equal(DeviceFlowError.Disabled, ex.Error);
    }

    [Fact]
    public async Task Start_network_failure_is_Network()
    {
        _http.When(r => r.RequestUri!.AbsoluteUri == GitHubDeviceFlow.DeviceCodeUrl,
            _ => throw new HttpRequestException("no route to host"));

        var ex = await Assert.ThrowsAsync<DeviceFlowException>(() => CreateFlow().StartAsync(CancellationToken.None));

        Assert.Equal(DeviceFlowError.Network, ex.Error);
    }

    [Theory]
    [InlineData("copilot/token-slow-down.json", 10)]              // server 10 s
    [InlineData("copilot/token-slow-down-interval-20.json", 20)]  // server 20 s is larger than previous + 5
    [InlineData("copilot/token-slow-down-no-interval.json", 10)]  // no server value: previous 5 s + 5 s
    public async Task Pending_then_slow_down_then_success_returns_the_token(string slowDown, int expectedGapAfterSlowDown)
    {
        GivenPollReplies("copilot/token-pending.json", slowDown, "copilot/token-success.json");

        var token = await DriveAsync(CreateFlow().WaitForTokenAsync(Code(), CancellationToken.None));

        Assert.Equal("ghu_test_device_token", token);
        Assert.Equal(3, _pollTimes.Count);
        Assert.Equal(TimeSpan.FromSeconds(5), _pollTimes[0] - Start);
        Assert.Equal(TimeSpan.FromSeconds(5), _pollTimes[1] - _pollTimes[0]);
        Assert.Equal(TimeSpan.FromSeconds(expectedGapAfterSlowDown), _pollTimes[2] - _pollTimes[1]);
        Assert.All(_pollBodies, b =>
        {
            Assert.Contains("client_id=Iv1.b507a08c87ecfe98", b);
            Assert.Contains("device_code=test-device-code", b);
            Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", b);
        });
    }

    [Fact]
    public async Task Repeated_slow_down_without_interval_adds_five_seconds_each_time()
    {
        GivenPollReplies(
            "copilot/token-pending.json",
            "copilot/token-slow-down-no-interval.json",
            "copilot/token-slow-down-no-interval.json",
            "copilot/token-success.json");

        var token = await DriveAsync(CreateFlow().WaitForTokenAsync(Code(), CancellationToken.None));

        Assert.Equal("ghu_test_device_token", token);
        // Delay used before each poll: 5 s (initial), 5 s (still 5 after pending), 10 s, 15 s.
        var times = new[] { Start }.Concat(_pollTimes).ToArray();
        var delays = times.Zip(times.Skip(1), (previous, next) => (next - previous).TotalSeconds).ToArray();
        Assert.Equal(new double[] { 5, 5, 10, 15 }, delays);
    }

    [Theory]
    [InlineData("copilot/token-expired.json", DeviceFlowError.Expired)]
    [InlineData("copilot/token-denied.json", DeviceFlowError.Denied)]
    [InlineData("copilot/token-incorrect-device-code.json", DeviceFlowError.Unknown)]
    public async Task Terminal_poll_errors_throw(string fixture, DeviceFlowError expected)
    {
        GivenPollReplies("copilot/token-pending.json", fixture);

        var ex = await Assert.ThrowsAsync<DeviceFlowException>(
            () => DriveAsync(CreateFlow().WaitForTokenAsync(Code(), CancellationToken.None)));

        Assert.Equal(expected, ex.Error);
        Assert.Equal(2, _pollTimes.Count);
        Assert.False(string.IsNullOrWhiteSpace(ex.UserMessage));
    }

    [Fact]
    public async Task Local_deadline_expires_while_still_pending()
    {
        GivenPollReplies("copilot/token-pending.json");

        var ex = await Assert.ThrowsAsync<DeviceFlowException>(
            () => DriveAsync(CreateFlow().WaitForTokenAsync(Code(expiresSeconds: 12), CancellationToken.None)));

        Assert.Equal(DeviceFlowError.Expired, ex.Error);
        Assert.Equal(2, _pollTimes.Count); // polls at 5 s and 10 s; the 15 s wake-up is past the 12 s deadline
    }

    [Fact]
    public async Task Three_consecutive_network_failures_throw_Network()
    {
        var attempts = 0;
        _http.When(r => r.RequestUri!.AbsoluteUri == GitHubDeviceFlow.AccessTokenUrl, _ =>
        {
            attempts++;
            throw new HttpRequestException("connection reset");
        });

        var ex = await Assert.ThrowsAsync<DeviceFlowException>(
            () => DriveAsync(CreateFlow().WaitForTokenAsync(Code(), CancellationToken.None)));

        Assert.Equal(DeviceFlowError.Network, ex.Error);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Cancellation_stops_polling()
    {
        GivenPollReplies("copilot/token-pending.json");
        using var cts = new CancellationTokenSource();
        var task = CreateFlow().WaitForTokenAsync(Code(), cts.Token);

        await AwaitTimerAsync();   // first wait
        AdvanceToNextTimer();      // first poll (pending)
        await AwaitTimerAsync();   // next wait is registered only after the poll was answered

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Single(_pollTimes);
    }

    private GitHubDeviceFlow CreateFlow() => new(new HttpClient(_http), _clock);

    private static DeviceCodeInfo Code(int expiresSeconds = 900, int intervalSeconds = 5) =>
        new("test-device-code", "WDJB-MJHT", new Uri("https://github.com/login/device"),
            TimeSpan.FromSeconds(expiresSeconds), TimeSpan.FromSeconds(intervalSeconds));

    /// <summary>Each poll takes the next fixture; the last one repeats.</summary>
    private void GivenPollReplies(params string[] fixtures)
    {
        var queue = new Queue<string>(fixtures);
        _http.When(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsoluteUri == GitHubDeviceFlow.AccessTokenUrl, r =>
        {
            _pollTimes.Add(_time.GetUtcNow());
            _pollBodies.Add(r.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var fixture = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            return Reply(HttpStatusCode.OK, Fixture.Read(fixture));
        });
    }

    /// <summary>
    /// Runs the flow to completion. Each iteration waits until the code under test has registered its next
    /// delay (no real-time race), then moves the fake clock to exactly that delay's due time. Fails the test if
    /// no delay appears within the safety timeout while the flow is still running.
    /// </summary>
    private async Task<T> DriveAsync<T>(Task<T> task)
    {
        for (var i = 0; i < MaxTimersPerTest && !task.IsCompleted; i++)
        {
            Task<bool> timerCreated = _clock.WaitForTimerAsync(SafetyTimeout);
            await Task.WhenAny(task, timerCreated);
            if (task.IsCompleted)
            {
                break;
            }

            Assert.True(await timerCreated, "The polling loop did not register a delay within the safety timeout.");
            AdvanceToNextTimer();
        }

        Assert.True(task.IsCompleted, "The polling loop kept registering delays without finishing.");
        return await task;
    }

    private async Task AwaitTimerAsync()
    {
        Assert.True(await _clock.WaitForTimerAsync(SafetyTimeout),
            "The polling loop did not register a delay within the safety timeout.");
    }

    private void AdvanceToNextTimer()
    {
        var due = _clock.DequeueDueTime();
        var now = _time.GetUtcNow();
        if (due > now)
        {
            _time.Advance(due - now);
        }
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>
    /// Test TimeProvider that delegates to the fake clock and signals every CreateTimer call, recording the
    /// timer's due time. Lets tests advance time exactly to the next delay instead of guessing with real sleeps.
    /// </summary>
    private sealed class ObservedClock(FakeTimeProvider inner) : TimeProvider
    {
        private readonly SemaphoreSlim _created = new(0);
        private readonly Queue<DateTimeOffset> _dueTimes = new();
        private readonly object _gate = new();

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            lock (_gate)
            {
                _dueTimes.Enqueue(inner.GetUtcNow() + dueTime);
            }

            _created.Release();
            return timer;
        }

        /// <summary>Completes with true when one more timer was created, or false after the real-time timeout.</summary>
        public Task<bool> WaitForTimerAsync(TimeSpan timeout) => _created.WaitAsync(timeout);

        public DateTimeOffset DequeueDueTime()
        {
            lock (_gate)
            {
                return _dueTimes.Dequeue();
            }
        }
    }
}
