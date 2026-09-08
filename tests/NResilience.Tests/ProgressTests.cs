using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     Progress bounds: what happens when a dependency answers and then stops writing.
///     <para>
///         The bound is <see cref="Resilience.AttemptTimeout" /> and there is no second number, so
///         every test here sets that one and reads it back as the stall. A policy whose attempt timeout
///         is <see cref="Timeout.InfiniteTimeSpan" /> - which <see cref="TestPolicy.Instant" /> is -
///         has no bound to apply, which is why these build their own policy rather than using it
///         unchanged, and why nothing else in the suite moved when this shipped.
///     </para>
///     <para>
///         The HTTP tests read the body themselves, under
///         <see cref="HttpCompletionOption.ResponseHeadersRead" />. That is not a convenience: it is the
///         shape in which a stalled body is a hang rather than a slow call, because the caller has the
///         response in hand and is waiting on bytes nobody is sending.
///     </para>
/// </summary>
public sealed class ProgressTests
{
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(10);

    // ---- The HTTP response body ----

    [Fact]
    public async Task A_response_body_that_stops_arriving_is_cut_off()
    {
        var time = new FakeTimeProvider();
        var body = new StallingStream("half a body"u8.ToArray());

        using var client = Client(time, body);
        using var response = await Head(client);

        var read = response.Content.ReadAsByteArrayAsync();

        // The far side has delivered its head and gone quiet, with a read outstanding on nothing.
        await body.Reached;

        time.Advance(Stall);

        var stalled = await Assert.ThrowsAsync<AttemptStalledException>(() => read);

        Assert.Equal(Stall, stalled.Stall);
        Assert.Equal("half a body".Length, stalled.Transferred);

        // No attempt log: the retry loop was over before the stall existed, which is the trade the
        // default makes and BufferResponses is how to take the other side of.
        Assert.Empty(stalled.Attempts);
    }

    [Fact]
    public async Task A_body_that_never_starts_reports_nothing_transferred()
    {
        var time = new FakeTimeProvider();
        var body = new StallingStream([]);

        using var client = Client(time, body);
        using var response = await Head(client);

        var read = response.Content.ReadAsByteArrayAsync();

        await body.Reached;
        time.Advance(Stall);

        var stalled = await Assert.ThrowsAsync<AttemptStalledException>(() => read);

        // Headers and then silence, which is the case worth telling apart from a truncated body.
        Assert.Equal(0, stalled.Transferred);
    }

