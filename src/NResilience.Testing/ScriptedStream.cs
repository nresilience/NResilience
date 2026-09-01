using System.Runtime.ExceptionServices;

namespace NResilience.Testing;

/// <summary>
///     Entry point for the scripted cold stream. <see cref="For{T}(TimeProvider)" /> starts a script of
///     stream-shaped outcomes that a policy can execute against through the <c>RunAsync</c>
///     overloads taking an <c>IAsyncEnumerable&lt;T&gt;</c> source.
/// </summary>
public static class ScriptedStream
{
    /// <summary>
    ///     Starts a script of <typeparamref name="T" />-element streams.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="time">
    ///     The clock <see cref="ScriptedStream{T}.Delay" /> is served against. Pass the same
    ///     <c>FakeTimeProvider</c> the policy was given, or a scripted delay is a real sleep - which is
    ///     the flakiness this package exists to remove. Defaults to <see cref="TimeProvider.System" />.
    /// </param>
    /// <example>
    ///     <code>
    /// var streams = ScriptedStream.For&lt;int&gt;(time)
    ///     .YieldsAfter(TimeSpan.FromSeconds(5), 0)   // attempt 1: times out before the first element
    ///     .Yields(1, 2, 3);                          // attempt 2: retried into on the first element
    ///
    /// await foreach (var item in policy.RunAsync(streams.Next, cancellationToken))
    ///     Consume(item);
    /// </code>
    /// </example>
    public static ScriptedStream<T> For<T>(TimeProvider? time = null) => new(time ?? TimeProvider.System);
}

/// <summary>
///     A scripted cold source: a list of stream-shaped outcomes served one per attempt, in order.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
/// <remarks>
///     <para>
///         Each attempt the policy starts pulls the next outcome. An outcome is either elements -
///         possibly delayed before the first - or an exception thrown from the source's first pull.
///         Elements after the first are served without delay, because the streaming path's machinery
///         stops at the first element; a script that wants to exercise a mid-stream fault pairs
///         elements with a <see cref="FaultsAfter" /> fault on the same step.
///     </para>
///     <para>
///         Like <see cref="Sequence{T}" />, building is not thread-safe and is not meant to; serving
///         is, because a policy under test may well be driven from several tasks at once, and a
///         scripted double that races is worse than no double at all.
///     </para>
/// </remarks>
public sealed class ScriptedStream<T>
{
    private readonly List<Step> _steps = [];
    private readonly TimeProvider _time;

    private TimeSpan _pendingDelay;
    private bool _hasPendingDelay;

    private int _served;
    private int _live;
    private int _disposed;

