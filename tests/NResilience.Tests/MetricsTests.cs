using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using NResilience.Extensions;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     The meter and the activity source.
///     <para>
///         The instrument set exists to produce one number: <c>attempts ÷ calls</c>, the retry fraction.
///         The tests that matter are therefore the ones that establish the denominator is right - one call
///         counted per logical operation, <i>including</i> the ones that failed, because a denominator that
///         counts only successes inflates the fraction exactly when it is being read in anger.
///     </para>
/// </summary>
public sealed class MetricsTests
{
    private static Resilience Named(string name) => (TestPolicy.Instant with { Name = name }).WithTelemetry();

    // ---- The retry fraction ----

    [Fact]
    public async Task A_successful_call_counts_one_call_and_one_attempt()
    {
        using var recording = new Recording();

        await Named("t-success").RunAsync(static ct => Task.FromResult(1));

        Assert.Equal(1, Calls(recording, "t-success"));
        Assert.Equal(1, Attempts(recording, "t-success"));
    }

    /// <summary>
    ///     Three attempts, one call. This is the whole point of splitting the counters: without it,
    ///     "we served 1,000 requests" and "we made 3,000 requests" are the same number and the retry
    ///     fraction cannot be computed at all.
    /// </summary>
    [Fact]
    public async Task Retries_count_attempts_without_counting_extra_calls()
    {
        using var recording = new Recording();
        var calls = 0;

        await (Named("t-retry") with { Attempts = 3 }).RunAsync(ct =>
            ++calls < 3 ? Task.FromException<int>(new IOException("flaky")) : Task.FromResult(1));

        Assert.Equal(1, Calls(recording, "t-retry"));
        Assert.Equal(3, Attempts(recording, "t-retry"));
    }

    /// <summary>
    ///     The denominator has to include the failures. A call that ran out of attempts is the most
    ///     interesting call in the process, and counting only the successes would understate the
    ///     denominator precisely when the fraction is being read in an incident.
    /// </summary>
    [Fact]
    public async Task A_call_that_exhausts_its_attempts_still_counts_as_one_call()
    {
        using var recording = new Recording();

        await (Named("t-exhausted") with { Attempts = 2 })
            .TryRunAsync(static ct => Task.FromException<int>(new IOException("down")));

        Assert.Equal(1, Calls(recording, "t-exhausted"));
        Assert.Equal(2, Attempts(recording, "t-exhausted"));
        Assert.Equal("attempts_exhausted", Outcome(recording, "t-exhausted"));
    }

    [Fact]
    public async Task A_permanent_failure_counts_one_call_tagged_permanent()
    {
        using var recording = new Recording();

        await Named("t-permanent")
            .TryRunAsync(static ct => Task.FromException<int>(new InvalidOperationException("no")));

        Assert.Equal(1, Calls(recording, "t-permanent"));
        Assert.Equal("permanent", Outcome(recording, "t-permanent"));
    }

    // ---- Rejections ----

    /// <summary>
    ///     A rejection is tagged with which guard refused, which is the difference between "the
    ///     dependency is down" and "we are retrying too hard" - two facts with opposite responses.
    /// </summary>
    [Fact]
    public async Task An_open_breaker_records_a_rejection_naming_the_dependency()
    {
        using var recording = new Recording();
        var breaker = new Breaker();
        breaker.Isolate();

        await (Named("t-breaker") with { Breaker = breaker })
            .TryRunAsync(static ct => Task.FromResult(1));

        // Filtered by policy: a MeterListener sees the whole process, and the suite runs in
        // parallel, so "the only rejection recorded" is not a claim a test can make.
        var rejection = Assert.Single(
            recording.TagsFor("nresilience.rejections"),
            tags => Equals(tags["nresilience.policy"], "t-breaker"));

        Assert.Equal("dependency_unavailable", rejection["nresilience.reason"]);

        // And it is still one logical call, so a rejected call does not vanish from the denominator.
        Assert.Equal(1, Calls(recording, "t-breaker"));
    }

    // ---- Durations ----

    [Fact]
    public async Task Both_durations_are_recorded_in_seconds()
    {
        using var recording = new Recording();

        await Named("t-duration").RunAsync(static ct => Task.FromResult(1));

        Assert.Single(recording.TagsFor("nresilience.call.duration"), t => Equals(t["nresilience.policy"], "t-duration"));
        Assert.Single(recording.TagsFor("nresilience.attempt.duration"), t => Equals(t["nresilience.policy"], "t-duration"));

        // Seconds, not milliseconds: a call this short must be well under a second either way, and
        // the unit on the instrument says "s".
        Assert.All(
            recording.Measurements.Where(m => m.Instrument.EndsWith("duration", StringComparison.Ordinal)),
            m => Assert.InRange(m.Value, 0, 10));
    }

