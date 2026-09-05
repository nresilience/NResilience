namespace NResilience.Testing;

/// <summary>
///     Entry point for the scripted callback. <see cref="For{T}(TimeProvider)" /> starts a script of
///     results, exceptions and delays that a policy can execute against.
/// </summary>
public static class Sequence
{
    /// <summary>
    ///     Starts a script of <typeparamref name="T" />-returning calls.
    /// </summary>
    /// <typeparam name="T">What the scripted callback returns.</typeparam>
    /// <param name="time">
    ///     The clock <see cref="Sequence{T}.Delays" /> is served against. Pass the same
    ///     <c>FakeTimeProvider</c> the policy was given, or a scripted delay is a real sleep - which is
    ///     the flakiness this package exists to remove. Defaults to <see cref="TimeProvider.System" />.
    /// </param>
    /// <example>
    ///     <code>
    /// var calls = Sequence.For&lt;HttpResponseMessage&gt;()
    ///     .Returns(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
    ///     .Returns(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
    ///     .Returns(new HttpResponseMessage(HttpStatusCode.OK));
    /// 
    /// var result = await policy.TryRunAsync(attempt => calls.NextAsync(attempt), cancellationToken);
    /// </code>
    /// </example>
    public static Sequence<T> For<T>(TimeProvider? time = null) => new(time ?? TimeProvider.System);

    /// <summary>
    ///     Starts a script of calls that return nothing, so the void execution overloads can be
    ///     scripted the same way.
    /// </summary>
    /// <param name="time">The clock delays are served against. See <see cref="For{T}(TimeProvider)" />.</param>
    /// <example>
    ///     <code>
    /// var calls = Sequence.ForVoid().Throws(new TimeoutException()).Returns();
    /// 
    /// var result = await policy.TryRunAsync(attempt =&gt; calls.NextAsync(attempt), cancellationToken);
    /// </code>
    /// </example>
    public static VoidSequence ForVoid(TimeProvider? time = null) => new(time ?? TimeProvider.System);
}

/// <summary>
///     The result type of a scripted callback that returns nothing, so <see cref="VoidSequence" /> can
///     be one line over <see cref="Sequence{T}" /> rather than a second copy of it.
/// </summary>
/// <remarks>
///     Internal, and named for the conventional one-valued type rather than <c>Void</c>, which would
///     read as <see cref="System.Void" /> - the type no value ever has, where this is the opposite.
///     Nothing published names it: <see cref="Sequence.ForVoid" /> hands back
///     <see cref="VoidSequence" />, whose members take and return nothing.
/// </remarks>
internal readonly struct Unit
{
}

/// <summary>
///     A scripted callback that returns nothing: the void-shaped twin of <see cref="Sequence{T}" />,
///     for the execution overloads whose callback returns a bare <see cref="Task" />.
/// </summary>
/// <remarks>
///     A type of its own rather than <c>Sequence&lt;T&gt;</c> with a type argument nobody can name.
///     It also settles overload resolution: <c>Task&lt;T&gt;</c> is a <c>Task</c>, so a script whose
///     <c>NextAsync</c> returned <c>Task&lt;T&gt;</c> would always bind to the generic execution
///     overload and a void call could never be scripted at all.
/// </remarks>
public sealed class VoidSequence
{
    private readonly Sequence<Unit> _inner;

    internal VoidSequence(TimeProvider time)
    {
        _inner = new Sequence<Unit>(time);
    }

    /// <summary>How many times <see cref="NextAsync" /> has been called. See <see cref="Sequence{T}.CallCount" />.</summary>
    public int CallCount => _inner.CallCount;

    /// <summary>How many steps the script has left to serve.</summary>
    public int Remaining => _inner.Remaining;

    /// <summary>Appends a step that completes.</summary>
    public VoidSequence Returns()
    {
        _inner.Returns(default);
        return this;
    }

    /// <summary>Appends <paramref name="count" /> steps that each complete.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count" /> is negative.</exception>
    public VoidSequence Returns(int count)
    {
        _inner.Returns(default, count);
        return this;
    }

    /// <summary>Appends a step that throws <paramref name="exception" />.</summary>
    /// <remarks>See <see cref="Sequence{T}.Throws(Exception)" />: the same instance is thrown each time.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="exception" /> is null.</exception>
    public VoidSequence Throws(Exception exception)
    {
        _inner.Throws(exception);
        return this;
    }

    /// <summary>Appends <paramref name="count" /> steps that each throw <paramref name="exception" />.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="exception" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count" /> is negative.</exception>
    public VoidSequence Throws(Exception exception, int count)
    {
        _inner.Throws(exception, count);
        return this;
    }

    /// <summary>
    ///     Makes the next step take <paramref name="delay" /> before it completes. Accumulates, exactly
    ///     as <see cref="Sequence{T}.Delays" /> does.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delay" /> is negative.</exception>
    public VoidSequence Delays(TimeSpan delay)
    {
        _inner.Delays(delay);
        return this;
    }

    /// <summary>
    ///     Serves the next step: waits its delay if it has one, then completes or throws its exception.
    /// </summary>
    /// <param name="cancellationToken">Observed while a delay is being served. See <see cref="Sequence{T}.NextAsync" />.</param>
    /// <returns>A bare <see cref="Task" />, which is what binds the script to the void execution overloads.</returns>
    /// <exception cref="InvalidOperationException">The script has run out of steps.</exception>
    public Task NextAsync(CancellationToken cancellationToken = default) => _inner.NextAsync(cancellationToken);
}