    internal ScriptedStream(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>
    ///     How many attempts have started - how many times <see cref="Next" /> has been invoked to
    ///     produce a source, whether or not that source was ever pulled from.
    /// </summary>
    public int Starts => Volatile.Read(ref _served);

    /// <summary>
    ///     How many of the sources served are still undisposed. The streaming path disposes an
    ///     attempt's enumerator when it abandons the attempt, so after a retried stream settles this
    ///     reads the number of surviving enumerators - one while the caller is still enumerating, and
    ///     zero once they are done.
    /// </summary>
    public int LiveEnumerators => Volatile.Read(ref _live);

    /// <summary>
    ///     How many served enumerators have been disposed - abandoned by the policy, or finished by
    ///     the consumer. A retried stream that leaks its losing attempts reads here as well as on
    ///     <see cref="LiveEnumerators" />.
    /// </summary>
    public int DisposedEnumerators => Volatile.Read(ref _disposed);

    /// <summary>Appends a step that yields <paramref name="elements" />.</summary>
    public ScriptedStream<T> Yields(params ReadOnlySpan<T> elements)
    {
        _steps.Add(new Step(TakePendingDelay(), elements.ToArray(), null, null));
        return this;
    }

    /// <summary>
    ///     Appends a step that yields <paramref name="elements" />, waiting <paramref name="delay" />
    ///     before the first one.
    /// </summary>
    public ScriptedStream<T> YieldsAfter(TimeSpan delay, params ReadOnlySpan<T> elements)
    {
        Delay(delay);
        return Yields(elements);
    }

    /// <summary>
    ///     Appends a step that yields nothing: an empty source, which the streaming path treats as a
    ///     success.
    /// </summary>
    public ScriptedStream<T> Empty()
    {
        _steps.Add(new Step(TakePendingDelay(), [], null, null));
        return this;
    }

    /// <summary>
    ///     Appends a step that throws <paramref name="exception" /> from its first pull, after any
    ///     pending <see cref="Delay" />.
    /// </summary>
    /// <remarks>
    ///     The same instance is thrown each time the step is reached, so a test can assert on reference
    ///     equality against what came back out of the policy.
    /// </remarks>
    public ScriptedStream<T> Throws(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        _steps.Add(new Step(TakePendingDelay(), [], exception, null));
        return this;
    }

    /// <summary>
    ///     Appends a step that yields <paramref name="elements" /> and then throws
    ///     <paramref name="exception" /> mid-stream, from the pull after the last element.
    /// </summary>
    /// <remarks>
    ///     The streaming path stops classifying at the first element, so a fault after one is the
    ///     consumer's - it propagates out of <c>MoveNextAsync</c> verbatim, unclassified and raising
    ///     nothing. This step is how a test proves that without hand-rolling its own source.
    /// </remarks>
    public ScriptedStream<T> FaultsAfter(Exception exception, params ReadOnlySpan<T> elements)
    {
        ArgumentNullException.ThrowIfNull(exception);

        _steps.Add(new Step(TakePendingDelay(), elements.ToArray(), null, exception));
        return this;
    }

    /// <summary>
    ///     Makes the next step wait <paramref name="delay" /> before its first pull, and its outcome
    ///     land. Accumulates, exactly as <see cref="Sequence{T}.Delays" /> accumulates, so
    ///     <c>.Delay(a).Delay(b).Yields(x)</c> waits <c>a + b</c>. A trailing <see cref="Delay" />
    ///     with no step after it is a scripting mistake and is reported as one.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delay" /> is negative.</exception>
    public ScriptedStream<T> Delay(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);

        _pendingDelay += delay;
        _hasPendingDelay = true;
        return this;
    }

    /// <summary>
    ///     Serves the next step as a cold source. Bind this as a method group -
    ///     <c>policy.RunAsync(streams.Next, cancellationToken)</c> - or from a static lambda
    ///     <c>(streams, ct) => streams.Next(ct)</c> with <c>streams</c> as caller state, so the
    ///     callback allocates no closure.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Each invocation is one attempt, whether or not the source it returns is ever pulled from.
    ///         Running off the end of the script is a scripting mistake and is reported as one.
    ///     </para>
    ///     <para>
    ///         <paramref name="cancellationToken" /> is combined with the one the enumeration is
    ///         started with, exactly as <c>[EnumeratorCancellation]</c> combines them for a real
    ///         <c>async</c> iterator: whichever is set wins, and two different tokens are linked. The
    ///         policy passes the same attempt token to both, which costs nothing.
    ///     </para>
    /// </remarks>
    /// <param name="cancellationToken">The attempt's token, as the policy hands it to the source factory.</param>
    /// <returns>One attempt's cold source.</returns>
    /// <exception cref="InvalidOperationException">The script has run out of steps.</exception>
    public IAsyncEnumerable<T> Next(CancellationToken cancellationToken = default)
    {
        var index = Interlocked.Increment(ref _served) - 1;

        if (index >= _steps.Count)
            throw new InvalidOperationException(
                $"The scripted stream has {_steps.Count} step(s) and attempt {index + 1} asked for one more." +
                (_hasPendingDelay && _pendingDelay > TimeSpan.Zero
                    ? " A trailing Delay() was scripted with no Yields(), Empty(), Throws() or FaultsAfter() after it, so it is not a step."
                    : " Script the attempts the policy will actually make - retries included."));

        return new ServedSource(this, _steps[index], cancellationToken);
    }

