using NResilience.Internal;

namespace NResilience;

/// <summary>How much randomness to apply to a computed delay.</summary>
public enum Jitter
{
    /// <summary>
    /// <c>random(0, computed)</c>. The default, and the only shape that actually destroys the
    /// correlation between clients - a narrow band around a shared base still leaves a
    /// synchronized pulse.
    /// </summary>
    Full,

    /// <summary><c>computed/2 + random(0, computed/2)</c>. Keeps a floor under the delay.</summary>
    Equal,

    /// <summary>No randomness. Only correct in tests, and rarely there.</summary>
    None,
}

/// <summary>The delay between one attempt and the next.</summary>
public readonly record struct Backoff
{
    internal const double DefaultFactor = 2.0;

    internal static readonly TimeSpan DefaultTransientBase = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan DefaultThrottledBase = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan DefaultMax = TimeSpan.FromSeconds(30);

    private readonly BackoffKind _kind;
    private readonly TimeSpan _transientBase;
    private readonly TimeSpan _throttledBase;
    private readonly double _factor;
    private readonly TimeSpan _max;
    private readonly Func<NextAttempt, TimeSpan>? _custom;

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
    /// Exponential with full jitter, a 100 ms transient base, a 1 s throttled base and a hard
    /// 30 s cap.
    /// </summary>
    public static Backoff Default { get; } = Exponential();

    /// <summary>Retry immediately. Correct only when the caller knows the dependency is not shared.</summary>
    public static Backoff None { get; } = new(BackoffKind.Constant, TimeSpan.Zero, TimeSpan.Zero, DefaultFactor, TimeSpan.Zero, null);

    /// <summary>
    /// Exponential backoff with separate base delays per retryable verdict, because throttling and
    /// transient failure need curves an order of magnitude apart: a base tuned for connection
    /// resets is a hostile retry rate against a rate limiter.
    /// </summary>
    /// <param name="transientBase">Base delay for <see cref="VerdictKind.Transient"/>. Defaults to 100 ms.</param>
    /// <param name="throttledBase">Base delay for <see cref="VerdictKind.Throttled"/>. Defaults to 1 s.</param>
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

    /// <summary>The same delay every time.</summary>
    /// <param name="delay">The delay.</param>
    /// <returns>The configured backoff.</returns>
    public static Backoff Constant(TimeSpan delay) => new(BackoffKind.Constant, delay, delay, DefaultFactor, delay, null);

    /// <summary>Compute the delay yourself.</summary>
    /// <param name="compute">
    /// Given the attempt that is about to happen - including the verdict and exception that ended
    /// the previous one, and the time left on the deadline - returns the delay before it.
    /// </param>
    /// <returns>The configured backoff.</returns>
    public static Backoff Custom(Func<NextAttempt, TimeSpan> compute)
    {
        ArgumentNullException.ThrowIfNull(compute);
        return new(BackoffKind.Custom, TimeSpan.Zero, TimeSpan.Zero, DefaultFactor, Timeout.InfiniteTimeSpan, compute);
    }

    /// <summary>How much randomness to apply. <see cref="Jitter.Full"/> by default.</summary>
    public Jitter Jitter { get; init; }

    /// <summary>The hard cap on any single delay, or <see cref="Timeout.InfiniteTimeSpan"/> for none.</summary>
    public TimeSpan Max => _kind == BackoffKind.Custom ? _max : Normalized()._max;

    /// <summary>
    /// The delay before <paramref name="next"/>.
    /// <para>
    /// Server pushback wins over every curve: when the previous verdict carried a
    /// <see cref="Verdict.RetryAfter"/>, that value is honored verbatim (capped by
    /// <see cref="Max"/>) and no jitter is applied - a server telling you when to come back is
    /// strictly better information than a client-side guess.
    /// </para>
    /// </summary>
    /// <param name="next">The attempt that is about to happen.</param>
    /// <returns>The delay, never negative.</returns>
    public TimeSpan Compute(in NextAttempt next)
    {
        Backoff effective = Normalized();

        if (effective._kind == BackoffKind.Custom)
        {
            TimeSpan custom = effective._custom!(next);
            return custom > TimeSpan.Zero ? custom : TimeSpan.Zero;
        }

        if (next.PreviousVerdict.RetryAfter is { } pushback)
        {
            TimeSpan capped = effective._max != Timeout.InfiniteTimeSpan && pushback > effective._max ? effective._max : pushback;
            return capped > TimeSpan.Zero ? capped : TimeSpan.Zero;
        }

        TimeSpan @base = next.PreviousVerdict.Kind == VerdictKind.Throttled ? effective._throttledBase : effective._transientBase;
        if (@base <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        double ticks = effective._kind == BackoffKind.Constant
            ? @base.Ticks
            : @base.Ticks * Math.Pow(effective._factor, Math.Max(0, next.Number - 2));

        if (effective._max != Timeout.InfiniteTimeSpan)
        {
            ticks = Math.Min(ticks, effective._max.Ticks);
        }

        ticks = effective.Jitter switch
        {
            Jitter.Full => ticks * Rng.NextDouble(),
            Jitter.Equal => (ticks / 2) + (ticks / 2 * Rng.NextDouble()),
            _ => ticks,
        };

        return ticks <= 0 ? TimeSpan.Zero : TimeSpan.FromTicks((long)ticks);
    }

    internal void Validate(List<string> problems)
    {
        Backoff effective = Normalized();
        if (effective._kind == BackoffKind.Exponential && effective._factor <= 0)
        {
            problems.Add($"Backoff factor must be greater than zero; it is {effective._factor}.");
        }

        if (effective._transientBase < TimeSpan.Zero)
        {
            problems.Add($"Backoff transient base delay must not be negative; it is {effective._transientBase}.");
        }

        if (effective._throttledBase < TimeSpan.Zero)
        {
            problems.Add($"Backoff throttled base delay must not be negative; it is {effective._throttledBase}.");
        }

        if (effective._max < TimeSpan.Zero && effective._max != Timeout.InfiniteTimeSpan)
        {
            problems.Add($"Backoff maximum delay must not be negative; it is {effective._max}.");
        }
    }

    /// <summary>
    /// <c>default(Backoff)</c> has to behave, because <c>policy with { Backoff = default }</c>
    /// compiles. An unconstructed value is identified by its zero growth factor - every factory
    /// sets a positive one - and reads as <see cref="Default"/>.
    /// </summary>
    private Backoff Normalized() =>
        _kind == BackoffKind.Exponential && _factor == 0
            ? new(BackoffKind.Exponential, DefaultTransientBase, DefaultThrottledBase, DefaultFactor, DefaultMax, null) { Jitter = Jitter }
            : this;

    private enum BackoffKind
    {
        Exponential,
        Constant,
        Custom,
    }
}
