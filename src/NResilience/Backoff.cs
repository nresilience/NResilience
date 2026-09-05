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
/// <remarks>
///     <para>
///         A factory chooses the curve and <c>with</c> changes one term of it:
///         <c>Backoff.Exponential()</c> is a complete configuration, and
///         <c>Backoff.Exponential() with { MaximumDelay = TimeSpan.FromSeconds(5) }</c> is the way to change the
///         cap without restating the three knobs beside it. The defaults are supplied on read rather
///         than by a constructor, for the reason <see cref="AttemptCeiling" /> and <see cref="Hedge" />
///         give: a struct's default instance is the one thing a constructor cannot reach, and
///         <c>policy with { Backoff = default }</c> compiles.
///     </para>
///     <para>
///         <see cref="Kind" /> is the one term <c>with</c> cannot change; see its own remarks.
///     </para>
/// </remarks>
public readonly record struct Backoff
{
    internal const double DefaultFactor = 2.0;

    internal static readonly TimeSpan DefaultTransientBase = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan DefaultThrottledBase = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan DefaultMax = TimeSpan.FromSeconds(30);

    private readonly Func<NextAttempt, TimeSpan>? _custom;
    private readonly double? _factor;
    private readonly TimeSpan? _max;
    private readonly TimeSpan? _throttledBase;
    private readonly TimeSpan? _transientBase;

    private Backoff(
        BackoffKind kind,
        TimeSpan? transientBase,
        TimeSpan? throttledBase,
        double? factor,
        TimeSpan? max,
        Func<NextAttempt, TimeSpan>? custom)
    {
        Kind = kind;
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
    ///         <see cref="MaximumDelay" /> and the <see cref="Verdict.RetryAfter" /> pushback precedence are all
    ///         unchanged. See <see cref="NResilience.MeasuredBase" /> for why, and
    ///         <see cref="Measured" /> for the short way to write it.
    ///     </para>
    /// </summary>
    public MeasuredBase? MeasuredBase { get; init; }

    /// <summary>
    ///     Base delay for a <see cref="VerdictKind.Transient" /> failure. Defaults to 100 ms.
    ///     <para>
    ///         Zero on a <see cref="BackoffKind.Custom" /> curve, which computes its own delays and
    ///         never reads this.
    ///     </para>
    /// </summary>
    public TimeSpan TransientBase
    {
        get => _transientBase ?? DefaultTransientBase;
        init => _transientBase = value;
    }

    /// <summary>
    ///     Base delay for a <see cref="VerdictKind.Throttled" /> failure. Defaults to 1 s, an order of
    ///     magnitude above <see cref="TransientBase" />, because a base tuned for connection resets is
    ///     a hostile retry rate against a rate limiter.
    ///     <para>
    ///         Zero on a <see cref="BackoffKind.Custom" /> curve, which computes its own delays and
    ///         never reads this.
    ///     </para>
    /// </summary>
    public TimeSpan ThrottledBase
    {
        get => _throttledBase ?? DefaultThrottledBase;
        init => _throttledBase = value;
    }

    /// <summary>
    ///     Growth per attempt. A value of 2 - the default - doubles the delay each time; 1 makes it
    ///     constant. Must be greater than zero on an <see cref="BackoffKind.Exponential" /> curve.
    /// </summary>
    public double Factor
    {
        get => _factor ?? DefaultFactor;
        init => _factor = value;
    }

    /// <summary>
    ///     The hard cap on any single delay. Defaults to 30 s;
    ///     <see cref="Timeout.InfiniteTimeSpan" /> for none.
    /// </summary>
    public TimeSpan MaximumDelay
    {
        get => _max ?? DefaultMax;
        init => _max = value;
    }

    /// <summary>
    ///     The backoff curve. <c>default(Backoff)</c> is <see cref="BackoffKind.Exponential" />.
    /// </summary>
    /// <remarks>
    ///     The one term <c>with</c> cannot change, because a <see cref="BackoffKind.Custom" /> curve
    ///     carries the delegate that computes its delays and nothing else can supply one. A kind
    ///     switched away from the factory that built it would name a curve with nothing behind it, so
    ///     the factories are the only way to choose one.
    /// </remarks>
    public BackoffKind Kind { get; }

    /// <summary>
    ///     Exponential backoff with separate base delays per retryable verdict, because throttling and
    ///     transient failure need curves an order of magnitude apart: a base tuned for connection
    ///     resets is a hostile retry rate against a rate limiter.
    /// </summary>
    /// <param name="transientBase">Base delay for <see cref="VerdictKind.Transient" />. Defaults to 100 ms.</param>
    /// <param name="throttledBase">Base delay for <see cref="VerdictKind.Throttled" />. Defaults to 1 s.</param>
    /// <param name="factor">Growth per attempt. Defaults to 2.0.</param>
    /// <param name="maximumDelay">Hard cap on any single delay. Defaults to 30 s. Uncapped exponential backoff is not hypothetical.</param>
    /// <returns>The configured backoff.</returns>
    public static Backoff Exponential(
        TimeSpan? transientBase = null,
        TimeSpan? throttledBase = null,
        double factor = DefaultFactor,
        TimeSpan? maximumDelay = null)
        => new(BackoffKind.Exponential, transientBase, throttledBase, factor, maximumDelay, null);

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
    /// <param name="maximumDelay">Hard cap on any single delay. Defaults to 30 s.</param>
    /// <returns>The configured backoff.</returns>
    public static Backoff Measured(
        double multiple = NResilience.MeasuredBase.DefaultMultiple,
        TimeSpan? transientBase = null,
        TimeSpan? throttledBase = null,
        double factor = DefaultFactor,
        TimeSpan? maximumDelay = null)
        => Exponential(transientBase, throttledBase, factor, maximumDelay)
            with
            {
                MeasuredBase = NResilience.MeasuredBase.Times(multiple),
            };

    /// <summary>The same delay every time.</summary>
    /// <param name="delay">The delay.</param>
    /// <returns>The configured backoff.</returns>
    public static Backoff Constant(TimeSpan delay) => new(BackoffKind.Constant, delay, delay, DefaultFactor, delay, null);

    /// <summary>
    ///     Compute the delay yourself. The curve ignores <see cref="MaximumDelay" /> and <see cref="Jitter" />:
    ///     the delegate's answer is the delay, clamped only at zero.
    ///     <para>
    ///         A custom curve does not have to start from nothing. <see cref="Compute(in NextAttempt)" />
    ///         is public, so a built-in curve can be the baseline and the delegate can adjust it -
    ///         which is how you add a term to exponential backoff without reimplementing it.
    ///     </para>
    /// </summary>
    /// <param name="compute">
    ///     Given the attempt that is about to happen - including the verdict and exception that ended
    ///     the previous one, and the time left on the deadline - returns the delay before it.
    /// </param>
    /// <returns>The configured backoff.</returns>
    /// <example>
    ///     <code>
    /// // Exponential, plus a second for every attempt after the first.
    /// var baseline = Backoff.Exponential();
    /// var curve = Backoff.Custom(next => baseline.Compute(next) + TimeSpan.FromSeconds(next.Number - 1));
    /// </code>
    /// </example>
    public static Backoff Custom(Func<NextAttempt, TimeSpan> compute)
    {
        ArgumentNullException.ThrowIfNull(compute);
        return new Backoff(BackoffKind.Custom, TimeSpan.Zero, TimeSpan.Zero, DefaultFactor, Timeout.InfiniteTimeSpan, compute);
    }

    /// <summary>
    ///     Value equality over the <i>effective</i> curve, so a value that names a default explicitly
    ///     equals one that left it alone - and <c>default(Backoff)</c> equals <see cref="Default" />,
    ///     which is what it behaves as.
    /// </summary>
    /// <param name="other">The other curve.</param>
    /// <returns>True when both would compute the same delays.</returns>
    public bool Equals(Backoff other) =>
        Kind == other.Kind
        && Jitter == other.Jitter
        && TransientBase == other.TransientBase
        && ThrottledBase == other.ThrottledBase
        && Factor.Equals(other.Factor)
        && MaximumDelay == other.MaximumDelay
        && Nullable.Equals(MeasuredBase, other.MeasuredBase)
        && _custom == other._custom;

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(Kind, Jitter, TransientBase, ThrottledBase, Factor, MaximumDelay, MeasuredBase, _custom);

    /// <summary>
    ///     The delay before <paramref name="next" />.
    ///     <para>
    ///         Server pushback wins over every curve: when the previous verdict carried a
    ///         <see cref="Verdict.RetryAfter" />, that value is honored verbatim (capped by
    ///         <see cref="MaximumDelay" />) and no jitter is applied - a server telling you when to come back is
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
        if (Kind == BackoffKind.Custom)
        {
            var custom = _custom!(next);
            return custom > TimeSpan.Zero ? custom : TimeSpan.Zero;
        }

        var max = MaximumDelay;

        if (next.PreviousVerdict.RetryAfter is { } pushback)
        {
            var capped = max != Timeout.InfiniteTimeSpan && pushback > max ? max : pushback;
            return capped > TimeSpan.Zero ? capped : TimeSpan.Zero;
        }

        var throttled = next.PreviousVerdict.Kind == VerdictKind.Throttled;
        var transient = TransientBase;
        var @base = throttled ? ThrottledBase : transient;

        // Throttling is deliberately excluded: a rate limiter's refill interval is not visible in how
        // fast it said no, and the one case where the server does know is the pushback above.
        if (!throttled && normal is { } measured && MeasuredBase is { } adaptive)
            @base = adaptive.BaseFor(transient, measured);

        if (@base <= TimeSpan.Zero)
            return TimeSpan.Zero;

        var ticks = Kind == BackoffKind.Constant
            ? @base.Ticks
            : @base.Ticks * Math.Pow(Factor, Math.Max(0, next.Number - 2));

        if (max != Timeout.InfiniteTimeSpan)
            ticks = Math.Min(ticks, max.Ticks);

        ticks = Jitter switch
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
        var factor = Factor;

        if (Kind == BackoffKind.Exponential && (double.IsNaN(factor) || factor <= 0))
            problems.Add($"Backoff factor must be greater than zero; it is {factor}.");

        if (TransientBase < TimeSpan.Zero)
            problems.Add($"Backoff transient base delay must not be negative; it is {TransientBase}.");

        if (ThrottledBase < TimeSpan.Zero)
            problems.Add($"Backoff throttled base delay must not be negative; it is {ThrottledBase}.");

        if (MaximumDelay < TimeSpan.Zero && MaximumDelay != Timeout.InfiniteTimeSpan)
            problems.Add($"Backoff maximum delay must not be negative; it is {MaximumDelay}.");

        if (MeasuredBase is { } measured)
        {
            measured.Validate(problems);

            // A measured base replaces the transient base of a curve that has one. A Constant curve's
            // single delay is also its cap, and a Custom curve computes everything itself - measuring
            // into either would be a value the caller cannot predict from what they wrote. Refused
            // rather than ignored, because silently doing nothing is how a caller ends up believing
            // their backoff tracks the dependency when it does not.
            if (Kind != BackoffKind.Exponential)
            {
                problems.Add(
                    $"Backoff.MeasuredBase is only supported on an exponential curve; this one is {Kind}. " +
                    "Use Backoff.Measured(...) to build one, or drop MeasuredBase to keep the curve as written.");
            }
        }
    }
}
