using System.Collections.Concurrent;

namespace NResilience;

/// <summary>
///     A client-side retry budget: a token bucket that bounds retries as a <i>fraction of traffic</i>.
///     <para>
///         A per-call attempt limit cannot prevent a retry storm, because every caller independently
///         believes it is being reasonable. Retries compose multiplicatively: if a frontend, a backend and
///         a database each permit 3 retries, one user action generates 4³ = 64 database attempts. Only a
///         budget expressed as a fraction bounds the aggregate - with every client independently holding to
///         10%, total amplification is 1.1×.
///     </para>
///     <para>
///         The retry budget is enabled by default. Presets use <see cref="Automatic" />, which resolves
///         to a budget private to each policy instance.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         <b>Budget state is per-process and unshared.</b> There is no coordination across pods, and that
///         is not a defect - it is why the mechanism works at all. The argument is statistical: every client
///         independently capping retries at 10% bounds fleet-wide amplification without any coordination
///         protocol.
///     </para>
///     <para>
///         It follows that a budget allocated per-<c>HttpClient</c>-instance, or resolved from a scoped DI
///         container, is worthless - it is thrown away before it can observe enough traffic to mean
///         anything. Share one instance, or use <see cref="Shared(string, double, int)" />.
///     </para>
/// </remarks>
public sealed class RetryBudget
{
    /// <summary>
    ///     How many seconds of the floor rate the bucket can bank. This bounds the <i>burst</i> a
    ///     recovering client may spend at once; the sustained rate is set by
    ///     <c>minimumPerSecond</c> and by the deposits that successful traffic makes, and neither is
    ///     affected by this.
    /// </summary>
    private const double BurstSeconds = 10.0;

    private static readonly ConcurrentDictionary<string, RetryBudget> SharedBudgets = new(StringComparer.Ordinal);
    private readonly double _capacity;
    private readonly double _fraction;

    private readonly object _gate = new();
    private readonly double _refillPerSecond;
    private readonly TimeProvider? _time;
    private readonly bool _isAutomatic;
    private long _refilledAt;

    private double _tokens;

    private RetryBudget(bool isAutomatic = false) => _isAutomatic = isAutomatic;

    private RetryBudget(string? name, double fraction, int minimumPerSecond, TimeProvider time)
    {
        Name = name;
        _time = time;
        _fraction = fraction;
        _refillPerSecond = minimumPerSecond;
        _capacity = Math.Max(minimumPerSecond * BurstSeconds, 1);

        // A cold process starts full. Throttling the first few retries a fresh instance makes would
        // penalize deployment rather than a storm.
        _tokens = _capacity;
        _refilledAt = time.GetTimestamp();
    }

    /// <summary>
    ///     No budget at all: every retry the policy's other bounds allow is funded. The only correct use
    ///     is a call whose dependency is known not to be shared.
    /// </summary>
    public static RetryBudget None { get; } = new();

    /// <summary>
    ///     A marker that resolves to a private budget with default settings on the policy's first
    ///     execution.
    ///     <para>
    ///         This marker prevents "blast-radius inversion" by ensuring policies derived from the
    ///         same preset do not share a single bucket.
    ///     </para>
    ///     <para>
    ///         <see cref="Utilization" /> returns 0 for the marker. The actual utilization is reported
    ///         by the resolved bucket, accessible via <c>ResilienceHandler.BudgetsByHost()</c> for
    ///         HTTP calls.
    ///     </para>
    /// </summary>
    public static RetryBudget Automatic { get; } = new(isAutomatic: true);

    /// <summary>The name a <see cref="Shared(string, double, int)" /> budget was looked up by, if any.</summary>
    public string? Name { get; }

    /// <summary>
    ///     How much of the bucket is spent, from 0 to 1. For dashboards: a budget sitting near 1 is a
    ///     client whose retries are being refused, which is a symptom worth alerting on rather than a
    ///     steady state.
    /// </summary>
    public double Utilization
    {
        get
        {
            if (_time is null)
                return 0;

            var now = _time.GetTimestamp();

            lock (_gate)
            {
                Refill(now);
                var spent = 1 - _tokens / _capacity;
                return spent < 0 ? 0 : spent > 1 ? 1 : spent;
            }
        }
    }

    /// <summary>
    ///     True for <see cref="Automatic" />, which the executor resolves to a bucket per policy instance.
    /// </summary>
    public bool IsAutomatic => _isAutomatic;

    /// <summary>
    ///     True for <see cref="None" />, which the executor skips entirely.
    ///     <para>
    ///         Public for the same reason <see cref="IsAutomatic" /> is: neither <see cref="None" /> nor
    ///         <see cref="Automatic" /> is a bucket, so anything reporting on budgets - a dashboard, a
    ///         health endpoint - has to be able to tell a marker from a real one before reading
    ///         <see cref="Utilization" /> and finding a zero that means "not applicable" rather than
    ///         "nothing spent".
    ///     </para>
    /// </summary>
    public bool IsNone => _time is null && !_isAutomatic;