    /// <summary>
    ///     The measured attempt ceiling reaches the meter, as a histogram recorded when it moves rather
    ///     than a gauge - which would need a registry of live policies that outlives them, the same
    ///     reason <c>nresilience.limiter.limit</c> is a histogram.
    /// </summary>
    [Fact]
    public async Task The_measured_attempt_ceiling_is_recorded_when_it_moves()
    {
        using var recording = new Recording();
        var time = new FakeTimeProvider();

        var policy = (TestPolicy.On(time) with
        {
            Name = "t-ceiling",
            Attempts = 1,
            AttemptTimeout = TimeSpan.FromSeconds(30),
            AttemptCeiling = AttemptCeiling.Above(3) with { Window = TimeSpan.FromHours(1) },
        }).WithTelemetry();

        for (var i = 0; i < 40; i++)
        {
            await policy.RunAsync(_ =>
            {
                time.Advance(TimeSpan.FromMilliseconds(100));
                return Task.FromResult(1);
            });
        }

        var recorded = recording.Measurements
            .Where(m => m.Instrument == "nresilience.attempt.timeout" && Equals(m.Tags["nresilience.policy"], "t-ceiling"))
            .ToList();

        // Once, when it moved - not forty times, once per attempt.
        var single = Assert.Single(recorded);

        // Seconds, matching the instrument's unit: three times a p95 of 100 ms.
        Assert.InRange(single.Value, 0.3, 0.34);
    }

    // ---- Tagging ----

    /// <summary>One listener serves every policy, so the policy name is what separates them.</summary>
    [Fact]
    public async Task Every_measurement_is_tagged_with_the_policy()
    {
        using var recording = new Recording();

        await Named("t-tagged").RunAsync(static ct => Task.FromResult(1));
        await Named("t-other").RunAsync(static ct => Task.FromResult(1));

        Assert.Equal(1, Calls(recording, "t-tagged"));
        Assert.Equal(1, Calls(recording, "t-other"));
    }

    // ---- Attaching ----

    /// <summary>Attaching twice attaches once, so a registration path that instruments defensively cannot double-count.</summary>
    [Fact]
    public async Task WithTelemetry_is_idempotent()
    {
        using var recording = new Recording();

        await Named("t-idempotent").WithTelemetry().WithTelemetry()
            .RunAsync(static ct => Task.FromResult(1));

        Assert.Equal(1, Calls(recording, "t-idempotent"));
    }

    /// <summary>
    ///     A listener the user already attached is kept. Installing metrics by silently dropping
    ///     somebody's logging would be the library preferring its own telemetry to theirs.
    /// </summary>
    [Fact]
    public async Task WithTelemetry_keeps_the_listener_that_was_already_there()
    {
        using var recording = new Recording();
        var seen = 0;

        var policy = (Resilience.Default with
        {
            Name = "t-chained",
            Backoff = Backoff.None,
            OnEvent = _ => Interlocked.Increment(ref seen),
        }).WithTelemetry();

        await policy.RunAsync(static ct => Task.FromResult(1));

        Assert.True(seen > 0);
        Assert.Equal(1, Calls(recording, "t-chained"));
    }

    // ---- Tracing ----

    /// <summary>
    ///     The span carries what happened inside it. A per-attempt HTTP span cannot say "these three
    ///     were one call that eventually succeeded"; this is where that is recorded.
    /// </summary>
    [Fact]
    public async Task A_call_annotates_the_current_activity()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ResilienceTelemetry.ActivitySourceName,
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
        };

        ActivitySource.AddActivityListener(listener);

        using var activity = ResilienceTelemetry.ActivitySource.StartActivity("test");
        Assert.NotNull(activity);

        var calls = 0;

        await (Named("t-trace") with { Attempts = 3 }).RunAsync(ct =>
            ++calls < 2 ? Task.FromException<int>(new IOException("flaky")) : Task.FromResult(1));

        Assert.Equal("succeeded", activity.GetTagItem("nresilience.outcome"));
        Assert.Equal(2, activity.GetTagItem("nresilience.attempts"));
        Assert.Equal(2, activity.Events.Count(e => e.Name == "nresilience.attempt"));
        Assert.Single(activity.Events, e => e.Name == "nresilience.retrying");
    }

    private static double Calls(Recording recording, string policy) =>
        recording.Measurements
            .Where(m => m.Instrument == "nresilience.calls" && Equals(m.Tags["nresilience.policy"], policy))
            .Sum(m => m.Value);

    private static double Attempts(Recording recording, string policy) =>
        recording.Measurements
            .Where(m => m.Instrument == "nresilience.attempts" && Equals(m.Tags["nresilience.policy"], policy))
            .Sum(m => m.Value);

    private static object? Outcome(Recording recording, string policy) =>
        recording.Measurements
            .Single(m => m.Instrument == "nresilience.calls" && Equals(m.Tags["nresilience.policy"], policy))
            .Tags["nresilience.outcome"];

    /// <summary>Collects what a named set of instruments recorded, with their tags.</summary>
    private sealed class Recording : IDisposable
    {
        private readonly object _gate = new();
        private readonly MeterListener _listener = new();
        private readonly List<(string Instrument, double Value, Dictionary<string, object?> Tags)> _measurements = [];

        public Recording()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, ResilienceTelemetry.Meter))
                    listener.EnableMeasurementEvents(instrument);
            };

            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.Start();
        }

        public IReadOnlyList<(string Instrument, double Value, Dictionary<string, object?> Tags)> Measurements
        {
            get
            {
                lock (_gate)
                {
                    return [.. _measurements];
                }
            }
        }

        public void Dispose() => _listener.Dispose();

        public double Total(string instrument) =>
            Measurements.Where(m => m.Instrument == instrument).Sum(m => m.Value);

        public IReadOnlyList<Dictionary<string, object?>> TagsFor(string instrument) =>
            [.. Measurements.Where(m => m.Instrument == instrument).Select(m => m.Tags)];

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
