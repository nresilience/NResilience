namespace NResilience;

/// <summary>
/// The four things an outcome can be. Everything the executor does after an attempt returns
/// is derived from this value, and from nothing else.
/// </summary>
public enum VerdictKind : byte
{
    /// <summary>The call worked. Return it.</summary>
    Ok,

    /// <summary>
    /// A failure that may not recur. Retried with the short backoff curve, and the only verdict
    /// that is evidence about the dependency's health.
    /// </summary>
    Transient,

    /// <summary>
    /// The dependency is defending itself. Retried with the long backoff curve, or with the
    /// server's own <c>Retry-After</c> when it supplied one, and never counted as a failure
    /// against the dependency.
    /// </summary>
    Throttled,

    /// <summary>A failure that will recur. Never retried.</summary>
    Permanent,
}

/// <summary>
/// One classification of one outcome. Produced by a <see cref="Classifier"/>, or by the executor
/// itself for the two cases a user predicate must not be able to get wrong: its own attempt
/// timeout, and caller cancellation.
/// </summary>
public readonly struct Verdict : IEquatable<Verdict>
{
    private Verdict(VerdictKind kind, TimeSpan? retryAfter)
    {
        Kind = kind;
        RetryAfter = retryAfter;
    }

    /// <summary>What kind of outcome this is.</summary>
    public VerdictKind Kind { get; }

    /// <summary>
    /// Server pushback, honoured verbatim in preference to any backoff curve, and capped only by
    /// the backoff maximum and the time left on the deadline. Null when the server said nothing.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>The call worked.</summary>
    public static Verdict Ok => new(VerdictKind.Ok, null);

    /// <summary>A failure that may not recur.</summary>
    public static Verdict Transient => new(VerdictKind.Transient, null);

    /// <summary>A failure that will recur.</summary>
    public static Verdict Permanent => new(VerdictKind.Permanent, null);

    /// <summary>The dependency is defending itself.</summary>
    /// <param name="retryAfter">When the server said to come back, if it said so.</param>
    /// <returns>A throttled verdict carrying the pushback.</returns>
    public static Verdict Throttled(TimeSpan? retryAfter = null) => new(VerdictKind.Throttled, retryAfter);

    /// <inheritdoc/>
    public bool Equals(Verdict other) => Kind == other.Kind && RetryAfter == other.RetryAfter;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Verdict other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Kind, RetryAfter);

    /// <summary>Value equality.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>True when both verdicts have the same kind and pushback.</returns>
    public static bool operator ==(Verdict left, Verdict right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>True when the verdicts differ.</returns>
    public static bool operator !=(Verdict left, Verdict right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() =>
        RetryAfter is { } after
            ? $"{Kind} (retry after {after.TotalSeconds:0.###}s)"
            : Kind.ToString();
}
