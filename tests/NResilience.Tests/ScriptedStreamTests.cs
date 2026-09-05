using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     The scripted cold stream, tested as itself rather than through a policy. The counters are the
///     reason this double exists - a streaming test asserts on <see cref="ScriptedStream{T}.CallCount" />
///     and <see cref="ScriptedStream{T}.LiveEnumerators" /> to prove which attempts the policy started
///     and which it tore down - so a counter that miscounts turns every one of those assertions into a
///     lie. <see cref="StreamingTests" /> covers what the policy does with it.
/// </summary>
public sealed class ScriptedStreamTests
{
    [Fact]
    public async Task Steps_are_served_in_order_one_per_attempt()
    {
        var streams = ScriptedStream.For<int>()
            .Yields(1, 2)
            .Yields(3);

        Assert.Equal([1, 2], await CollectAsync(streams.Next()));
        Assert.Equal([3], await CollectAsync(streams.Next()));
        Assert.Equal(2, streams.CallCount);
    }

    [Fact]
    public async Task Throws_throws_the_instance_it_was_given_from_the_first_pull()
    {
        var boom = new IOException("reset");
        var streams = ScriptedStream.For<int>().Throws(boom);

        Assert.Same(boom, await Assert.ThrowsAsync<IOException>(() => CollectAsync(streams.Next())));
    }

    [Fact]
    public async Task FaultsAfter_serves_its_elements_and_then_throws_once()
    {
        var boom = new InvalidOperationException("mid-stream");
        var streams = ScriptedStream.For<int>().FaultsAfter(boom, 1, 2);

        var received = new List<int>();

        await using var enumerator = streams.Next().GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        received.Add(enumerator.Current);
        Assert.True(await enumerator.MoveNextAsync());
        received.Add(enumerator.Current);

        Assert.Same(boom, await Assert.ThrowsAsync<InvalidOperationException>(async () => await enumerator.MoveNextAsync()));

        // Exactly once: what the consumer does after the fault is the consumer's business, and a
        // source that threw the same fault forever would be a different scenario to script.
        Assert.False(await enumerator.MoveNextAsync());
        Assert.Equal([1, 2], received);
    }

    [Fact]
    public async Task YieldsNothing_serves_a_source_that_completes_with_nothing()
    {
        var streams = ScriptedStream.For<int>().YieldsNothing();

        Assert.Equal([], await CollectAsync(streams.Next()));
    }

    [Fact]
    public async Task Running_out_of_script_says_how_many_steps_there_were()
    {
        var streams = ScriptedStream.For<int>().Yields(1);

        _ = streams.Next();

        var thrown = Assert.Throws<InvalidOperationException>(() => streams.Next());

        Assert.Contains("1 step(s)", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("attempt 2", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(2, streams.CallCount);
    }

    [Fact]
    public void A_trailing_Delays_is_named_as_the_scripting_mistake_it_is()
    {
        var streams = ScriptedStream.For<int>().Yields(1).Delays(TimeSpan.FromSeconds(1));

        _ = streams.Next();

        var thrown = Assert.Throws<InvalidOperationException>(() => streams.Next());

        Assert.Contains("trailing Delays()", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Negative_delays_are_rejected_where_they_are_written()
        => Assert.Throws<ArgumentOutOfRangeException>(() => ScriptedStream.For<int>().Delays(TimeSpan.FromSeconds(-1)));

    [Fact]
    public async Task Delays_accumulate_onto_the_next_step_and_are_served_on_the_supplied_clock()
    {
        var time = new FakeTimeProvider();

        var streams = ScriptedStream.For<int>(time)
            .Delays(TimeSpan.FromSeconds(2))
            .Delays(TimeSpan.FromSeconds(3))
            .Yields(1);

        var collected = CollectAsync(streams.Next());

        Assert.False(collected.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal([1], await collected);
    }

    /// <summary>
    ///     The token handed to the source factory is honored, not discarded - the policy passes the
    ///     attempt's token there, and a double that dropped it would make every attempt-ceiling test
    ///     pass for the wrong reason.
    /// </summary>
    [Fact]
    public async Task A_delayed_step_honors_the_token_the_factory_was_given()
    {
        var time = new FakeTimeProvider();
        var streams = ScriptedStream.For<int>(time).YieldsAfter(TimeSpan.FromSeconds(10), 1);

        using var cancellation = new CancellationTokenSource();

        var collected = CollectAsync(streams.Next(cancellation.Token));

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collected);
    }

    /// <summary>
    ///     And the enumeration-time token as well, combined with the factory's on the same rules
    ///     <c>[EnumeratorCancellation]</c> uses, because that is what the source this double stands in
    ///     for would do.
    /// </summary>
    [Fact]
    public async Task A_delayed_step_honors_the_token_the_enumeration_was_started_with()
    {
        var time = new FakeTimeProvider();
        var streams = ScriptedStream.For<int>(time).YieldsAfter(TimeSpan.FromSeconds(10), 1);

        using var factory = new CancellationTokenSource();
        using var enumeration = new CancellationTokenSource();

        await using var enumerator = streams.Next(factory.Token).GetAsyncEnumerator(enumeration.Token);

        var pulled = enumerator.MoveNextAsync();

        await enumeration.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pulled);
    }

    [Fact]
    public async Task The_counters_track_every_enumerator_from_construction_to_disposal()
    {
        var streams = ScriptedStream.For<int>().Yields(1).Yields(2);

        // Served but never pulled from: an attempt that started, with no enumerator yet.
        var source = streams.Next();

        Assert.Equal(1, streams.CallCount);
        Assert.Equal(0, streams.LiveEnumerators);

        var enumerator = source.GetAsyncEnumerator();

        Assert.Equal(1, streams.LiveEnumerators);
        Assert.Equal(0, streams.DisposedEnumerators);

        await enumerator.DisposeAsync();

        Assert.Equal(0, streams.LiveEnumerators);
        Assert.Equal(1, streams.DisposedEnumerators);
    }

    /// <summary>
    ///     Disposing twice is legal for an <see cref="IAsyncDisposable" /> and does happen - a consumer
    ///     with its own <c>await using</c> around an enumerator the streaming path also owns disposes it
    ///     once each. The counters have to survive it, because a double-count reports a leak that is not
    ///     there and would fail the tests that exist to catch a real one.
    /// </summary>
    [Fact]
    public async Task Disposing_an_enumerator_twice_counts_it_once()
    {
        var streams = ScriptedStream.For<int>().Yields(1);

        var enumerator = streams.Next().GetAsyncEnumerator();

        await enumerator.DisposeAsync();
        await enumerator.DisposeAsync();

        Assert.Equal(0, streams.LiveEnumerators);
        Assert.Equal(1, streams.DisposedEnumerators);
    }

    private static async Task<List<int>> CollectAsync(IAsyncEnumerable<int> stream)
    {
        var received = new List<int>();

        await foreach (var item in stream)
        {
            received.Add(item);
        }

        return received;
    }
}
