namespace NResilience;

/// <summary>
///     The four things an outcome can be. Everything the executor does after an attempt returns
///     is derived from this value, and from nothing else.
/// </summary>
public enum VerdictKind : byte
{
    /// <summary>The call worked. Return it.</summary>
    Ok,

    /// <summary>
    ///     A failure that may not recur. Retried with the short backoff curve, and the only verdict
    ///     that is evidence about the dependency's health.
    /// </summary>
    Transient,

    /// <summary>
    ///     The dependency is defending itself. Retried with the long backoff curve, or with the
    ///     server's own <c>Retry-After</c> when it supplied one, and never counted as a failure
    ///     against the dependency.
    /// </summary>
    Throttled,

    /// <summary>A failure that will recur. Never retried.</summary>
    Permanent,
}

/// <summary>
///     One classification of one outcome. Produced by a <see cref="Classifier" />, or by the executor
///     itself for the three cases a user predicate must not be able to get wrong: its own attempt
///     timeout, caller cancellation, and local admission control refusing the attempt.
/// </summary>
public readonly struct Verdict : IEquatable<Verdict>
{
    /// <summary>
    ///     The top bit of <see cref="_packed" />, carrying <see cref="SelfImposed" />.
    ///     <para>
    ///         The flag shares the kind's byte rather than sitting beside it in a <c>bool</c> field. The
    ///         obvious version measured 32 bytes against this one's 24: the runtime's automatic layout does
    ///         not pack a <c>bool</c> into the padding a single-byte enum leaves in front of a nullable
    ///         <see cref="TimeSpan" />. A verdict is live across the attempt <c>await</c>, so those eight
    ///         bytes would be paid for in the state-machine box of every suspending call in the library,
    ///         whether or not anything is ever rate limited. Gated by
    ///         <c>The_verdict_carries_its_origin_for_free</c>.
    ///     </para>
    ///     <para>
    ///         Four of the byte's 256 values are <see cref="VerdictKind" /> members, which is the same spare
    ///         capacity <c>AttemptRecord</c> exploits to carry the flag in the inline attempt log for
    ///         nothing.
    ///     </para>
    ///     <para>
    ///         This is the pattern to reach for before spending a byte on the next single-bit fact about a
    ///         verdict: <see cref="VerdictKind" />'s four members only occupy bits 0-1, and this flag takes
    ///         bit 7, so bits 2-6 are unclaimed and free for a future flag of the same shape - packed into
    ///         <c>_packed</c> here, and into the matching bits of <c>AttemptRecord</c>'s inline field, at
    ///         zero marginal cost to the state-machine box.
    ///     </para>
    /// </summary>
    internal const byte SelfImposedFlag = 0x80;

    private readonly byte _packed;

    private Verdict(VerdictKind kind, TimeSpan? retryAfter, bool selfImposed = false)
    {
        _packed = (byte)((byte)kind | (selfImposed ? SelfImposedFlag : 0));
        RetryAfter = retryAfter;
    }

    /// <summary>What kind of outcome this is.</summary>
    public VerdictKind Kind => (VerdictKind)(byte)(_packed & ~SelfImposedFlag);

    /// <summary>
    ///     Server pushback, honored verbatim in preference to any backoff curve, and capped only by
    ///     the backoff maximum and the time left on the deadline. Null when the server said nothing.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>
    ///     True when this verdict came from inside this process rather than from the dependency: local
    ///     admission control refused to start the attempt, so nothing reached the dependency and
    ///     nothing was learned about its health.
    ///     <para>
    ///         The <see cref="RetryBudget" /> is not charged for a self-imposed refusal. A retry that never
    ///         left the process costs the dependency nothing, so funding it out of a budget expressed as a
    ///         fraction of the dependency's own traffic would throttle the retries that do matter.
    ///     </para>
    ///     <para>
    ///         False is the conservative answer, and it is what <c>default</c> reports. That is why the
    ///         property is spelled this way round rather than as "reached the dependency": a
    ///         default-constructed verdict must not be able to claim exemption from the budget.
    ///     </para>
    /// </summary>
    public bool SelfImposed => (_packed & SelfImposedFlag) != 0;

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

    /// <summary>
    ///     Local admission control refused the attempt: a rate limiter, a concurrency limit, or anything
    ///     else in this process that said no before the call left it.
    ///     <para>
    ///         Throttling, because that is what it is - retried on the long backoff curve, honoring
    ///         <paramref name="retryAfter" /> verbatim when the limiter supplied one. It is never counted as
    ///         evidence against the dependency, and never charged to the retry budget; see
    ///         <see cref="SelfImposed" />.
    ///     </para>
    /// </summary>
    /// <param name="retryAfter">When the limiter said a permit would be available, if it said.</param>
    /// <returns>A self-imposed throttled verdict.</returns>
    public static Verdict Limited(TimeSpan? retryAfter = null) => new(VerdictKind.Throttled, retryAfter, true);

    /// <summary>
    ///     An alias for <see cref="Limited" />, for a classifier rule that is not a rate limiter. Both
    ///     produce the identical verdict; this name is for a hand-rolled admission-control guard - a
    ///     distributed lock, a consensus check, a load shedder - where "limited" would misdescribe what
    ///     refused the attempt. See the admission control deep dive for the full pattern.
    /// </summary>
    /// <param name="retryAfter">When the guard said it would allow another attempt, if it said.</param>
    /// <returns>A self-imposed throttled verdict.</returns>
    public static Verdict Refused(TimeSpan? retryAfter = null) => Limited(retryAfter);

    /// <inheritdoc />
    public bool Equals(Verdict other) => _packed == other._packed && RetryAfter == other.RetryAfter;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Verdict other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_packed, RetryAfter);

    /// <summary>Value equality.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>True when both verdicts have the same kind, pushback and origin.</returns>
    public static bool operator ==(Verdict left, Verdict right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    /// <returns>True when the verdicts differ.</returns>
    public static bool operator !=(Verdict left, Verdict right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => (SelfImposed, RetryAfter) switch
    {
        (true, { } after) => $"{Kind} (self-imposed, retry after {after.TotalSeconds:0.###}s)",
        (true, null) => $"{Kind} (self-imposed)",
        (false, { } after) => $"{Kind} (retry after {after.TotalSeconds:0.###}s)",
        _ => Kind.ToString(),
    };
}
