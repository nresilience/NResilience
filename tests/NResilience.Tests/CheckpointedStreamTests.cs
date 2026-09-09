using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     The checkpointed resume contract: a failure before any element restarts through the ordinary
///     retry loop, a failure after one or more elements restarts from the last checkpoint instead of
///     stopping, <see cref="Resilience.Restarts" /> bounds how many times that can happen, and nothing
///     already delivered is delivered again.
/// </summary>
public sealed class CheckpointedStreamTests
{
    [Fact]
    public async Task A_failure_after_progress_restarts_from_the_last_checkpoint()
    {
        // The source is a plain function of the checkpoint: from offset 0 it yields 1, 2 and then
        // throws; from offset 2 it yields 3, 4 to completion. A caller who did not resume would see
        // 1, 2 and an exception; this overload sees 1, 2, 3, 4.
        var source = Source(
            (0, new[] { 1, 2 }, true),
            (2, new[] { 3, 4 }, false));

        var policy = TestPolicy.Instant;
        var received = await CollectAsync(policy.RunAsync(source, 0, static v => v));

        Assert.Equal([1, 2, 3, 4], received);
    }

    [Fact]
    public async Task A_failure_before_any_element_is_not_a_restart_candidate()
    {
        // The inner RunAsync's own Attempts are exhausted (Instant gives 3) before anything is ever
        // delivered - a restart would not help, because there is no checkpoint yet, and the ordinary
        // failure surfaces unchanged.
        var attempts = 0;

        Func<int, CancellationToken, IAsyncEnumerable<int>> source = (checkpoint, ct) =>
        {
            attempts++;
            throw new IOException("never answers");
        };

        var policy = TestPolicy.Instant with { Classifier = Classifier.RetryEverything };

        await Assert.ThrowsAsync<IOException>(() => CollectAsync(policy.RunAsync(source, 0, static v => v)));

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Restarts_bounds_how_many_times_a_stream_may_reconnect()
    {
        // Every restart delivers one element and then fails - forever, if nothing capped it. With
        // Restarts = 2 the caller sees three segments' worth of one element each and then the
        // failure the third restart attempt produced.
        var starts = 0;

        Func<int, CancellationToken, IAsyncEnumerable<int>> source = (checkpoint, ct) => Segment(checkpoint, ct);

        async IAsyncEnumerable<int> Segment(int checkpoint, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            starts++;
            await Task.Yield();
            yield return checkpoint + 1;
            throw new IOException("dropped");
        }

        var policy = TestPolicy.Instant with { Attempts = 1, Restarts = 2 };

        var received = new List<int>();

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var v in policy.RunAsync(source, 0, static v => v))
                received.Add(v);
        });

        // One initial segment plus two restarts: three elements, three starts.
        Assert.Equal([1, 2, 3], received);
        Assert.Equal(3, starts);
    }

    [Fact]
    public async Task Zero_restarts_never_reconnects()
    {
        var source = Source((0, new[] { 1 }, true));
        var policy = TestPolicy.Instant with { Restarts = 0 };

        var received = new List<int>();

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (var v in policy.RunAsync(source, 0, static v => v))
                received.Add(v);
        });

        Assert.Equal([1], received);
    }

    [Fact]
    public async Task A_restart_raises_StreamResumed()
    {
        var source = Source(
            (0, new[] { 1 }, true),
            (1, new[] { 2 }, false));

        var events = new List<CallEvent>();
        var policy = TestPolicy.Instant with { OnEvent = e => events.Add(e) };

        await CollectAsync(policy.RunAsync(source, 0, static v => v));

        var resumed = Assert.Single(events, e => e.Kind == CallEventKind.StreamResumed);
        Assert.Equal(1, resumed.AttemptNumber);
    }

    [Fact]
    public void Hedge_refuses_the_checkpointed_overloads()
    {
        var policy = TestPolicy.Instant with { Attempts = 2, Hedge = Hedge.At() };

        Assert.Throws<ResilienceConfigurationException>(() =>
            policy.RunAsync<int, int>(static (_, ct) => Run([], false, ct), 0, static v => v));
    }

    [Fact]
    public async Task TryRunAsync_reports_success_without_throwing()
    {
        var source = Source(
            (0, new[] { 1, 2 }, true),
            (2, new[] { 3 }, false));

        var policy = TestPolicy.Instant;
        var result = await policy.TryRunAsync(source, 0, static v => v);

        Assert.True(result.IsSuccess);
        Assert.True(result.TryGetValue(out var stream));

        var received = await CollectAsync(stream!);
        Assert.Equal([1, 2, 3], received);
    }

    [Fact]
    public async Task TryRunAsync_reports_failure_when_the_first_element_never_arrives()
    {
        Func<int, CancellationToken, IAsyncEnumerable<int>> source = (checkpoint, ct) => throw new IOException("down");
        var policy = TestPolicy.Instant with { Classifier = Classifier.RetryEverything };

        var result = await policy.TryRunAsync(source, 0, static v => v);

        Assert.False(result.IsSuccess);
        Assert.IsType<IOException>(result.Exception);
    }

    [Fact]
    public void Restarts_below_zero_is_rejected()
    {
        var problems = (TestPolicy.Instant with { Restarts = -1 }).Problems();
        Assert.Contains(problems, p => p.Contains("Restarts", StringComparison.Ordinal));
    }

    [Fact]
    public void None_disables_restarts()
    {
        Assert.Equal(0, Resilience.None.Restarts);
    }

    /// <summary>
    ///     Builds a checkpointed source from segments: each is <c>(fromCheckpoint, elements, thenFails)</c>.
    ///     The next segment is picked by matching the checkpoint it was invoked with, and using
    ///     <see cref="ScriptedStream{T}" /> would not fit here because that double has no notion of a
    ///     checkpoint argument at all.
    /// </summary>
    private static Func<int, CancellationToken, IAsyncEnumerable<int>> Source(params (int From, int[] Elements, bool ThenFails)[] segments) =>
        (checkpoint, ct) =>
        {
            var segment = segments.First(s => s.From == checkpoint);
            return Run(segment.Elements, segment.ThenFails, ct);
        };

    private static async IAsyncEnumerable<int> Run(int[] elements, bool thenFails,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();

        foreach (var element in elements)
        {
            ct.ThrowIfCancellationRequested();
            yield return element;
        }

        if (thenFails)
            throw new IOException("connection reset");
    }

    private static async Task<List<int>> CollectAsync(IAsyncEnumerable<int> stream)
    {
        var received = new List<int>();

        await foreach (var v in stream)
            received.Add(v);

        return received;
    }
}
