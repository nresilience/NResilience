namespace NResilience;

/// <summary>
///     What a policy is currently measuring, read live from its own estimates:
///     <see cref="Resilience.Measured" /> is the one place a dashboard looks.
///     <para>
///         Every property is a <i>reading</i>, not configuration - the configuration that produces it is
///         <see cref="Resilience.AttemptCeiling" />, <see cref="NResilience.Backoff.MeasuredBase" /> and
///         <see cref="Resilience.Hedge" /> respectively. Each returns <c>null</c> when its feature is not
///         configured, or when the estimate is still cold, and reading one validates the policy exactly
///         as executing it does.
///     </para>
/// </summary>
/// <remarks>
///     The estimates are private to the policy instance. The HTTP handler derives one policy per host,
///     so each host is measured independently and <c>ResilienceHandler.PoliciesByHost()</c> is where
///     per-host readings come from.
///     <para>
///         The struct holds the policy and computes on read, so a value kept in a local keeps reporting
///         current numbers rather than a snapshot.
///     </para>
/// </remarks>
public readonly struct MeasuredValues : IEquatable<MeasuredValues>
{
    private readonly Resilience? _policy;

    internal MeasuredValues(Resilience policy)
    {
        _policy = policy;
    }

    /// <summary>
    ///     The current measured attempt ceiling, including the floor and hedge floor, before
    ///     <see cref="Resilience.AttemptTimeout" /> and the deadline clamp it. <c>null</c> when
    ///     <see cref="Resilience.AttemptCeiling" /> is not configured, or when the estimate is still cold.
    ///     <para>
    ///         This value is what the attempt gets whenever it is below
    ///         <see cref="Resilience.AttemptTimeout" />. A value above it means the clamp is currently
    ///         bounding the attempt.
    ///     </para>
    ///     <para>
    ///         This is the primary value to monitor on a dashboard.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     Validation is a side effect of looking the estimate up, so it happens only on the path that
    ///     looks one up: with <see cref="Resilience.AttemptCeiling" /> unset this returns <c>null</c>
    ///     without validating anything.
    /// </remarks>
    /// <exception cref="ResilienceConfigurationException">
    ///     A ceiling is configured and the policy cannot be executed.
    /// </exception>
    public TimeSpan? AttemptCeiling => _policy?.ReadCeiling();

    /// <summary>
    ///     The base delay the next transient retry would wait, when
    ///     <see cref="NResilience.Backoff.MeasuredBase" /> is configured: the measured baseline, after
    ///     <see cref="MeasuredBase.Spread" /> has clamped it around
    ///     <see cref="NResilience.Backoff.TransientBase" />. <c>null</c> when no base is being measured,
    ///     or when the estimate is still cold and the configured constant is what a retry would use.
    ///     <para>
    ///         The jitter and the growth factor are applied on top of this per attempt, so it is the
    ///         first retry's delay before randomness rather than any single delay a call served.
    ///     </para>
    ///     <para>
    ///         The value to put on a dashboard beside the configured base: the gap between the two is how
    ///         wrong the constant was.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     Validation is a side effect of looking the estimate up, so it happens only on the path that
    ///     looks one up: with <see cref="NResilience.Backoff.MeasuredBase" /> unset this returns
    ///     <c>null</c> without validating anything.
    /// </remarks>
    /// <exception cref="ResilienceConfigurationException">
    ///     A base is being measured and the policy cannot be executed.
    /// </exception>
    public TimeSpan? BackoffBase => _policy?.ReadBackoffBase();

    /// <summary>
    ///     How long a call has to run before a hedge arms, after <see cref="NResilience.Hedge.MinimumDelay" />
    ///     has floored it. <c>null</c> when <see cref="Resilience.Hedge" /> is not configured, or when the
    ///     latency estimate has fewer than <see cref="NResilience.Hedge.MinimumSamples" /> samples and no
    ///     hedge can fire yet.
    ///     <para>
    ///         This is the latency at which the library starts duplicating load, so it belongs on the same
    ///         dashboard as the extra traffic it explains. It moves with the dependency: that is the whole
    ///         safety argument for a quantile over a constant.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     The gates that can still refuse a hedge - the breaker, the win rate, the concurrency ceiling,
    ///     the remaining deadline - are asked when the threshold fires, so a non-null reading is when a
    ///     hedge <i>would</i> be considered rather than a promise that one starts.
    ///     <para>
    ///         Validation is a side effect of looking the estimate up, so it happens only on the path that
    ///         looks one up: with <see cref="Resilience.Hedge" /> unset this returns <c>null</c> without
    ///         validating anything.
    ///     </para>
    /// </remarks>
    /// <exception cref="ResilienceConfigurationException">
    ///     A hedge is configured and the policy cannot be executed.
    /// </exception>
    public TimeSpan? HedgeThreshold => _policy?.ReadHedgeThreshold();

    /// <summary>Whether two readings came from the same policy.</summary>
    /// <param name="other">The other reading.</param>
    /// <returns>True when both read the same policy instance.</returns>
    public bool Equals(MeasuredValues other) => ReferenceEquals(_policy, other._policy);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MeasuredValues other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _policy is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_policy);

    /// <summary>Whether two readings came from the same policy.</summary>
    /// <param name="left">The left reading.</param>
    /// <param name="right">The right reading.</param>
    /// <returns>True when both read the same policy instance.</returns>
    public static bool operator ==(MeasuredValues left, MeasuredValues right) => left.Equals(right);

    /// <summary>Whether two readings came from different policies.</summary>
    /// <param name="left">The left reading.</param>
    /// <param name="right">The right reading.</param>
    /// <returns>True when they read different policy instances.</returns>
    public static bool operator !=(MeasuredValues left, MeasuredValues right) => !left.Equals(right);
}