    [Fact]
    public async Task The_bound_does_not_fire_early()
    {
        var time = new FakeTimeProvider();
        var body = new StallingStream([]);

        using var client = Client(time, body);
        using var response = await Head(client);

        var read = response.Content.ReadAsByteArrayAsync();

        await body.Reached;

        time.Advance(Stall - TimeSpan.FromMilliseconds(1));
        await Task.Yield();

        Assert.False(read.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.ThrowsAsync<AttemptStalledException>(() => read);
    }

    [Fact]
    public async Task A_body_that_keeps_arriving_is_never_cut_off()
    {
        var time = new FakeTimeProvider();

        using var client = Client(time, new TricklingStream(20));
        using var response = await Head(client);

        var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[1];
        var total = 0;

        // Twenty reads, each preceded by almost the whole bound. Three minutes of virtual time
        // against a ten-second stall: a bound on the total would have fired long ago, and the bound
        // on the gap never does.
        for (var i = 0; i < 20; i++)
        {
            time.Advance(Stall - TimeSpan.FromSeconds(1));
            total += await stream.ReadAsync(buffer);
        }

        Assert.Equal(20, total);
        Assert.Equal(0, await stream.ReadAsync(buffer));
    }

    [Fact]
    public async Task A_consumer_that_waits_between_reads_is_not_blamed_for_the_gap()
    {
        var time = new FakeTimeProvider();

        using var client = Client(time, new TricklingStream(4));
        using var response = await Head(client);

        var stream = await response.Content.ReadAsStreamAsync();

        // Nobody is reading, so the far side owes nothing. The bound fires three times over and
        // finds no outstanding read each time, which is the case that would otherwise make a slow
        // consumer look like a broken dependency.
        time.Advance(Stall * 3);

        var buffer = new byte[1];

        Assert.Equal(1, await stream.ReadAsync(buffer));
    }

    [Fact]
    public async Task BoundProgress_false_leaves_the_body_unbounded()
    {
        var time = new FakeTimeProvider();
        var body = new StallingStream("head"u8.ToArray());

        using var cancellation = new CancellationTokenSource();
        using var client = Client(time, body, Policy(time) with { BoundProgress = false });
        using var response = await Head(client);

        var read = response.Content.ReadAsByteArrayAsync(cancellation.Token);

        await body.Reached;

        time.Advance(Stall * 100);
        await Task.Yield();

        // The failure this feature exists to remove, pinned so that turning the feature off is a
        // decision with a visible consequence rather than a no-op.
        Assert.False(read.IsCompleted);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    [Fact]
    public async Task An_infinite_attempt_timeout_bounds_nothing()
    {
        var time = new FakeTimeProvider();
        var body = new StallingStream("head"u8.ToArray());

        using var cancellation = new CancellationTokenSource();

        // BoundProgress is on and there is still no bound: the number it applies is the attempt
        // timeout, and this policy has none. That is the whole rule.
        using var client = Client(time, body, Policy(time) with { AttemptTimeout = Timeout.InfiniteTimeSpan });
        using var response = await Head(client);

        var read = response.Content.ReadAsByteArrayAsync(cancellation.Token);

        await body.Reached;

        time.Advance(Stall * 100);
        await Task.Yield();

        Assert.False(read.IsCompleted);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    [Fact]
    public async Task The_consumers_own_cancellation_wins_over_the_bound()
    {
        var time = new FakeTimeProvider();
        var body = new StallingStream("head"u8.ToArray());

        using var cancellation = new CancellationTokenSource();
        using var client = Client(time, body);
        using var response = await Head(client);

        var read = response.Content.ReadAsByteArrayAsync(cancellation.Token);

        await body.Reached;
        await cancellation.CancelAsync();

        // Their own cancellation, not an AttemptStalledException: a caller must never be told that
        // the thing they cancelled was the dependency's fault.
        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);

        Assert.IsNotType<AttemptStalledException>(thrown);
    }

    [Fact]
    public async Task A_stall_raises_one_Stalled_event_and_no_second_terminal_event()
    {
        var time = new FakeTimeProvider();
        var body = new StallingStream([]);
        var recorder = new EventRecorder();

        using var client = Client(time, body, Policy(time).WithListener(recorder.Record));
        using var response = await Head(client);

        // The call already succeeded, because it did: the status line arrived and was classified.
        Assert.Single(recorder.Events, e => e.Kind == CallEventKind.Succeeded);

        var read = response.Content.ReadAsByteArrayAsync();

        await body.Reached;
        time.Advance(Stall);

        await Assert.ThrowsAsync<AttemptStalledException>(() => read);

        var stalled = Assert.Single(recorder.Events, e => e.Kind == CallEventKind.Stalled);

        Assert.Equal(Stall, stalled.Delay);
        Assert.Equal(VerdictKind.Transient, stalled.Verdict.Kind);
        Assert.IsType<AttemptStalledException>(stalled.Exception);

        // The one event that can arrive after a terminal one, and it is not itself terminal - a
        // listener counting terminal events per call still counts exactly one.
        Assert.False(stalled.IsTerminal);
        Assert.Single(recorder.Events, e => e.IsTerminal);
    }

    [Fact]
    public async Task A_declared_empty_body_is_not_wrapped()
    {
        var time = new FakeTimeProvider();

        var transport = new ScriptedHttpHandler().Responds(() => new HttpResponseMessage(HttpStatusCode.NoContent)
        {
            Content = new ByteArrayContent([]),
        });

        using var client = Client(transport, Policy(time));
        using var response = await Head(client);

        // Nothing to stall on, so nothing is paid: no wrapper, and therefore no timer and no
        // cancellation source for a 204 or a HEAD.
        Assert.IsType<ByteArrayContent>(response.Content);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task The_wrapper_keeps_the_headers_the_transport_sent()
    {
        var time = new FakeTimeProvider();

        var transport = new ScriptedHttpHandler().Responds(() =>
        {
            var content = new StreamContent(new TricklingStream(3));
            content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=utf-8");
            content.Headers.TryAddWithoutValidation("Content-Encoding", "identity");

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        using var client = Client(transport, Policy(time));
        using var response = await Head(client);

        // Content-Type above all: a substituted content that lost it would break every caller that
        // deserializes by media type.
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Equal(["identity"], response.Content.Headers.ContentEncoding);
    }

    // ---- BufferResponses ----

    [Fact]
    public async Task BufferResponses_moves_the_stall_inside_the_attempt_and_retries_it()
    {
        var time = new FakeTimeProvider();
        var body = new StallingStream("truncated"u8.ToArray());

        var transport = new ScriptedHttpHandler()
            .Responds(() => Streamed(body))
            .Responds(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("whole") });

        using var client = Client(transport, Policy(time), new HttpResilienceOptions { BufferResponses = true });

        var call = client.GetAsync(new Uri("https://api.test/thing"));

        await body.Reached;
        time.Advance(Stall);

        using var response = await call;

        // The body was read inside the attempt, so the stall was the attempt's own ceiling running
        // out - the executor's AttemptTimeoutException, classified transient - and the retry got a
        // whole body. That is the point of the option: the failure became retryable.
        Assert.Equal("whole", await response.Content.ReadAsStringAsync());
        Assert.Equal(2, transport.CallCount);
    }

    [Fact]
    public async Task A_buffered_body_is_not_wrapped_because_it_is_already_read()
    {
        var time = new FakeTimeProvider();

        var transport = new ScriptedHttpHandler()
            .Responds(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("done") });

        using var client = Client(transport, Policy(time), new HttpResilienceOptions { BufferResponses = true });
        using var response = await Head(client);

        Assert.IsType<StreamContent>(response.Content);
        Assert.Equal("done", await response.Content.ReadAsStringAsync());
    }

    // ---- The streaming overload ----

    [Fact]
    public async Task A_stream_that_stops_between_elements_is_cut_off()
    {
        var time = new FakeTimeProvider();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = Policy(time);
        var seen = new List<int>();

        var enumeration = Task.Run(async () =>
        {
            await foreach (var element in policy.RunAsync(ct => ThreeThenNothing(reached, ct)))
            {
                seen.Add(element);
            }
        });

        // Three elements handed over, and the fourth pull outstanding on a source that will never
        // answer it.
        await reached.Task;

        time.Advance(Stall);

        var stalled = await Assert.ThrowsAsync<AttemptStalledException>(() => enumeration);

        Assert.Equal(Stall, stalled.Stall);
        Assert.Equal([1, 2, 3], seen);

        // Elements delivered rather than bytes, which is what progress means for this shape.
        Assert.Equal(3, stalled.Transferred);
    }

    [Fact]
    public async Task A_stream_that_stops_between_elements_raises_the_same_event()
    {
        var time = new FakeTimeProvider();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder = new EventRecorder();
        var policy = Policy(time).WithListener(recorder.Record);

        var enumeration = Task.Run(async () =>
        {
            await foreach (var _ in policy.RunAsync(ct => ThreeThenNothing(reached, ct)))
            {
            }
        });

        await reached.Task;
        time.Advance(Stall);

        await Assert.ThrowsAsync<AttemptStalledException>(() => enumeration);

        var stalled = Assert.Single(recorder.Events, e => e.Kind == CallEventKind.Stalled);

        Assert.Equal(Stall, stalled.Delay);
        Assert.False(stalled.IsTerminal);
    }

    [Fact]
    public async Task A_stream_whose_elements_keep_arriving_is_not_cut_off()
    {
        var time = new FakeTimeProvider();
        var policy = Policy(time);
        var seen = new List<int>();

        await foreach (var element in policy.RunAsync(ct => Trickling(10, time, ct)))
        {
            seen.Add(element);
        }

        Assert.Equal(10, seen.Count);
    }

    [Fact]
    public async Task A_stream_of_ready_elements_is_handed_over_unbounded()
    {
        var time = new FakeTimeProvider();
        var policy = Policy(time);
        var seen = new List<int>();

        // Every pull completes synchronously, so nothing is ever armed. Advancing past the bound
        // between elements has to be inert, which is what makes the fast path free rather than
        // merely cheap.
        await foreach (var element in policy.RunAsync(ct => Ready(5)))
        {
            time.Advance(Stall * 2);
            seen.Add(element);
        }

        Assert.Equal([0, 1, 2, 3, 4], seen);
    }

    [Fact]
    public async Task A_stream_bound_reaches_a_TryRunAsync_consumer_at_their_own_read()
    {
        var time = new FakeTimeProvider();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = Policy(time);

        // TryRunAsync reports the outcome of *starting* the stream, and starting it worked. The
        // stall is after the handover, so it arrives where every other post-start fault arrives.
        var result = await policy.TryRunAsync(ct => ThreeThenNothing(reached, ct));

        Assert.True(result.IsSuccess);

        var seen = new List<int>();

        var enumeration = Task.Run(async () =>
        {
            await foreach (var element in result.Value!)
            {
                seen.Add(element);
            }
        });

        await reached.Task;
        time.Advance(Stall);

        await Assert.ThrowsAsync<AttemptStalledException>(() => enumeration);
        Assert.Equal([1, 2, 3], seen);
    }

    [Fact]
    public async Task A_stream_under_a_policy_that_bounds_nothing_is_unchanged()
    {
        var time = new FakeTimeProvider();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var cancellation = new CancellationTokenSource();
        var policy = Policy(time) with { BoundProgress = false };

        var enumeration = Task.Run(async () =>
        {
            await foreach (var _ in policy.RunAsync(ct => ThreeThenNothing(reached, ct), cancellation.Token))
            {
            }
        });

        await reached.Task;

        time.Advance(Stall * 100);
        await Task.Yield();

        Assert.False(enumeration.IsCompleted);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumeration);
    }

    // ---- Sources ----

    private static async IAsyncEnumerable<int> ThreeThenNothing(
        TaskCompletionSource reached,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return 1;
        yield return 2;
        yield return 3;

        reached.TrySetResult();

        // The far side of a stream that answered and then went quiet. Infinite rather than long, so
        // the test cannot pass by waiting.
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

        yield return 4;
    }

    private static async IAsyncEnumerable<int> Trickling(
        int count,
        FakeTimeProvider time,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < count; i++)
        {
            // Yielded so the pull is genuinely pending and the bound is genuinely armed; a
            // synchronous yield would skip the arming path this test exists to cover.
            await Task.Yield();

            time.Advance(Stall - TimeSpan.FromSeconds(1));
            cancellationToken.ThrowIfCancellationRequested();

            yield return i;
        }
    }

    private static async IAsyncEnumerable<int> Ready(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return i;
        }

        await Task.CompletedTask;
    }