    private TimeSpan TakePendingDelay()
    {
        var delay = _pendingDelay;
        _pendingDelay = TimeSpan.Zero;
        _hasPendingDelay = false;
        return delay;
    }

    private readonly struct Step(TimeSpan delay, T[] elements, Exception? startFault, Exception? midFault)
    {
        public TimeSpan Delay { get; } = delay;

        public T[] Elements { get; } = elements;

        public Exception? StartFault { get; } = startFault;

        public Exception? MidFault { get; } = midFault;
    }

    /// <summary>
    ///     One attempt's cold source, handing out one enumerator per <c>GetAsyncEnumerator</c> call.
    ///     Not an async iterator method: the counters have to observe construction and disposal
    ///     symmetrically, and a class implementing <c>IAsyncEnumerable</c> directly does that without
    ///     an iterator box in the way. Combining the two tokens is therefore this type's job rather
    ///     than the compiler's, and it is done on the compiler's rules.
    /// </summary>
    private sealed class ServedSource(ScriptedStream<T> owner, Step step, CancellationToken factoryToken) : IAsyncEnumerable<T>
    {
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken token = default)
        {
            // The three cases [EnumeratorCancellation] generates, in the same order and for the same
            // reason: only the last of them is worth a linked source, and the policy - which passes
            // the attempt token to both - lands on the second.
            if (!factoryToken.CanBeCanceled)
                return ServedEnumerator.Track(owner, step, token, null);

            if (!token.CanBeCanceled || token == factoryToken)
                return ServedEnumerator.Track(owner, step, factoryToken, null);

            var linked = CancellationTokenSource.CreateLinkedTokenSource(factoryToken, token);
            return ServedEnumerator.Track(owner, step, linked.Token, linked);
        }
    }

    /// <summary>
    ///     One enumerator over one step. The step's delay is served against the script's clock but
    ///     observes the token the enumerator was given - the attempt's token, which is what a
    ///     timed-out attempt is supposed to stop on, and which is what makes attempt ceilings testable
    ///     against a fake clock.
    /// </summary>
    private sealed class ServedEnumerator(ScriptedStream<T> owner, Step step, CancellationToken token, CancellationTokenSource? linked)
        : IAsyncEnumerator<T>
    {
        private int _index;

        private bool _started;

        private bool _midFaultThrown;

        private int _disposed;

        public T Current { get; private set; } = default!;

        public async ValueTask<bool> MoveNextAsync()
        {
            if (!_started)
            {
                _started = true;

                if (step.Delay > TimeSpan.Zero)
                    await Task.Delay(step.Delay, owner._time, token).ConfigureAwait(false);
            }

            if (_index < step.Elements.Length)
            {
                Current = step.Elements[_index++];
                return true;
            }

            if (step.StartFault is not null)
                ExceptionDispatchInfo.Throw(step.StartFault);

            if (step.MidFault is not null && !_midFaultThrown)
            {
                // Exactly once: the pull that asked for the element past the script. What the consumer
                // does after the fault - pull again, dispose, abandon - is the consumer's business.
                _midFaultThrown = true;
                ExceptionDispatchInfo.Throw(step.MidFault);
            }

            return false;
        }

        /// <summary>
        ///     Idempotent, because the counters are the whole point of this double and a second
        ///     disposal would corrupt them into reporting a leak that is not there. Disposing twice
        ///     is legal for an <see cref="IAsyncDisposable" /> and does happen: a consumer with its
        ///     own <c>await using</c> around an enumerator the streaming path also owns disposes it
        ///     once each.
        /// </summary>
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return ValueTask.CompletedTask;

            linked?.Dispose();
            Interlocked.Decrement(ref owner._live);
            Interlocked.Increment(ref owner._disposed);
            return ValueTask.CompletedTask;
        }

        internal static ServedEnumerator Track(ScriptedStream<T> owner, Step step, CancellationToken token, CancellationTokenSource? linked)
        {
            var enumerator = new ServedEnumerator(owner, step, token, linked);
            Interlocked.Increment(ref owner._live);
            return enumerator;
        }
    }
}