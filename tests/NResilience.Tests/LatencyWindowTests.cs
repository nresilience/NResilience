using Microsoft.Extensions.Time.Testing;
using NResilience.Internal;

namespace NResilience.Tests;

/// <summary>
///     The quantile estimator. Nothing consumes it yet: it lands on its own so its behavior can be
///     argued about separately from hedging and from the adaptive slow-call threshold, both of which
///     are built on it.
/// </summary>
public sealed class LatencyWindowTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(4);

    /// <summary>The window is divided into four slices, so this is one of them.</summary>
    private static readonly TimeSpan Slice = TimeSpan.FromSeconds(1);

    [Fact]
    public void A_cold_window_has_no_opinion()
    {
        var window = New(out _);

        Assert.Null(window.Threshold(minimumSamples: 20));
        Assert.Equal(0, window.Samples);
    }

    /// <summary>
    ///     The memoized answer is per slice, so a window that crosses the minimum has to notice within
    ///     the slice rather than at the end of it - which is what a cold process starting up does.
    /// </summary>
    [Fact]
    public void Crossing_the_minimum_is_noticed_without_waiting_for_the_slice_to_roll()
    {
        var window = New(out _);

        Record(window, TimeSpan.FromMilliseconds(10), times: 19);
        Assert.Null(window.Threshold(minimumSamples: 20));

        window.Record(TimeSpan.FromMilliseconds(10));
        Assert.NotNull(window.Threshold(minimumSamples: 20));
    }

    [Fact]
    public void A_uniform_distribution_reports_the_value_it_is_made_of()
    {
        var window = New(out _);
        Record(window, TimeSpan.FromMilliseconds(100), times: 1_000);

        var threshold = window.Threshold(minimumSamples: 20);

        Assert.NotNull(threshold);
        Assert.InRange(threshold.Value, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(112.5));
    }

    /// <summary>
    ///     The property the whole thing exists for: the answer separates the body of the distribution
    ///     from its tail, so a threshold set at one quantile does not accidentally sit in the other.
    /// </summary>
    [Fact]
    public void The_quantile_tells_the_body_from_the_tail()
    {
        var p95 = New(out var time, quantile: 0.95);
        var p99 = new LatencyWindow(quantile: 0.99, Window, time);

        foreach (var window in new[] { p95, p99 })
        {
            Record(window, TimeSpan.FromMilliseconds(10), times: 95);
            Record(window, TimeSpan.FromSeconds(1), times: 5);
        }

        // 95 of 100 samples are 10 ms, so the 95th is one of them and the 99th is in the tail.
        Assert.InRange(p95.Threshold(minimumSamples: 20)!.Value, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(11.25));
        Assert.InRange(p99.Threshold(minimumSamples: 20)!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.125));
    }

    /// <summary>
    ///     The direction of the error is the point. A threshold that is a little high hedges slightly
    ///     less often than asked; one that is a little low hedges more, and more load is the failure
    ///     mode this estimator exists to avoid.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(100)]
    [InlineData(999)]
    [InlineData(1_000)]
    [InlineData(50_000)]
    [InlineData(1_000_000)]
    [InlineData(30_000_000)]
    public void The_answer_is_never_below_the_value_and_never_more_than_one_bucket_above(long micros)
    {
        var value = TimeSpan.FromTicks(micros * TimeSpan.TicksPerMicrosecond);
        var window = New(out _);

        Record(window, value, times: 50);
        var threshold = window.Threshold(minimumSamples: 1);

        Assert.NotNull(threshold);
        Assert.True(threshold.Value >= value, $"{threshold} was below {value}");

        // A bucket is at most an eighth of its own lower bound wide, plus the microsecond of
        // truncation the linear region below 8 µs costs.
        var ceiling = TimeSpan.FromTicks((long)(value.Ticks * 1.125) + TimeSpan.TicksPerMicrosecond);
        Assert.True(threshold.Value <= ceiling, $"{threshold} was more than one bucket above {value}");
    }

    [Fact]
    public void Samples_age_out_when_the_window_has_passed()
    {
        var window = New(out var time);
        Record(window, TimeSpan.FromMilliseconds(10), times: 100);

        Assert.NotNull(window.Threshold(minimumSamples: 20));

        time.Advance(Window);

        Assert.Equal(0, window.Samples);
        Assert.Null(window.Threshold(minimumSamples: 20));
    }

    [Fact]
    public void Most_of_the_window_survives_one_slice_rolling()
    {
        var window = New(out var time);

        // One slice's worth of traffic, four times over, advancing between each.
        for (var slice = 0; slice < 4; slice++)
        {
            Record(window, TimeSpan.FromMilliseconds(10), times: 100);
            time.Advance(Slice);
        }

        // The oldest slice has now aged out and the other three have not.
        Assert.Equal(300, window.Samples);
    }

    [Fact]
    public void The_estimate_follows_a_dependency_that_slows_down()
    {
        var window = New(out var time);

        Record(window, TimeSpan.FromMilliseconds(10), times: 500);
        Assert.InRange(window.Threshold(minimumSamples: 20)!.Value, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(11.25));

        // A full window later, none of the fast traffic is still counted.
        time.Advance(Window);
        Record(window, TimeSpan.FromMilliseconds(100), times: 500);

        Assert.InRange(window.Threshold(minimumSamples: 20)!.Value, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(112.5));
    }

    /// <summary>
    ///     A brownout is the case the design turns on: when everything slows by the same factor, the
    ///     quantile moves with it, so the fraction of calls above the threshold does not change. That
    ///     is what stops a hedge from firing on every call the moment a dependency degrades.
    /// </summary>
    [Fact]
    public void A_uniform_slowdown_moves_the_threshold_rather_than_the_fraction_above_it()
    {
        var fast = New(out var time);
        var slow = new LatencyWindow(quantile: 0.95, Window, time);

        for (var i = 0; i < 1_000; i++)
        {
            // The same shape, ten times slower.
            var value = TimeSpan.FromMilliseconds(i % 100 < 95 ? 10 : 200);
            fast.Record(value);
            slow.Record(value * 10);
        }

        var ratio = slow.Threshold(minimumSamples: 20)!.Value / fast.Threshold(minimumSamples: 20)!.Value;

        Assert.InRange(ratio, 9.0, 11.0);
    }

    [Fact]
    public void A_duration_past_the_top_bucket_is_clamped_rather_than_thrown()
    {
        var window = New(out _);
        Record(window, TimeSpan.FromHours(1), times: 50);

        var threshold = window.Threshold(minimumSamples: 1);

        // Clamped into the top bucket, which is the one case the answer comes out below the value.
        Assert.NotNull(threshold);
        Assert.InRange(threshold.Value, TimeSpan.FromSeconds(134), TimeSpan.FromSeconds(269));
    }

    [Fact]
    public void A_negative_duration_is_ignored_and_zero_is_counted()
    {
        var window = New(out _);

        window.Record(TimeSpan.FromSeconds(-1));
        Assert.Equal(0, window.Samples);

        window.Record(TimeSpan.Zero);
        Assert.Equal(1, window.Samples);
        Assert.Equal(TimeSpan.FromTicks(TimeSpan.TicksPerMicrosecond), window.Threshold(minimumSamples: 1));
    }

    [Fact]
    public void Concurrent_records_are_all_counted()
    {
        var window = New(out _);

        // One sequential record first, so the ring is claimed before the threads arrive: a sample
        // that lands while another thread is clearing a ring for a new slice is lost by design, and
        // the first record of a slice is the only place that race exists.
        window.Record(TimeSpan.FromMilliseconds(10));

        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < 1_000; i++)
                window.Record(TimeSpan.FromMilliseconds(10));
        });

        Assert.Equal(8_001, window.Samples);
    }

    [Fact]
    public void A_quantile_or_window_that_cannot_work_is_refused()
    {
        var time = new FakeTimeProvider();

        Assert.Throws<ArgumentOutOfRangeException>(() => new LatencyWindow(quantile: 0, Window, time));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LatencyWindow(quantile: 1, Window, time));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LatencyWindow(quantile: double.NaN, Window, time));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LatencyWindow(quantile: 0.95, TimeSpan.Zero, time));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LatencyWindow(quantile: 0.95, TimeSpan.FromSeconds(-1), time));
        Assert.Throws<ArgumentNullException>(() => new LatencyWindow(quantile: 0.95, Window, null!));
    }

    [Fact]
    public void An_idle_window_costs_nothing_and_says_nothing()
    {
        var window = New(out var time);

        // Ten windows of nothing at all. Rings are cleared on write, so no timer ever runs.
        time.Advance(Window * 10);

        Assert.Equal(0, window.Samples);
        Assert.Null(window.Threshold(minimumSamples: 1));

        window.Record(TimeSpan.FromMilliseconds(10));
        Assert.Equal(1, window.Samples);
    }

    /// <summary>
    ///     The breaker asks both questions about every attempt it samples, so it asks them together and
    ///     reads the clock once. The combined form has to be the two separate ones and nothing else.
    /// </summary>
    [Fact]
    public void Recording_and_reading_together_answers_what_the_two_calls_answer()
    {
        var combined = New(out _);
        var separate = New(out _);

        for (var i = 1; i <= 500; i++)
        {
            var duration = TimeSpan.FromMilliseconds(i % 50);

            separate.Record(duration);

            Assert.Equal(separate.Threshold(minimumSamples: 20), combined.RecordAndThreshold(duration, minimumSamples: 20));
        }

        Assert.Equal(separate.Samples, combined.Samples);
    }

    private static LatencyWindow New(out FakeTimeProvider time, double quantile = 0.95)
    {
        time = new FakeTimeProvider();
        return new LatencyWindow(quantile, Window, time);
    }

    private static void Record(LatencyWindow window, TimeSpan duration, int times)
    {
        for (var i = 0; i < times; i++)
            window.Record(duration);
    }
}