/// <summary>
///     A scripted callback: a list of outcomes served one per call, in order.
/// </summary>
/// <typeparam name="T">What the callback returns.</typeparam>
/// <remarks>
///     <para>
///         The script is built before the call and read during it. Building is not thread-safe and is not
///         meant to be; serving is, because a policy under test may well be driven from several tasks at
///         once, and a scripted double that races is worse than no double at all.
///     </para>
///     <para>
///         A step with no delay completes synchronously, which is deliberate: it is what makes the
///         synchronous-completion path - the one the allocation budget cares most about - scriptable.
///         A step with a delay is the suspending path and honors the cancellation token, which is what
///         makes attempt timeouts and deadlines testable.
///     </para>
/// </remarks>
public sealed class Sequence<T>
{
    private readonly List<Step> _steps = [];
    private readonly TimeProvider _time;
    private bool _hasPendingDelay;
    private TimeSpan _pendingDelay;
    private int _served;

    internal Sequence(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>How many times <see cref="NextAsync" /> has been called.</summary>
    /// <remarks>
    ///     Counts calls that threw as well as calls that returned, including the call that runs off the
    ///     end of the script - a double that under-reports the call that broke the test is not worth
    ///     having.
    /// </remarks>
    public int CallCount => Volatile.Read(ref _served);

    /// <summary>How many steps the script has left to serve.</summary>
    public int Remaining => Math.Max(0, _steps.Count - CallCount);

    /// <summary>Appends a step that returns <paramref name="result" />.</summary>
    public Sequence<T> Returns(T result)
    {
        _steps.Add(new Step(TakePendingDelay(), result, null));
        return this;
    }

    /// <summary>
    ///     Appends <paramref name="count" /> steps that each return <paramref name="result" />.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count" /> is negative.</exception>
    public Sequence<T> Returns(T result, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        for (var i = 0; i < count; i++)
        {
            Returns(result);
        }

        return this;
    }

    /// <summary>Appends a step that throws <paramref name="exception" />.</summary>
    /// <remarks>
    ///     The same instance is thrown each time the step is reached, so a test can assert on reference
    ///     equality against what came back out of the policy.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="exception" /> is null.</exception>
    public Sequence<T> Throws(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        _steps.Add(new Step(TakePendingDelay(), default, exception));
        return this;
    }

    /// <summary>
    ///     Appends <paramref name="count" /> steps that each throw <paramref name="exception" />.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="exception" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count" /> is negative.</exception>
    public Sequence<T> Throws(Exception exception, int count)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        for (var i = 0; i < count; i++)
        {
            Throws(exception);
        }

        return this;
    }

    /// <summary>
    ///     Makes the next step take <paramref name="delay" /> before it produces its outcome.
    /// </summary>
    /// <remarks>
    ///     The delay attaches to the step that follows rather than standing on its own, because a step
    ///     has to produce something and a bare delay has nothing to produce. Repeated calls accumulate,
    ///     so <c>.Delays(a).Delays(b).Returns(x)</c> waits <c>a + b</c>. A trailing
    ///     <see cref="Delays" /> with no step after it is a scripting mistake and is reported as one
    ///     when the script runs out.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delay" /> is negative.</exception>
    public Sequence<T> Delays(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);

        _pendingDelay += delay;
        _hasPendingDelay = true;
        return this;
    }

    /// <summary>
    ///     Serves the next step: waits its delay if it has one, then returns its result or throws its
    ///     exception.
    /// </summary>
    /// <remarks>
    ///     The <c>Async</c> suffix is the difference from <see cref="ScriptedStream{T}.Next" />, which
    ///     carries none: this one awaits a single result, so it is a <see cref="Task{TResult}" /> the
    ///     caller awaits; the streaming double hands back a cold source the caller enumerates, and
    ///     nothing about returning it is asynchronous.
    /// </remarks>
    /// <param name="cancellationToken">
    ///     Observed while a delay is being served. A step with no delay completes synchronously and
    ///     does not observe it - see the remarks on <see cref="Sequence{T}" />.
    /// </param>
    /// <exception cref="InvalidOperationException">The script has run out of steps.</exception>
    public Task<T> NextAsync(CancellationToken cancellationToken = default)
    {
        var index = Interlocked.Increment(ref _served) - 1;

        if (index >= _steps.Count)
        {
            return Task.FromException<T>(new InvalidOperationException(
                $"The scripted sequence has {_steps.Count} step(s) and call {index + 1} asked for one more." +
                (_hasPendingDelay && _pendingDelay > TimeSpan.Zero
                    ? " A trailing Delays() was scripted with no Returns() or Throws() after it, so it is not a step."
                    : " Script the calls the policy will actually make - retries included.")));
        }

        var step = _steps[index];

        return step.Delay <= TimeSpan.Zero
            ? step.Complete()
            : DelayThenComplete(step, cancellationToken);
    }

    private async Task<T> DelayThenComplete(Step step, CancellationToken cancellationToken)
    {
        await Task.Delay(step.Delay, _time, cancellationToken).ConfigureAwait(false);
        return await step.Complete().ConfigureAwait(false);
    }

    private TimeSpan TakePendingDelay()
    {
        var delay = _pendingDelay;
        _pendingDelay = TimeSpan.Zero;
        _hasPendingDelay = false;
        return delay;
    }

    private readonly struct Step(TimeSpan delay, T? result, Exception? exception)
    {
        public TimeSpan Delay { get; } = delay;

        public Task<T> Complete() =>
            exception is null ? Task.FromResult(result!) : Task.FromException<T>(exception);
    }
}
