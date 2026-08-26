using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Time.Testing;
using NResilience.Extensions;
using NResilience.Http;
using NResilience.Testing;

namespace NResilience.IntegrationTests;

/// <summary>
///     Shared breaker and retry budget under concurrent admission.
///     <para>
///         The behavioural suite tests the breaker and budget via direct <c>TryEnter</c>/<c>Record</c> calls
///         and through single-threaded policy execution. These tests run many calls at once through one
///         policy with a shared guard and assert that the state transitions are not lost to a race - the
///         one thing a sequential test cannot catch.
///     </para>
/// </summary>
public sealed class ConcurrentCallTests
{
    /// <summary>
    ///     A shared breaker opens after enough failures even under concurrent admission. 16 calls through
    ///     one policy with a shared breaker, each call failing. The contract: the breaker opens, and at
    ///     least some of the later calls are <see cref="CallEventKind.RejectedByBreaker" /> - the failure count is not
    ///     lost to a race between concurrent admissions.
    /// </summary>
    [Fact]
    public async Task A_shared_breaker_opens_under_concurrent_failures()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(new BreakerSettings { ConsecutiveFailures = 5, Time = time });
        var events = new EventRecorder();

        var policy = TestPolicy.On(time) with
        {
            Breaker = breaker,
            Attempts = 1,
            OnEvent = events.Record,
        };

        var calls = Sequence.For<int>().Throws(new IOException("down"), 16);

