using NResilience.Testing.Internal;

namespace NResilience.Testing;

/// <summary>
///     Wraps a callback so a chosen fraction of its calls fail or run slowly.
///     <para>
///         Every overload returns a callback of the same shape it was given, so the result drops
///         straight into <c>RunAsync</c> or <c>TryRunAsync</c> and the policy is untouched. The
///         injected outcome is therefore classified, retried, counted against the breaker and written
///         to the attempt log exactly like a real one.
///     </para>
///     <para>
///         A profile with <see cref="Chaos.Enabled" /> false returns the callback unwrapped. That is
///         worth knowing rather than merely being an optimization: leaving <c>Inject</c> in production
///         code costs one branch at composition time and nothing per call, so the call site does not
///         have to be edited to turn a game day on and off.
///     </para>
///     <para>
///         The overloads that take an <c>outcome</c> inject a <i>result</i> the classifier will judge -
///         a 503, an empty page, a stale record - rather than an exception. That is the difference
///         between testing your exception rules and testing your result rules, and most classifiers
///         have both. They are separate overloads rather than one with an optional parameter because
///         two overloads with optional parameters is a source-compatibility hazard the public API
///         analyzer refuses, and rightly.
///     </para>
/// </summary>
public static class ChaosExtensions
{
    /// <summary>Wraps a <see cref="Task" />-returning callback. A failing call throws.</summary>
    /// <typeparam name="T">What the callback returns.</typeparam>
    /// <param name="chaos">The profile.</param>
    /// <param name="work">The callback.</param>
    /// <returns>The wrapped callback, or <paramref name="work" /> itself when the profile is disabled.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="work" /> is null.</exception>
    /// <exception cref="ResilienceConfigurationException">The profile cannot be used.</exception>
    public static Func<CancellationToken, Task<T>> Inject<T>(this Chaos chaos, Func<CancellationToken, Task<T>> work) =>
        InjectCore(chaos, work, null);

    /// <summary>Wraps a <see cref="Task" />-returning callback. A failing call returns <paramref name="outcome" />.</summary>
    /// <typeparam name="T">What the callback returns.</typeparam>
    /// <param name="chaos">The profile.</param>
    /// <param name="work">The callback.</param>
    /// <param name="outcome">
    ///     What a failing call returns instead of throwing. Called once per injected failure, so it must
    ///     produce a fresh value each time when the value owns something disposable.
    /// </param>
    /// <returns>The wrapped callback, or <paramref name="work" /> itself when the profile is disabled.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="work" /> or <paramref name="outcome" /> is null.</exception>
    /// <exception cref="ResilienceConfigurationException">The profile cannot be used.</exception>
    public static Func<CancellationToken, Task<T>> Inject<T>(this Chaos chaos, Func<CancellationToken, Task<T>> work, Func<T> outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return InjectCore(chaos, work, outcome);
    }

    /// <summary>Wraps a <see cref="Task" />-returning callback with no result.</summary>
    /// <param name="chaos">The profile.</param>
    /// <param name="work">The callback.</param>
    /// <returns>The wrapped callback, or <paramref name="work" /> itself when the profile is disabled.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="work" /> is null.</exception>
    /// <exception cref="ResilienceConfigurationException">The profile cannot be used.</exception>
    public static Func<CancellationToken, Task> Inject(this Chaos chaos, Func<CancellationToken, Task> work)
    {
        var dice = Prepare(chaos, work);

        if (dice is null)
            return work;

        return async cancellationToken =>
        {
            var roll = ChaosCore.Roll(chaos, dice);

            if (roll.Latency > TimeSpan.Zero)
                await Task.Delay(roll.Latency, chaos.Time, cancellationToken).ConfigureAwait(false);

            if (roll.Faults)
                throw ChaosCore.FaultFor(chaos);

            await work(cancellationToken).ConfigureAwait(false);
        };
    }

    /// <summary>Wraps a <see cref="ValueTask" />-returning callback. A failing call throws.</summary>
    /// <typeparam name="T">What the callback returns.</typeparam>
    /// <param name="chaos">The profile.</param>
    /// <param name="work">The callback.</param>
    /// <returns>The wrapped callback, or <paramref name="work" /> itself when the profile is disabled.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="work" /> is null.</exception>
    /// <exception cref="ResilienceConfigurationException">The profile cannot be used.</exception>
    public static Func<CancellationToken, ValueTask<T>> Inject<T>(this Chaos chaos, Func<CancellationToken, ValueTask<T>> work) =>
        InjectCore(chaos, work, null);

