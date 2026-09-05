using System.Runtime.CompilerServices;
using NResilience.Testing;

namespace NResilience.Docs;

/// <summary>Streaming calls: retry until the first element, then hand the enumeration to the caller.</summary>
public sealed class StreamingDocs
{
    [Fact]
    public async Task A_stream_is_retried_until_the_first_element()
    {
        var streams = ScriptedStream.For<int>()
            .Throws(new IOException())
            .Yields(1, 2, 3);

        // <snippet:stream-basic>
        // The source is cold: each attempt re-invokes it, exactly as the HTTP handler
        // builds a fresh request per attempt. Pass the token into whatever you call.
        var api = Resilience.Default;

        var received = new List<int>();

        await foreach (var item in api.RunAsync(ct => streams.Next(ct)))
            received.Add(item);
        // </snippet:stream-basic>

        Assert.Equal(expected: [1, 2, 3], actual: received);
        Assert.Equal(expected: 2, actual: streams.Starts);
    }

    [Fact]
    public async Task A_stream_over_the_stateful_overload_avoids_a_closure()
    {
        var streams = ScriptedStream.For<int>().Yields(1, 2);

        // <snippet:stream-state>
        // The static lambda takes the stream as caller state, so it allocates no closure.
        await foreach (var item in Resilience.Default.RunAsync(
                           static (source, ct) => source.Next(ct),
                           streams))
        {
            Consume(item);
        }
        // </snippet:stream-state>

        static void Consume(int item)
        {
        }
    }

    [Fact]
    public async Task The_first_element_is_classified()
    {
        var streams = ScriptedStream.For<int>()
            .Yields(-1)
            .Yields(7);

        var api = Resilience.Default with
        {
            Classifier = Classifier.Default.OnResult<int>(static v => v < 0 ? Verdict.Transient : Verdict.Ok),
        };

        var received = new List<int>();

        // <snippet:stream-classifier>
        // `OnResult<T>` judges the first element like any result. A verdict the policy will not
        // accept is retried, and on the final attempt it throws CallRejectedException from the
        // first MoveNextAsync - the consumer never receives an element the classifier refused.
        await foreach (var item in api.RunAsync(ct => streams.Next(ct)))
            received.Add(item);
        // </snippet:stream-classifier>

        Assert.Equal(expected: [7], actual: received);
        Assert.Equal(expected: 2, actual: streams.Starts);
    }

    [Fact]
    public async Task A_post_start_fault_is_the_consumers()
    {
        var midStream = new InvalidOperationException("the dependency broke mid-stream");
        var streams = ScriptedStream.For<int>().FaultsAfter(midStream, 1, 2);

        var received = new List<int>();
        InvalidOperationException? fault = null;

        // <snippet:stream-post-start>
        // A fault after the first element propagates out of MoveNextAsync verbatim:
        // unclassified, no event raised, nothing recorded against the breaker. The call
        // succeeded; what the source does afterwards is the consumer's exception.
        try
        {
            await foreach (var item in Resilience.Default.RunAsync(ct => streams.Next(ct)))
                received.Add(item);
        }
        catch (InvalidOperationException e)
        {
            fault = e;
        }
        // </snippet:stream-post-start>

        Assert.Equal(expected: [1, 2], actual: received);
        Assert.Same(midStream, fault);
    }

    [Fact]
    public async Task Hedge_is_refused_for_streams()
    {
        var hedged = Resilience.Default with { Hedge = Hedge.At() };

        // <snippet:stream-hedge-refusal>
        // A hedge is a concurrent second copy of a value-returning attempt; two interleaved
        // enumerables is a buffering problem, not a hedge. The streaming overloads refuse a
        // hedged policy at the RunAsync call, and the same policy still runs calls.
        Assert.Throws<ResilienceConfigurationException>(() => hedged.RunAsync<int>(static ct => Empty(ct)));
        // </snippet:stream-hedge-refusal>

        static async IAsyncEnumerable<int> Empty([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    [Fact]
    public async Task A_streamed_source_is_scriptable()
    {
        var time = TimeProvider.System;
        var streams = ScriptedStream.For<int>(time)
            .YieldsAfter(TimeSpan.FromSeconds(5), 0)
            .Yields(1, 2);

        var received = new List<int>();

        // <snippet:stream-scripted>
        // ScriptedStream is to a streaming source what Sequence is to a callback: a
        // script of stream-shaped outcomes, served one per attempt. The counters prove
        // which attempts started, which were abandoned, and which survived.
        var policy = Resilience.Default with
        {
            AttemptTimeout = TimeSpan.FromSeconds(1),
            Backoff = Backoff.None,
        };

        await foreach (var item in policy.RunAsync(ct => streams.Next(ct)))
            received.Add(item);
        // </snippet:stream-scripted>

        Assert.Equal(expected: [1, 2], actual: received);
        Assert.Equal(expected: 2, actual: streams.Starts);
        Assert.Equal(expected: 2, actual: streams.DisposedEnumerators);
    }
}