        // The rejection delay is served against the policy's clock. A background pacer advances the
        // fake clock so rejections complete without the test having to interleave them by hand.
        using var pacer = time.StartPaceThread(TimeSpan.FromMilliseconds(10));

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => policy.TryRunAsync(ct => calls.NextAsync(ct)).AsTask())
            .ToList();

        await Task.WhenAll(tasks);

        Assert.Equal(BreakerState.Open, breaker.State);
        Assert.True(events.Contains(CallEventKind.RejectedByBreaker), "No call was rejected - the breaker never opened under load.");
    }

    /// <summary>
    ///     A retry budget throttles retries under concurrent admission. A budget with a small capacity,
    ///     many concurrent failing calls. The contract: the retry count is bounded by the budget, not by
    ///     the attempt cap - which is what makes a budget a storm preventer rather than a per-call limit.
    /// </summary>
    [Fact]
    public async Task A_retry_budget_throttles_retries_under_concurrent_admission()
    {
        var time = new FakeTimeProvider();
        var budget = RetryBudget.Shared("test-throttle", 0.10, 1);
        var events = new EventRecorder();

        var policy = TestPolicy.On(time) with
        {
            Budget = budget,
            Attempts = 5,
            OnEvent = events.Record,
        };

        // 8 concurrent calls, each failing. The budget is small; without it, 8 calls x 5 attempts
        // = 32 retries. With it, some retries are refused.
        var calls = Sequence.For<int>().Throws(new IOException("down"), 40);

        using var pacer = time.StartPaceThread(TimeSpan.FromMilliseconds(10));

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => policy.TryRunAsync(ct => calls.NextAsync(ct)).AsTask())
            .ToList();

        await Task.WhenAll(tasks);

        // A budget that is working refuses some retries. The assertion is that at least one
        // retry was refused - the exact count depends on timing and refill, but the presence of
        // any throttling is the claim.
        var rejections = events.CountOf(CallEventKind.RejectedByBudget);

        Assert.True(rejections > 0,
            $"Expected the budget to refuse some retries, but {events.Count} events were recorded and none were rejections. Events: {events}.");
    }

    /// <summary>
    ///     Per-host breaker scoping holds under concurrency. Two servers on different ports - one dead,
    ///     one healthy. N concurrent calls to each through one client. The contract: the dead host's
    ///     breaker opens while the healthy host's stays closed - the per-host state is not shared across
    ///     hosts, even under concurrent admission.
    /// </summary>
    [Fact]
    public async Task Per_host_breaker_scoping_holds_under_concurrency()
    {
        await using var deadServer = await LoopbackHttp.StartAsync(
            new LoopbackResponse(HttpStatusCode.ServiceUnavailable));

        await using var healthyServer = await LoopbackHttp.StartAsync(
            new LoopbackResponse(HttpStatusCode.OK, "ok"u8.ToArray()));

        var events = new EventRecorder();

        // No explicit breaker on the policy: the default BreakerPerHost scoping creates one per host,
        // which is what this test asserts. An explicit breaker would be shared across hosts.
        var handler = new ResilienceHandler(new SocketsHttpHandler(), Resilience.Http with
        {
            Backoff = Backoff.None,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Deadline = Timeout.InfiniteTimeSpan,
            OnEvent = events.Record,
        }, new HttpResilienceOptions { BreakerSettings = new BreakerSettings { ConsecutiveFailures = 3 } });

        using var client = new HttpClient(handler, true);

        // Enough concurrent calls to the dead host to trip its breaker.
        var deadTasks = Enumerable.Range(0, 10)
            .Select(_ => SafeGetAsync(client, deadServer.BaseUri))
            .ToList();

        // Concurrent calls to the healthy host. These must all succeed.
        var healthyTasks = Enumerable.Range(0, 10)
            .Select(_ => SafeGetAsync(client, healthyServer.BaseUri))
            .ToList();

        await Task.WhenAll(deadTasks.Concat(healthyTasks));

        // The dead host's breaker opened; the healthy host's stays closed. The keys are the
        // host:port authorities, which differ because the servers are on different ports.
        var breakers = handler.BreakersByHost();
        Assert.Equal(2, breakers.Count);

        var deadKey = breakers.Keys.Single(k => k.Contains(deadServer.Port.ToString(), StringComparison.Ordinal));
        var healthyKey = breakers.Keys.Single(k => k.Contains(healthyServer.Port.ToString(), StringComparison.Ordinal));

        Assert.Equal(BreakerState.Open, breakers[deadKey].State);
        Assert.Equal(BreakerState.Closed, breakers[healthyKey].State);

        // The healthy calls all succeeded - the dead host's breaker did not take out the healthy one.
        var healthyResults = healthyTasks.Select(t => t.Result).ToList();
        Assert.All(healthyResults, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }

    /// <summary>
    ///     The retry-fraction metric is consistent under contention. The same scenario as the
    ///     shared-breaker test, but with telemetry attached: the meter's attempt count matches the event
    ///     recorder's attempt count - the two surfaces agree even when calls are concurrent.
    /// </summary>
    [Fact]
    public async Task The_retry_fraction_metric_is_consistent_under_contention()
    {
        var time = new FakeTimeProvider();
        var events = new EventRecorder();

        var policy = (TestPolicy.On(time) with
        {
            Attempts = 3,
            OnEvent = events.Record,
        }).WithTelemetry();

        using var recording = new MeterRecording();

        var calls = Sequence.For<int>().Throws(new IOException("down"), 30);

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => policy.TryRunAsync(ct => calls.NextAsync(ct)).AsTask())
            .ToList();

        await Task.WhenAll(tasks);

        // The event recorder and the meter both counted the same attempts. The meter sees the whole
        // process, so filter by the policy name.
        var eventAttempts = events.CountOf(CallEventKind.Attempt);
        var meterAttempts = recording.AttemptsFor("(unnamed)");

        Assert.True(meterAttempts > 0, "The meter recorded no attempts.");
        Assert.Equal(eventAttempts, meterAttempts);
    }

    private static async Task<HttpResponseMessage> SafeGetAsync(HttpClient client, Uri uri)
    {
        try
        {
            return await client.GetAsync(uri);
        }
        catch (CallRejectedException)
        {
            // The breaker opened and refused the call. Return a placeholder so the task completes.
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }

    /// <summary>
    ///     Collects what the NResilience meter recorded, filtered by policy name. A trimmed version
    ///     of <c>MetricsTests.Recording</c> - enough to assert on the attempt counter.
    /// </summary>
    private sealed class MeterRecording : IDisposable
    {
        private readonly object _gate = new();
        private readonly MeterListener _listener = new();
        private readonly List<(string Instrument, double Value, Dictionary<string, object?> Tags)> _measurements = [];

        public MeterRecording()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, ResilienceTelemetry.Meter))
                    listener.EnableMeasurementEvents(instrument);
            };

            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();

        public long AttemptsFor(string policy)
        {
            lock (_gate)
            {
                return (long)_measurements
                    .Where(m => m.Instrument == "nresilience.attempts" && Equals(m.Tags.GetValueOrDefault("nresilience.policy"), policy))
                    .Sum(m => m.Value);
            }
        }

        private void Add<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
            where T : struct
        {
            var copied = new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (var tag in tags)
            {
                copied[tag.Key] = tag.Value;
            }

            lock (_gate)
            {
                _measurements.Add((instrument.Name, Convert.ToDouble(value, CultureInfo.InvariantCulture), copied));
            }
        }
    }
}
