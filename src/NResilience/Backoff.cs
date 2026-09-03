using NResilience.Internal;

namespace NResilience;

/// <summary>How much randomness to apply to a computed delay.</summary>
public enum Jitter
{
    /// <summary>
    ///     <c>random(0, computed)</c>. The default, and the only shape that actually destroys the
    ///     correlation between clients - a narrow band around a shared base still leaves a
    ///     synchronized pulse.
    /// </summary>
    Full,

    /// <summary><c>computed/2 + random(0, computed/2)</c>. Keeps a floor under the delay.</summary>
    Equal,

    /// <summary>No randomness. Only correct in tests, and rarely there.</summary>
    None,
}

/// <summary>Which curve a <see cref="Backoff" /> follows.</summary>
public enum BackoffKind
{
    /// <summary>The delay grows by <see cref="Backoff.Factor" /> on each attempt. See <see cref="Backoff.Exponential" />.</summary>
    Exponential,

    /// <summary>The same delay every time. See <see cref="Backoff.Constant" /> and <see cref="Backoff.None" />.</summary>
    Constant,

    /// <summary>A caller-supplied delegate computes the delay. See <see cref="Backoff.Custom" />.</summary>
    Custom,
}

/// <summary>The delay between one attempt and the next.</summary>
public readonly record struct Backoff
{
    internal const double DefaultFactor = 2.0;

    internal static readonly TimeSpan DefaultTransientBase = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan DefaultThrottledBase = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan DefaultMax = TimeSpan.FromSeconds(30);
    private readonly Func<NextAttempt, TimeSpan>? _custom;
    private readonly double _factor;

    private readonly BackoffKind _kind;
    private readonly TimeSpan _max;
    private readonly TimeSpan _throttledBase;
    private readonly TimeSpan _transientBase;

    private Backoff(BackoffKind kind, TimeSpan transientBase, TimeSpan throttledBase, double factor, TimeSpan max, Func<NextAttempt, TimeSpan>? custom)
    {
        _kind = kind;
        _transientBase = transientBase;
        _throttledBase = throttledBase;
        _factor = factor;
        _max = max;
        _custom = custom;
    }

    /// <summary>
    ///     Exponential with full jitter, a 100 ms transient base, a 1 s throttled base and a hard
    ///     30 s cap.
    /// </summary>
    public static Backoff Default { get; } = Exponential();

    /// <summary>Retry immediately. Correct only when the caller knows the dependency is not shared.</summary>
    public static Backoff None { get; } = new(BackoffKind.Constant, TimeSpan.Zero, TimeSpan.Zero, DefaultFactor, TimeSpan.Zero, null);

    /// <summary>How much randomness to apply. <see cref="Jitter.Full" /> by default.</summary>
    public Jitter Jitter { get; init; }

    /// <summary>
    ///     Measures <see cref="TransientBase" /> from the dependency's own recent latency instead of
    ///     taking it as a constant. Null - the default - leaves the configured base alone.
    ///     <para>
    ///         Only <see cref="BackoffKind.Exponential" /> curves can carry one, and it moves only the
    ///         transient base: <see cref="ThrottledBase" />, <see cref="Factor" />, <see cref="Jitter" />,
    ///         <see cref="Max" /> and the <see cref="Verdict.RetryAfter" /> pushback precedence are all
    ///         unchanged. See <see cref="NResilience.MeasuredBase" /> for why, and
    ///         <see cref="Measured" /> for the short way to write it.
    ///     </para>
    /// </summary>
    public MeasuredBase? MeasuredBase { get; init; }

    /// <summary>
    ///     Base delay for a <see cref="VerdictKind.Transient" /> failure. Returns zero for
    ///     <see cref="BackoffKind.Custom" /> curves because they compute their own delays.
    /// </summary>
    public TimeSpan TransientBase => Normalized()._transientBase;

    /// <summary>
    ///     Base delay for a <see cref="VerdictKind.Throttled" /> failure. Returns zero for a
    ///     <see cref="BackoffKind.Custom" /> curve because it computes its own delays.
    /// </summary>
    public TimeSpan ThrottledBase => Normalized()._throttledBase;

    /// <summary>Growth per attempt. A value of 2 doubles the delay each time; 1 makes it constant.</summary>
    public double Factor => Normalized()._factor;

    /// <summary>The backoff curve. <c>default(Backoff)</c> is <see cref="BackoffKind.Exponential" />.</summary>
    public BackoffKind Kind => _kind;

    /// <summary>The hard cap on any single delay, or <see cref="Timeout.InfiniteTimeSpan" /> for none.</summary>
    public TimeSpan Max => Normalized()._max;

    /// <summary>
    ///     Exponential backoff with separate base delays per retryable verdict, because throttling and
    ///     transient failure need curves an order of magnitude apart: a base tuned for connection
    ///     resets is a hostile retry rate against a rate limiter.
    /// </summary>
    /// <param name="transientBase">Base delay for <see cref="VerdictKind.Transient" />. Defaults to 100 ms.</param>
    /// <param name="throttledBase">Base delay for <see cref="VerdictKind.Throttled" />. Defaults to 1 s.</param>
    /// <param name="factor">Growth per attempt. Defaults to 2.0.</param>
    /// <param name="max">Hard cap on any single delay. Defaults to 30 s. Uncapped exponential backoff is not hypothetical.</param>
    /// <returns>The configured backoff.</returns>
    public static Backoff Exponential(
        TimeSpan? transientBase = null,
        TimeSpan? throttledBase = null,
        double factor = DefaultFactor,
        TimeSpan? max = null)
        => new(
            BackoffKind.Exponential,
            transientBase ?? DefaultTransientBase,
            throttledBase ?? DefaultThrottledBase,
            factor,
            max ?? DefaultMax,
            null);

    /// <summary>
    ///     Exponential backoff whose transient base is measured from the dependency's own recent
    ///     latency rather than guessed, clamped to a factor either side of
    ///     <paramref name="transientBase" />. Everything else - the throttled base, the factor, the
    ///     jitter, the cap and the <see cref="Verdict.RetryAfter" /> pushback precedence - is
    ///     <see cref="Exponential" />'s.
    /// </summary>
    /// <param name="multiple">How many normal calls the first retry waits. Defaults to 1.</param>
    /// <param name="transientBase">The base the measurement is clamped around. Defaults to 100 ms.</param>
    /// <param name="throttledBase">Base delay for <see cref="VerdictKind.Throttled" />, which is never measured. Defaults to 1 s.</param>
    /// <param name="factor">Growth per attempt. Defaults to 2.0.</param>
    /// <param name="max">Hard cap on any single delay. Defaults to 30 s.</param>
    /// <returns>The configured backoff.</returns>
    public static Backoff Measured(
        double multiple = NResilience.MeasuredBase.DefaultMultiple,
        TimeSpan? transientBase = null,
        TimeSpan? throttledBase = null,
        double factor = DefaultFactor,
        TimeSpan? max = null)
        => Exponential(transientBase, throttledBase, factor, max)
            with { MeasuredBase = NResilience.MeasuredBase.Of(multiple) };

    /// <summary>The same delay every time.</summary>
    /// <param name="delay">The delay.</param>
    /// <returns>The configured backoff.</returns>
    public static Backoff Constant(TimeSpan delay) => new(BackoffKind.Constant, delay, delay, DefaultFactor, delay, null);

    /// <summary>Compute the delay yourself.</summary>
    /// <param name="compute">
    ///     Given the attempt that is about to happen - including the verdict and exception that ended
    ///     the previous one, and the time left on the deadline - returns the delay before it.
    /// </param>
    /// <returns>The configured backoff.</returns>
    public static Backoff Custom(Func<NextAttempt, TimeSpan> compute)
    {
        ArgumentNullException.ThrowIfNull(compute);
        return new Backoff(BackoffKind.Custom, TimeSpan.Zero, TimeSpan.Zero, DefaultFactor, Timeout.InfiniteTimeSpan, compute);
    }

    /// <summary>
    ///     The delay before <paramref name="next" />.
    ///     <para>
    ///         Server pushback wins over every curve: when the previous verdict carried a
    ///         <see cref="Verdict.RetryAfter" />, that value is honored verbatim (capped by
    ///         <see cref="Max" />) and no jitter is applied - a server telling you when to come back is
    ///         strictly better information than a client-side guess.
    ///     </para>
    /// </summary>
    /// <param name="next">The attempt that is about to happen.</param>
    /// <returns>The delay, never negative.</returns>
    /// <remarks>
    ///     A curve carrying a <see cref="MeasuredBase" /> computes its <i>unmeasured</i> delay here.
    ///     The estimate is private to the policy instance that owns it, so only the executor can supply
    ///     it, and a caller asking a bare <see cref="Backoff" /> value what it would do gets the
    ///     configured curve - the same answer the executor gives while the estimate is still cold.
    /// </remarks>
    public TimeSpan Compute(in NextAttempt next) => Compute(next, null);

    /// <summary>
    ///     The delay before <paramref name="next" />, given what a normal call to this dependency
    ///     recently took.
    /// </summary>
    /// <param name="next">The attempt that is about to happen.</param>
    /// <param name="normal">
    ///     The measured baseline, already gated on <see cref="MeasuredBase.MinimumSamples" />, or null
    ///     when nothing is measuring or the estimate is still cold.
    /// </param>
    /// <returns>The delay, never negative.</returns>
    internal TimeSpan Compute(in NextAttempt next, TimeSpan? normal)
    {
        var effective = Normalized();

        if (effective._kind == BackoffKind.Custom)
        {
            var custom = effective._custom!(next);
            return custom > TimeSpan.Zero ? custom : TimeSpan.Zero;
        }

        if (next.PreviousVerdict.RetryAfter is { } pushback)
        {
            var capped = effective._max != Timeout.InfiniteTimeSpan && pushback > effective._max ? effective._max : pushback;
            return capped > TimeSpan.Zero ? capped : TimeSpan.Zero;
        }

        var throttled = next.PreviousVerdict.Kind == VerdictKind.Throttled;
        var @base = throttled ? effective._throttledBase : effective._transientBase;

        // Throttling is deliberately excluded: a rate limiter's refill interval is not visible in how
        // fast it said no, and the one case where the server does know is the pushback above.
        if (!throttled && normal is { } measured && effective.MeasuredBase is { } adaptive)
            @base = adaptive.BaseFor(effective._transientBase, measured);

        if (@base <= TimeSpan.Zero)
            return TimeSpan.Zero;

        var ticks = effective._kind == BackoffKind.Constant
            ? @base.Ticks
            : @base.Ticks * Math.Pow(effective._factor, Math.Max(0, next.Number - 2));

        if (effective._max != Timeout.InfiniteTimeSpan)
            ticks = Math.Min(ticks, effective._max.Ticks);

        ticks = effective.Jitter switch
        {
            Jitter.Full => ticks * Rng.NextDouble(),
            Jitter.Equal => ticks / 2 + ticks / 2 * Rng.NextDouble(),
            _ => ticks,
        };

        // An uncapped exponential (max is InfiniteTimeSpan) with a large attempt count can push
        // ticks past double.MaxValue's representable range as a TimeSpan, and a NaN factor would
        // propagate through every operation above. Either reaches TimeSpan.FromTicks as a value
        // that wraps to a negative duration, so clamp before the conversion rather than after.
        if (!double.IsFinite(ticks) || ticks <= 0)
            return TimeSpan.Zero;

        var clamped = ticks > long.MaxValue ? long.MaxValue : (long)ticks;
        return TimeSpan.FromTicks(clamped);
    }

    internal void Validate(List<string> problems)
    {
        var effective = Normalized();

        if (effective._kind == BackoffKind.Exponential && (double.IsNaN(effective._factor) || effective._factor <= 0))
            problems.Add($"Backoff factor must be greater than zero; it is {effective._factor}.");

        if (effective._transientBase < TimeSpan.Zero)
            problems.Add($"Backoff transient base delay must not be negative; it is {effective._transientBase}.");

        if (effective._throttledBase < TimeSpan.Zero)
            problems.Add($"Backoff throttled base delay must not be negative; it is {effective._throttledBase}.");

        if (effective._max < TimeSpan.Zero && effective._max != Timeout.InfiniteTimeSpan)
            problems.Add($"Backoff maximum delay must not be negative; it is {effective._max}.");

        if (MeasuredBase is { } measured)
        {
            measured.Validate(problems);

            // A measured base replaces the transient base of a curve that has one. A Constant curve's
            // single delay is also its cap, and a Custom curve computes everything itself - measuring
            // into either would be a value the caller cannot predict from what they wrote. Refused
            // rather than ignored, because silently doing nothing is how a caller ends up believing
            // their backoff tracks the dependency when it does not.
            if (effective._kind != BackoffKind.Exponential)
            {
                problems.Add(
                    $"Backoff.MeasuredBase is only supported on an exponential curve; this one is {effective._kind}. " +
                    "Use Backoff.Measured(...) to build one, or drop MeasuredBase to keep the curve as written.");
            }
        }
    }

    /// <summary>
    ///     <c>default(Backoff)</c> has to behave, because <c>policy with { Backoff = default }</c>
    ///     compiles. An unconstructed value is identified by its zero growth factor - every factory
    ///     sets a positive one - and reads as <see cref="Default" />.
    /// </summary>
    private Backoff Normalized() =>
        _kind == BackoffKind.Exponential && _factor == 0
            ? new Backoff(BackoffKind.Exponential, DefaultTransientBase, DefaultThrottledBase, DefaultFactor, DefaultMax, null)
            {
                Jitter = Jitter,
                MeasuredBase = MeasuredBase,
            }
            : this;
}
