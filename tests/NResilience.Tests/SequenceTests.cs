using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     The scripted callback. What it serves, in what order, and what it does when the script and the
///     policy disagree about how many calls there will be.
/// </summary>
public sealed class SequenceTests
{
    [Fact]
    public async Task Steps_are_served_in_order()
    {
        var calls = Sequence.For<int>().Returns(1).Returns(2).Returns(3);

        Assert.Equal(1, await calls.NextAsync());
        Assert.Equal(2, await calls.NextAsync());
        Assert.Equal(3, await calls.NextAsync());
    }

    [Fact]
    public async Task Throws_throws_the_instance_it_was_given()
    {
        var boom = new TimeoutException();
        var calls = Sequence.For<int>().Throws(boom).Returns(7);

        Assert.Same(boom, await Assert.ThrowsAsync<TimeoutException>(() => calls.NextAsync()));
        Assert.Equal(7, await calls.NextAsync());
    }

    [Fact]
    public async Task Counted_overloads_repeat_a_step()
    {
        var calls = Sequence.For<int>().Throws(new TimeoutException(), 2).Returns(9, 2);

        await Assert.ThrowsAsync<TimeoutException>(() => calls.NextAsync());
        await Assert.ThrowsAsync<TimeoutException>(() => calls.NextAsync());
        Assert.Equal(9, await calls.NextAsync());
        Assert.Equal(9, await calls.NextAsync());
        Assert.Equal(0, calls.Remaining);
    }

    [Fact]
    public async Task CallCount_counts_every_call_including_the_one_that_ran_off_the_end()
    {
        var calls = Sequence.For<int>().Returns(1);

        Assert.Equal(0, calls.CallCount);
        await calls.NextAsync();
        Assert.Equal(1, calls.CallCount);

        await Assert.ThrowsAsync<InvalidOperationException>(() => calls.NextAsync());
        Assert.Equal(2, calls.CallCount);
    }

    [Fact]
    public async Task Running_out_of_script_says_how_many_steps_there_were()
    {
        var calls = Sequence.For<int>().Returns(1).Returns(2);

        await calls.NextAsync();
        await calls.NextAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => calls.NextAsync());

        Assert.Contains("2 step(s)", error.Message, StringComparison.Ordinal);
        Assert.Contains("call 3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_trailing_Delays_is_named_as_the_scripting_mistake_it_is()
    {
        var calls = Sequence.For<int>().Returns(1).Delays(TimeSpan.FromSeconds(1));

        await calls.NextAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => calls.NextAsync());

        Assert.Contains("trailing Delays()", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_with_no_delay_completes_synchronously()
    {
        var calls = Sequence.For<int>().Returns(1);

        Assert.True(calls.NextAsync().IsCompleted);
    }

    [Fact]
    public async Task Delays_is_served_against_the_supplied_clock()
    {
        var time = new FakeTimeProvider();
        var calls = Sequence.For<int>(time).Delays(TimeSpan.FromSeconds(5)).Returns(1);

        var pending = calls.NextAsync();
        Assert.False(pending.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(1, await pending);
    }

    [Fact]
    public async Task Repeated_Delays_accumulate_onto_the_next_step()
    {
        var time = new FakeTimeProvider();

        var calls = Sequence.For<int>(time)
            .Delays(TimeSpan.FromSeconds(2))
            .Delays(TimeSpan.FromSeconds(3))
            .Returns(1);

        var pending = calls.NextAsync();

        time.Advance(TimeSpan.FromSeconds(4));
        Assert.False(pending.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await pending);
    }

    [Fact]
    public async Task A_delay_applies_only_to_the_step_it_precedes()
    {
        var time = new FakeTimeProvider();
        var calls = Sequence.For<int>(time).Delays(TimeSpan.FromSeconds(5)).Returns(1).Returns(2);

        var pending = calls.NextAsync();
        time.Advance(TimeSpan.FromSeconds(5));
        await pending;

        Assert.True(calls.NextAsync().IsCompleted);
    }

    [Fact]
    public async Task A_delayed_step_honors_the_cancellation_token()
    {
        var time = new FakeTimeProvider();
        var calls = Sequence.For<int>(time).Delays(TimeSpan.FromSeconds(5)).Returns(1);
        using var cts = new CancellationTokenSource();

        var pending = calls.NextAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public void Negative_delays_and_counts_are_rejected_where_they_are_written()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Sequence.For<int>().Delays(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Sequence.For<int>().Returns(1, -1));
        Assert.Throws<ArgumentNullException>(() => Sequence.For<int>().Throws(null!));
    }

    [Fact]
    public async Task Concurrent_callers_each_get_a_distinct_step()
    {
        var calls = Sequence.For<int>();

        for (var i = 0; i < 64; i++)
        {
            calls.Returns(i);
        }

        var served = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => calls.NextAsync())));

        Assert.Equal(Enumerable.Range(0, 64), served.Order());
    }

    [Fact]
    public async Task A_void_sequence_scripts_the_void_overloads()
    {
        var calls = Sequence.ForVoid().Throws(new TimeoutException()).Returns(default);

        var policy = Resilience.Default with
        {
            Backoff = Backoff.None,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Deadline = Timeout.InfiniteTimeSpan,
        };

        var result = await policy.TryRunAsync(ct => calls.NextVoidAsync(ct));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, calls.CallCount);
    }

    /// <summary>
    ///     The documented scenario, kept executable: three scripted calls, two transient,
    ///     asserted on the attempt log rather than on elapsed time.
    /// </summary>
    [Fact]
    public async Task The_documented_scenario_runs_as_documented()
    {
        var time = new FakeTimeProvider();
        var policy = Resilience.Default with { Time = time, Backoff = Backoff.None };

        var calls = Sequence.For<int>(time)
            .Throws(new TimeoutException())
            .Throws(new TimeoutException())
            .Returns(200);

        var result = await policy.TryRunAsync(ct => calls.NextAsync(ct));

        Assert.True(result.IsSuccess);
        Assert.Equal(200, result.Value);
        Assert.Equal(3, result.Attempts.Count);
        Assert.Equal(StopReason.Succeeded, result.StopReason);
        Assert.All(result.Attempts.Take(2), a => Assert.Equal(VerdictKind.Transient, a.Verdict.Kind));
    }
}
