using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     The recording listener. It exists so a test can assert on the ordered event sequence, which is
///     the only assertion that catches a telemetry surface raising the right events in the wrong order.
/// </summary>
public sealed class EventRecorderTests
{
    private static Resilience Instant => Resilience.Default with
    {
        Backoff = Backoff.None,
        AttemptTimeout = Timeout.InfiniteTimeSpan,
        Deadline = Timeout.InfiniteTimeSpan,
    };

    [Fact]
    public async Task It_records_the_whole_sequence_in_order()
    {
        var events = new EventRecorder();
        var calls = Sequence.For<int>().Throws(new TimeoutException()).Returns(1);

        await (Instant with { OnEvent = events.Record }).RunAsync(ct => calls.NextAsync(ct));

        Assert.Equal(
            [CallEventKind.Attempt, CallEventKind.Retrying, CallEventKind.Attempt, CallEventKind.Succeeded],
            events.Kinds);
    }

    [Fact]
    public async Task Single_returns_the_one_event_of_a_kind()
    {
        var events = new EventRecorder();
        var calls = Sequence.For<int>().Throws(new TimeoutException()).Returns(42);

        await (Instant with { Name = "api", OnEvent = events.Record }).RunAsync(ct => calls.NextAsync(ct));

        var succeeded = events.Single(CallEventKind.Succeeded);

        Assert.Equal("api", succeeded.PolicyName);
        Assert.Equal(2, succeeded.AttemptNumber);
        Assert.Equal(42, succeeded.Result);
    }

    [Fact]
    public async Task Single_says_what_was_actually_recorded_when_it_cannot()
    {
        var events = new EventRecorder();
        var calls = Sequence.For<int>().Throws(new TimeoutException()).Returns(1);

        await (Instant with { OnEvent = events.Record }).RunAsync(ct => calls.NextAsync(ct));

        var error = Assert.Throws<InvalidOperationException>(() => events.Single(CallEventKind.Attempt));

        Assert.Contains("found 2", error.Message, StringComparison.Ordinal);
        Assert.Contains("Attempt, Retrying, Attempt, Succeeded", error.Message, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => events.Single(CallEventKind.BreakerOpened));
    }

    [Fact]
    public async Task Counting_and_filtering_agree_with_the_recorded_sequence()
    {
        var events = new EventRecorder();
        var calls = Sequence.For<int>().Throws(new TimeoutException(), 2).Returns(1);

        await (Instant with { OnEvent = events.Record }).RunAsync(ct => calls.NextAsync(ct));

        Assert.Equal(6, events.Count);
        Assert.Equal(3, events.CountOf(CallEventKind.Attempt));
        Assert.Equal(3, events.OfKind(CallEventKind.Attempt).Count);
        Assert.True(events.Contains(CallEventKind.Retrying));
        Assert.False(events.Contains(CallEventKind.DeadlineExceeded));
        Assert.Equal(CallEventKind.Attempt, events[0].Kind);
        Assert.Equal(events.Count, events.Events.Count);
    }

    [Fact]
    public async Task Clear_lets_one_recorder_span_several_calls()
    {
        var events = new EventRecorder();
        var policy = Instant with { OnEvent = events.Record };

        await policy.RunAsync(_ => Task.FromResult(1));
        Assert.Equal(2, events.Count);

        events.Clear();
        Assert.Equal("(no events)", events.ToString());

        await policy.RunAsync(_ => Task.FromResult(1));
        Assert.Equal([CallEventKind.Attempt, CallEventKind.Succeeded], events.Kinds);
    }

    [Fact]
    public async Task Events_is_a_snapshot_rather_than_a_live_view()
    {
        var events = new EventRecorder();
        var policy = Instant with { OnEvent = events.Record };

        await policy.RunAsync(_ => Task.FromResult(1));
        var snapshot = events.Events;

        await policy.RunAsync(_ => Task.FromResult(1));

        Assert.Equal(2, snapshot.Count);
        Assert.Equal(4, events.Count);
    }

    [Fact]
    public async Task Two_listeners_compose_the_way_delegates_do()
    {
        var events = new EventRecorder();
        var also = new EventRecorder();

        var both = events.Record;
        both += also.Record;

        await (Instant with { OnEvent = both }).RunAsync(_ => Task.FromResult(1));

        Assert.Equal(events.Kinds, also.Kinds);
    }

    [Fact]
    public async Task A_deadline_that_stops_the_call_is_recorded_against_the_fake_clock()
    {
        var time = new FakeTimeProvider();
        var events = new EventRecorder();

        var policy = Resilience.Default with
        {
            Time = time,
            Backoff = Backoff.None,
            Deadline = TimeSpan.FromSeconds(1),
            OnEvent = events.Record,
        };

        var calls = Sequence.For<int>(time).Delays(TimeSpan.FromSeconds(30)).Returns(1);

        var pending = policy.TryRunAsync(ct => calls.NextAsync(ct));
        time.Advance(TimeSpan.FromSeconds(2));

        var result = await pending;

        Assert.False(result.IsSuccess);
        Assert.Equal(StopReason.DeadlineExceeded, result.StopReason);
        Assert.True(events.Contains(CallEventKind.DeadlineExceeded));
        Assert.False(events.Contains(CallEventKind.Retrying));
    }

    [Fact]
    public async Task Concurrent_calls_sharing_one_recorder_lose_nothing()
    {
        var events = new EventRecorder();
        var policy = Instant with { OnEvent = events.Record };

        await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(async () => await policy.RunAsync(_ => Task.FromResult(1)))));

        Assert.Equal(64, events.Count);
        Assert.Equal(32, events.CountOf(CallEventKind.Succeeded));
    }
}