    /// <summary>A budget private to whoever holds this instance.</summary>
    /// <param name="fraction">
    ///     Retries funded per successful attempt, so 0.1 means one retry per ten successes in steady
    ///     state. Google SRE's figure; Finagle and Envoy use 0.2.
    /// </param>
    /// <param name="minimumPerSecond">
    ///     An absolute floor, so a low-traffic client whose deposits are too sparse to matter can still
    ///     retry. Zero means the fraction is the only source of tokens.
    /// </param>
    /// <param name="time">The clock. Leave it alone outside tests.</param>
    /// <returns>The budget.</returns>
    /// <exception cref="ResilienceConfigurationException">The parameters cannot be used.</exception>
    public static RetryBudget Of(double fraction = 0.1, int minimumPerSecond = 3, TimeProvider? time = null) =>
        new(null, Check(fraction, minimumPerSecond), minimumPerSecond, time ?? TimeProvider.System);

    /// <summary>
    ///     A process-wide budget looked up by name. Two policies naming the same string share it, and
    ///     the first caller's parameters win.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="fraction">As <see cref="Of" />. Used only if this call creates the budget.</param>
    /// <param name="minimumPerSecond">As <see cref="Of" />. Used only if this call creates the budget.</param>
    /// <returns>The budget.</returns>
    /// <exception cref="ResilienceConfigurationException">The parameters cannot be used.</exception>
    public static RetryBudget Shared(string name, double fraction = 0.1, int minimumPerSecond = 3)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Check(fraction, minimumPerSecond);

        return SharedBudgets.GetOrAdd(
            name,
            static (key, parameters) => new RetryBudget(key, parameters.Fraction, parameters.MinimumPerSecond, TimeProvider.System),
            (Fraction: fraction, MinimumPerSecond: minimumPerSecond));
    }

    /// <summary>The bucket <see cref="Automatic" /> resolves to. Same defaults as <see cref="Of" />.</summary>
    internal static RetryBudget CreateAutomatic(TimeProvider time) => new(null, 0.1, 3, time);

    /// <summary>Charges one retry. False means the retry is refused.</summary>
    internal bool TrySpend()
    {
        // The executor resolves None to null and never calls this, so the guard is for the sake of a
        // caller that holds the instance directly rather than for the hot path.
        if (_time is null)
            return true;

        // Read outside the lock, so the critical section is arithmetic and nothing else. See Refill.
        var now = _time.GetTimestamp();

        lock (_gate)
        {
            Refill(now);

            if (_tokens < 1)
                return false;

            _tokens -= 1;
            return true;
        }
    }

    /// <summary>Credits a successful attempt, which is what funds future retries.</summary>
    internal void Deposit()
    {
        if (_time is null)
            return;

        // Read outside the lock. Deposit() runs on every successful call, so this is the one gate the
        // library takes on the success path and the one worth keeping to arithmetic. See Refill.
        var now = _time.GetTimestamp();

        lock (_gate)
        {
            Refill(now);
            _tokens = Math.Min(_capacity, _tokens + _fraction);
        }
    }

    /// <summary>
    ///     How long until the floor rate has accrued a whole token, for the
    ///     <see cref="CallRejectedException" /> a refusal carries. Null when the floor is zero, because
    ///     then only traffic can refill the bucket and there is no honest number to give.
    /// </summary>
    internal TimeSpan? RetryAfterHint()
    {
        if (_time is null || _refillPerSecond <= 0)
            return null;

        var now = _time.GetTimestamp();

        lock (_gate)
        {
            // Refilled first, like every other reader of _tokens. The executor asks for this hint
            // *after* serving the guarded rejection delay, so without the refill the answer would be
            // the shortfall as it stood 100 ms ago and the hint would overstate.
            Refill(now);

            var needed = 1 - _tokens;
            return needed <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(needed / _refillPerSecond);
        }
    }

    private static double Check(double fraction, int minimumPerSecond)
    {
        var problems = new List<string>();

        if (fraction <= 0 || fraction > 1 || double.IsNaN(fraction))
            problems.Add($"fraction must be in (0, 1]; it is {fraction}. Use RetryBudget.None to disable the budget.");

        if (minimumPerSecond < 0)
            problems.Add($"minimumPerSecond must not be negative; it is {minimumPerSecond}.");

        if (problems.Count > 0)
            throw new ResilienceConfigurationException(problems);

        return fraction;
    }

    /// <summary>Accrues the floor rate up to <paramref name="now" />.</summary>
    /// <param name="now">
    ///     A timestamp its caller read <i>before</i> taking <c>_gate</c>, which is what keeps the clock
    ///     read out of the critical section: <see cref="Deposit" /> runs on every successful call, so a
    ///     process holding one static policy funnels all of its successful traffic through this lock and
    ///     the read roughly doubles how long each one holds it.
    ///     <para>
    ///         Reading before the lock means a thread can arrive with a timestamp another thread has
    ///         already refilled past. The <c>seconds &lt;= 0</c> guard below is what makes that safe -
    ///         it was already there for a non-monotonic clock, and it covers this too: the stale caller
    ///         accrues nothing rather than rewinding <c>_refilledAt</c>. The lost accrual is the
    ///         sub-microsecond gap between the two reads, which no rate expressed per second can see.
    ///     </para>
    /// </param>
    private void Refill(long now)
    {
        var seconds = _time!.GetElapsedTime(_refilledAt, now).TotalSeconds;

        if (seconds <= 0)
            return;

        _refilledAt = now;

        if (_refillPerSecond > 0)
            _tokens = Math.Min(_capacity, _tokens + seconds * _refillPerSecond);
    }
}