    // ---- Plumbing ----

    private static Resilience Policy(TimeProvider time) => TestPolicy.InstantHttp with
    {
        AttemptTimeout = Stall,
        Time = time,

        // One bound, named. The measured ceiling would be a second number tightening the first, and
        // these tests are about the number they set.
        AttemptCeiling = null,
    };

    /// <summary>
    ///     The response without its body, which is the shape in which a stalled body is a hang. The
    ///     transport timeout is off for the reason the library turns it off in production: two timeout
    ///     systems are worse than one, and here the second one is measured in real seconds against a
    ///     test that runs on virtual ones.
    /// </summary>
    private static Task<HttpResponseMessage> Head(HttpClient client) =>
        client.GetAsync(new Uri("https://api.test/thing"), HttpCompletionOption.ResponseHeadersRead);

    private static HttpClient Client(TimeProvider time, Stream body, Resilience? policy = null) =>
        Client(new ScriptedHttpHandler().Responds(() => Streamed(body)), policy ?? Policy(time));

    private static HttpClient Client(
        HttpMessageHandler transport,
        Resilience policy,
        HttpResilienceOptions? options = null) =>
        new(new HttpResilienceHandler(transport, policy, options)) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    ///     A 200 whose body is a live stream and whose length is undeclared, which is what a chunked
    ///     response looks like and is where a stall actually happens.
    /// </summary>
    private static HttpResponseMessage Streamed(Stream body) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(body) };

    /// <summary>
    ///     Hands back its head and then holds the connection open forever, which is the failure the
    ///     progress bound exists to make finite.
    /// </summary>
    private sealed class StallingStream(byte[] head) : ReadOnlyStream
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        /// <summary>Completes once the head is delivered and a read is outstanding on nothing.</summary>
        internal Task Reached => _reached.Task;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < head.Length)
            {
                var take = Math.Min(buffer.Length, head.Length - _position);
                head.AsSpan(_position, take).CopyTo(buffer.Span);
                _position += take;

                return take;
            }

            _reached.TrySetResult();

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

            return 0;
        }
    }

    /// <summary>A body that keeps arriving, one byte per read, synchronously.</summary>
    private sealed class TricklingStream(int count) : ReadOnlyStream
    {
        private int _served;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_served == count || buffer.Length == 0)
                return new ValueTask<int>(0);

            _served++;
            buffer.Span[0] = (byte)'x';

            return new ValueTask<int>(1);
        }
    }

    /// <summary>The members neither test stream has an opinion about.</summary>
    private abstract class ReadOnlyStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