    /// <summary>Wraps a <see cref="ValueTask" />-returning callback. A failing call returns <paramref name="outcome" />.</summary>
    /// <typeparam name="T">What the callback returns.</typeparam>
    /// <param name="chaos">The profile.</param>
    /// <param name="work">The callback.</param>
    /// <param name="outcome">What a failing call returns instead of throwing.</param>
    /// <returns>The wrapped callback, or <paramref name="work" /> itself when the profile is disabled.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="work" /> or <paramref name="outcome" /> is null.</exception>
    /// <exception cref="ResilienceConfigurationException">The profile cannot be used.</exception>
    public static Func<CancellationToken, ValueTask<T>> Inject<T>(this Chaos chaos, Func<CancellationToken, ValueTask<T>> work, Func<T> outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return InjectCore(chaos, work, outcome);
    }

    /// <summary>Wraps a <see cref="ValueTask" />-returning callback with no result.</summary>
    /// <param name="chaos">The profile.</param>
    /// <param name="work">The callback.</param>
    /// <returns>The wrapped callback, or <paramref name="work" /> itself when the profile is disabled.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="work" /> is null.</exception>
    /// <exception cref="ResilienceConfigurationException">The profile cannot be used.</exception>
    public static Func<CancellationToken, ValueTask> Inject(this Chaos chaos, Func<CancellationToken, ValueTask> work)
    {
        var dice = Prepare(chaos, work);

        if (dice is null)
            return work;

        return async cancellationToken =>
        {
            var roll = ChaosCore.Roll(chaos, dice);

            if (roll.Latency > TimeSpan.Zero)
                await Task.Delay(roll.Latency, chaos.Time, cancellationToken).ConfigureAwait(false);

            if (roll.Faults)
                throw ChaosCore.FaultFor(chaos);

            await work(cancellationToken).ConfigureAwait(false);
        };
    }

    private static Func<CancellationToken, Task<T>> InjectCore<T>(Chaos chaos, Func<CancellationToken, Task<T>> work, Func<T>? outcome)
    {
        var dice = Prepare(chaos, work);

        if (dice is null)
            return work;

        return async cancellationToken =>
        {
            var roll = ChaosCore.Roll(chaos, dice);

            if (roll.Latency > TimeSpan.Zero)
                await Task.Delay(roll.Latency, chaos.Time, cancellationToken).ConfigureAwait(false);

            if (roll.Faults)
                return outcome is not null ? outcome() : throw ChaosCore.FaultFor(chaos);

            return await work(cancellationToken).ConfigureAwait(false);
        };
    }

    private static Func<CancellationToken, ValueTask<T>> InjectCore<T>(Chaos chaos, Func<CancellationToken, ValueTask<T>> work, Func<T>? outcome)
    {
        var dice = Prepare(chaos, work);

        if (dice is null)
            return work;

        return async cancellationToken =>
        {
            var roll = ChaosCore.Roll(chaos, dice);

            // An inert roll awaits the callback's own ValueTask and nothing else, so the allocation
            // property the ValueTask overloads exist for is not thrown away on the calls chaos leaves
            // alone - which, at a realistic rate, is almost all of them.
            if (roll.IsInert)
                return await work(cancellationToken).ConfigureAwait(false);

            if (roll.Latency > TimeSpan.Zero)
                await Task.Delay(roll.Latency, chaos.Time, cancellationToken).ConfigureAwait(false);

            if (roll.Faults)
                return outcome is not null ? outcome() : throw ChaosCore.FaultFor(chaos);

            return await work(cancellationToken).ConfigureAwait(false);
        };
    }

    /// <summary>
    ///     Validates the profile and returns its random stream, or null when the profile is disabled and
    ///     the callback should be handed back untouched.
    ///     <para>
    ///         Validation happens here rather than lazily, because the point of a chaos profile is to be
    ///         turned on under pressure, and a rate of 1.5 discovered then is discovered too late.
    ///     </para>
    /// </summary>
    private static ChaosDice? Prepare(Chaos chaos, object work)
    {
        ArgumentNullException.ThrowIfNull(chaos);
        ArgumentNullException.ThrowIfNull(work);

        chaos.Validate();

        return chaos.Enabled ? new ChaosDice(chaos.Seed) : null;
    }
}
