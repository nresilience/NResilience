using NResilience.Internal;

namespace NResilience;

/// <summary>
///     A policy, as a value. This is the one type most users ever touch.
///     <para>
///         It is a record, so <c>with</c> is the configuration language: there is no builder, no
///         <c>Build()</c>, no mutable-to-immutable transition, and no fluent chain whose order matters.
///         Deriving a variant of a house policy is one expression, and the result is an ordinary
///         immutable value you can hold in a <c>static readonly</c> field, pass around, and print.
///     </para>
///     <para>
///         It is not generic, and there is no generic variant. The result type is a property of the
///         <i>call</i>, not of the policy: one policy covers <c>HttpResponseMessage</c>, <c>int</c>,
///         <c>Stream</c> and <c>void</c>.
///     </para>
/// </summary>
/// <example>
///     <code>
/// public static class Policies
/// {
///     public static readonly Resilience Api      = Resilience.Http with { Deadline = TimeSpan.FromSeconds(10) };
///     public static readonly Resilience Realtime = Api with { Attempts = 1, AttemptTimeout = TimeSpan.FromMilliseconds(250) };
/// }
/// </code>
/// </example>
public sealed partial record Resilience
{
    /// <summary>
    ///     The multiple the default measured attempt ceiling uses: three times the dependency's own
    ///     recent p95.
    /// </summary>
    private const double DefaultTimeoutMultiple = 3.0;

    private readonly AttemptCeiling? _ceiling;
    private readonly bool _ceilingSet;

    /// <summary>
    ///     Passthrough. Every bound is off, so the executor returns the callback's own task and the
    ///     call allocates nothing at all - the only genuinely free configuration in the library.
    /// </summary>
    public static Resilience None { get; } = new()
    {
        Attempts = 1,
        Deadline = Timeout.InfiniteTimeSpan,
        AttemptTimeout = Timeout.InfiniteTimeSpan,
        Backoff = Backoff.None,

        // Passthrough means every bound is off, and a measured ceiling is a bound - the one bound
        // that is on by default everywhere else. Said as Adaptive rather than as
        // `AttemptCeiling = null`, because that is the one word for "measure nothing", and a preset
        // for "no bounds at all" should be spelled the same way a caller would spell it.
        Adaptive = false,

        // Redundant against Attempts = 1, and stated anyway: passthrough means *every* bound is
        // off, and a reader should not have to derive "so no budget either" from the attempt count.
        Budget = RetryBudget.None,
    };

    /// <summary>
    ///     The shipped defaults: three attempts, a 30 s deadline, a 10 s attempt ceiling, exponential
    ///     backoff with full jitter, and <see cref="Classifier.Default" /> - which does not retry
    ///     exceptions it does not recognize.
    /// </summary>
    public static Resilience Default { get; } = new();

    /// <summary>
    ///     <see cref="Default" /> with <see cref="Classifier.Http" />, which knows that a 429 is
    ///     throttling, a 5xx or 408 is transient, and a 404 is an answer rather than a failure.
    /// </summary>
    /// <remarks>
    ///     Held behind a nested holder so an application that never touches HTTP does not root
    ///     <c>System.Net.Http</c> just by reading <see cref="Default" />.
    /// </remarks>
    public static Resilience Http => HttpHolder.Instance;

    /// <summary>
    ///     How many attempts to make. <c>1</c> means no retry; <c>3</c> means try, then retry twice.
    ///     <para>
    ///         A count of calls, not a count of retries. Most libraries configure the retries and leave
    ///         you to add one; this is the total, so there is no off-by-one to get wrong.
    ///     </para>
    /// </summary>
    public int Attempts { get; init; } = 3;

    /// <summary>
    ///     Wall-clock budget for the whole operation, retries and backoff included.
    ///     <see cref="Timeout.InfiniteTimeSpan" /> means unbounded.
    /// </summary>
    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Ceiling for one attempt. The effective value is <c>min(this, time left on the Deadline)</c>,
    ///     so the "is that per attempt or total?" question has no answer to get wrong.
    ///     <see cref="Timeout.InfiniteTimeSpan" /> means the deadline is the only bound.
    /// </summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     The per-attempt ceiling measured from the dependency's own recent latency, on by default at
    ///     <c>AttemptCeiling.Above(3)</c>: the effective ceiling is the minimum of
    ///     <see cref="AttemptTimeout" />, the time remaining on the deadline, and a multiple of recent
    ///     latency. Set it to <c>null</c> to leave <see cref="AttemptTimeout" /> as the only per-attempt
    ///     ceiling. A policy whose <see cref="AttemptTimeout" /> is <see cref="Timeout.InfiniteTimeSpan" />
    ///     gets no default: see the remarks.
    ///     <para>
    ///         The measured term only lowers the ceiling, making the feature safe to leave on: <see cref="AttemptTimeout" />
    ///         remains the absolute ceiling, and a dependency slow enough that the measurement exceeds it
    ///         gets the default behavior. See <see cref="NResilience.AttemptCeiling" /> for details.
    ///     </para>
    ///     <para>
    ///         The estimate is fed by successful attempts only and requires
    ///         <see cref="NResilience.AttemptCeiling.MinimumSamples" /> before it bounds any attempt; until then,
    ///         the attempt uses <see cref="AttemptTimeout" /> unchanged. When <see cref="Hedge" /> is also configured,
    ///         the hedge threshold acts as a floor for the ceiling to prevent the first leg from being cancelled
    ///         before the second leg starts.
    ///     </para>
    ///     <para>
    ///         Configuring this does not increase the allocation of the caller's state-machine box.
    ///         See <c>ExecutionState.AttemptCeilingFor</c> for details.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     Defaulted on because the measured term can only tighten: <see cref="AttemptTimeout" /> stays
    ///     the ceiling, the estimate is cold until it has
    ///     <see cref="NResilience.AttemptCeiling.MinimumSamples" /> successful attempts, and the worst
    ///     the feature can do is stop shortening. A policy whose <see cref="AttemptTimeout" /> is at or
    ///     below <see cref="NResilience.AttemptCeiling.Floor" /> gets no default at all, because the
    ///     measured term could never lower anything there - rather than the configuration error the same
    ///     pair would be if the caller had written it.
    /// </remarks>
    public AttemptCeiling? AttemptCeiling
    {
        get => _ceilingSet ? _ceiling : DefaultAttemptCeiling();
        init
        {
            _ceiling = value;
            _ceilingSet = true;
        }
    }

    /// <summary>The delay between one attempt and the next.</summary>
    public Backoff Backoff { get; init; } = Backoff.Default;

    /// <summary>What counts as what. Said once, and read by everything.</summary>
    public Classifier Classifier { get; init; } = Classifier.Default;

    /// <summary>
    ///     Null means no circuit breaking. Breakers are shared only where you share the object: this is
    ///     a live, mutable object rather than configuration, and <c>with</c> copies the reference.
    /// </summary>
    public Breaker? Breaker { get; init; }

    /// <summary>
    ///     The retry budget, which bounds retries as a fraction of traffic. Three values, and the
    ///     default is the middle one:
    ///     <list type="bullet">
    ///         <item>
    ///             <see cref="RetryBudget.Automatic" /> - the default - is a budget private to this
    ///             policy instance, or to each key when the policy is scoped.
    ///         </item>
    ///         <item><see cref="RetryBudget.None" /> is no budget at all.</item>
    ///         <item>
    ///             Any other instance is that budget, shared wherever the instance is shared. Use
    ///             <see cref="RetryBudget.Shared(string, double, int)" /> to share one by name.
    ///         </item>
    ///     </list>
    ///     <para>
    ///         Retry budgets are not process-wide singletons by default. This prevents a failure
    ///         storm against one dependency from throttling retries for another.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     The budget resolved from <see cref="RetryBudget.Automatic" /> is stored in a
    ///     <c>ConditionalWeakTable</c> keyed by reference identity. This ensures that lazily
    ///     created budgets do not affect record equality.
    /// </remarks>
    public RetryBudget Budget { get; init; } = RetryBudget.Automatic;

    /// <summary>
    ///     Runs before every attempt, including the first. The place to build a fresh request or
    ///     refresh a token - retry re-invokes the callback, so anything single-use has to be rebuilt.
    ///     <para>
    ///         Returns <see cref="Task" /> rather than <see cref="ValueTask" /> for the same reason the
    ///         execution callbacks do, plus one of its own: the executor already awaits a
    ///         <see cref="Task" /> for the attempt and the backoff delay, so a <see cref="Task" />-returning
    ///         hook shares their hoisted awaiter field instead of adding one to every suspending call.
    ///         Measured: 16 B/call cheaper for every caller, whether or not the hook is set.
    ///     </para>
    ///     <para>
    ///         This runs outside the executor's classification region. An exception it throws is not
    ///         retried, not logged to the attempt log, and raises no <see cref="CallEvent" /> - it
    ///         propagates out of the call unchanged. Use this for setup that always has to run, not for
    ///         a guard that should be able to refuse an attempt: a check that needs retry, backoff, or
    ///         telemetry belongs inside the callback instead, classified like any other outcome. See
    ///         "Building a custom guard" in the admission control deep dive.
    ///     </para>
    ///     <para>
    ///         One slot, and setting it replaces whatever was there - there is no combinator, unlike
    ///         <see cref="WithListener" /> for <see cref="OnEvent" />. Two pieces of setup are one hook
    ///         that does both, in the order you write them.
    ///     </para>
    /// </summary>
    public Func<NextAttempt, Task>? BeforeAttempt { get; init; }

    /// <summary>
    ///     First-class local admission control, checked once per attempt, inside the same classified
    ///     region the attempt itself runs in. Return <see cref="Verdict.Ok" /> to admit the attempt;
    ///     return anything else - typically <see cref="Verdict.Refused" /> or
    ///     <see cref="Verdict.Limited" /> - to refuse it, and the attempt is skipped and treated exactly
    ///     as if that verdict had come back from the callback: the same log entry, the same telemetry,
    ///     the same retry-budget exemption for <see cref="Verdict.SelfImposed" />, and the same breaker
    ///     treatment.
    ///     <para>
    ///         Unlike <see cref="BeforeAttempt" />, this is bounded by the attempt's own token and runs
    ///         where a thrown exception is classified - an exception this hook throws is classified like
    ///         any other. Prefer this when a guard's outcome should participate in retry, backoff, the
    ///         breaker and the attempt log; use <see cref="BeforeAttempt" /> for setup that always has to
    ///         run and has no outcome to classify. The classified-exception recipe in the admission
    ///         control deep dive still works without configuring this at all - this hook exists for
    ///         callers who would rather express the guard as a value than as a thrown exception.
    ///     </para>
    ///     <para>
    ///         Configuring this selects a second, separate execution path with one extra hoisted awaiter
    ///         field, because an <c>await</c> written once in the executor's source costs every caller
    ///         that field whether or not the hook is set - see "One bit, zero bytes" in the admission
    ///         control deep dive. Callers who never set this pay nothing for it: the shipping baseline is
    ///         unchanged, gated in <c>NResilience.Gates</c>.
    ///     </para>
    ///     <para>
    ///         A guard that answers from memory should hand back <see cref="Verdict.OkTask" /> rather than
    ///         <c>Task.FromResult(Verdict.Ok)</c>, which allocates once per admitted attempt.
    ///     </para>
    ///     <para>
    ///         One slot, and setting it replaces whatever was there. Deliberately: combining two guards
    ///         needs a rule for which refusal wins, and that is a decision about your system rather than
    ///         one the library should make for you. Two guards are one hook that checks both.
    ///     </para>
    /// </summary>
    public Func<NextAttempt, Task<Verdict>>? Admit { get; init; }

    /// <summary>
    ///     Null - the default, and the default in every preset - means no hedging. Set it, and an
    ///     attempt that is taking longer than <see cref="NResilience.Hedge.Quantile" /> of recent calls
    ///     to this dependency gets a second copy started alongside it, and the first answer wins.
    ///     <para>
    ///         The threshold is always a live quantile rather than a constant, which is what makes the
    ///         feature safe to leave on: see <see cref="NResilience.Hedge" /> for the argument.
    ///         <see cref="Attempts" /> still bounds the total number of calls that reach the dependency,
    ///         and <see cref="NResilience.Hedge.MaximumConcurrent" /> bounds how many of them overlap.
    ///     </para>
    ///     <para>
    ///         A hedge is charged to the <see cref="Budget" /> exactly like a retry, fires only while the
    ///         <see cref="Breaker" /> is closed, and never fires before the estimate has
    ///         <see cref="NResilience.Hedge.MinimumSamples" /> samples. For HTTP, a request the handler
    ///         would not retry is never hedged either - the idempotency gate is the same one.
    ///     </para>
    ///     <para>
    ///         Configuring this selects a third execution path, which allocates: a list of legs, a task
    ///         per leg, and the racing machinery <see cref="Task.WhenAny(Task[])" /> needs. That cost is
    ///         quarantined to callers who ask for it - the non-hedged budgets are unchanged, and gated.
    ///         Hedging also takes ownership of disposing the results it discards; see
    ///         <see cref="NResilience.Hedge" />.
    ///     </para>
    /// </summary>
    public Hedge? Hedge { get; init; }

    /// <summary>
    ///     Whether this policy's deadline is clamped by the inherited deadline of the current call.
    ///     Off by default.
    ///     <para>
    ///         When set, the effective deadline is
    ///         <c>min(<see cref="Deadline" />, <see cref="ResilienceDeadline.Remaining" />)</c>, resolved
    ///         once at the start of the call. <see cref="AttemptTimeout" /> remains <c>min(configured, time left)</c>,
    ///         so a shorter deadline reduces attempt durations. Calls with an already expired inherited
    ///         deadline stop immediately with <see cref="DeadlineExceededException" />.
    ///     </para>
    ///     <para>
    ///         Reading an <see cref="AsyncLocal{T}" /> has a cost, and most calls lack an inbound deadline.
    ///         When false, the cost is one branch per call; when true, it is one read. Use
    ///         <see cref="ResilienceDeadline.Begin" /> to publish an inbound deadline, or
    ///         <c>UseResilienceDeadline()</c> from <c>NResilience.AspNetCore</c> in ASP.NET Core apps.
    ///     </para>
    /// </summary>
    public bool UseAmbientDeadline { get; init; }

    /// <summary>
    ///     Told about everything that happens during a call. Null - the default - means the executor
    ///     raises nothing and pays nothing, which is what "pay-for-play telemetry" has to mean if it
    ///     is to mean anything.
    ///     <para>
    ///         Synchronous, and called on the thread the executor is running on, so a listener that blocks
    ///         blocks the call. Log, count, enqueue; do not do I/O. An exception thrown by a listener is
    ///         swallowed: telemetry that can fail the operation it is observing is worse than no
    ///         telemetry.
    ///     </para>
    ///     <para>
    ///         Setting this in a <c>with</c> expression <i>replaces</i> what is here, which silently drops
    ///         the telemetry and logging a container registration attached. Use
    ///         <see cref="WithListener" /> to add one without taking one away.
    ///     </para>
    /// </summary>
    public Action<CallEvent>? OnEvent { get; init; }

    /// <summary>
    ///     Whether this policy is allowed to measure the dependency and bound itself by what it
    ///     measures. True by default.
    ///     <para>
    ///         Setting it to <c>false</c> is the one-line answer to "make this deterministic": it turns
    ///         off every measured term the library would otherwise supply, leaving only the constants
    ///         written here. Today that is <see cref="AttemptCeiling" />; anything added later that
    ///         measures rather than asks joins it, and <see cref="NResilience.Backoff.MeasuredBase" />
    ///         already has.
    ///     </para>
    ///     <para>
    ///         It suppresses defaults rather than overriding what you wrote. A policy that says
    ///         <c>false</c> and then configures <see cref="AttemptCeiling" />, <see cref="Hedge" /> or
    ///         <see cref="NResilience.Backoff.MeasuredBase" /> has contradicted itself, and <see cref="Validate" /> says so rather than picking a
    ///         winner.
    ///     </para>
    ///     <para>
    ///         It does not reach the <see cref="Breaker" />, which has its own
    ///         <see cref="BreakerSettings.Adaptive" />. A breaker is a live object that two policies may
    ///         share, so a switch on one policy cannot silently re-configure a guard the other is also
    ///         holding. In configuration there is no such sharing and one <c>"Adaptive": false</c>
    ///         covers both.
    ///     </para>
    /// </summary>
    public bool Adaptive { get; init; } = true;

    /// <summary>A name for this policy, used in diagnostics.</summary>
    public string? Name { get; init; }

    /// <summary>
    ///     The clock. Leave it alone in production: the timeout-source pool is only available on
    ///     <see cref="TimeProvider.System" />, because <c>CancellationTokenSource.TryReset()</c> always
    ///     returns false on a source built with a custom provider.
    /// </summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    ///     What this policy is currently measuring: the attempt ceiling, the backoff base and the hedge
    ///     threshold, each <c>null</c> until its feature is configured and its estimate is warm.
    ///     <para>
    ///         Readings, not configuration. The configuration that produces them is
    ///         <see cref="AttemptCeiling" />, <see cref="NResilience.Backoff.MeasuredBase" /> and
    ///         <see cref="Hedge" />. Reading one validates the policy, exactly as executing it does.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     The estimates are private to the policy instance. The HTTP handler derives one policy per
    ///     host, so each host is measured independently.
    /// </remarks>
    public MeasuredValues Measured => new(this);

    /// <summary>
    ///     True when the policy imposes nothing at all, so a call can hand back the callback's own
    ///     task without an executor frame.
    /// </summary>
    /// <remarks>
    ///     Computed rather than cached in a field: a record's synthesized equality compares every
    ///     instance field, so a cached flag would make two identically-configured policies compare
    ///     unequal.
    /// </remarks>
    private bool IsPassthrough =>
        Attempts <= 1
        && Deadline == Timeout.InfiniteTimeSpan
        && AttemptTimeout == Timeout.InfiniteTimeSpan
        && BeforeAttempt is null
        && Admit is null
        && Hedge is null

        // A measured ceiling is a bound, and one this policy has asked for even though the two
        // constants above say "no bound". A policy that set AttemptTimeout to infinite and AttemptCeiling
        // to something has asked to be bounded by the dependency's own latency, which passthrough
        // cannot deliver.
        && AttemptCeiling is null

        // An inbound deadline is a bound like any other, and a policy asking to be clamped by one has
        // asked for a bound it cannot see from here - so passthrough is off the table whether or not
        // the current call actually inherited one.
        && !UseAmbientDeadline

        // A listener takes a policy out of passthrough even though it imposes no bound. Handing
        // back the callback's own task would be cheaper and would silently raise nothing, and a
        // listener that never fires is a worse surprise than a policy that stopped being free the
        // moment it was explicitly instrumented.
        && OnEvent is null
        && Breaker is null

        // Shared budgets are funded by all policies, including those with a single attempt. Only
        // the two markers - <see cref="RetryBudget.None" /> and <see cref="RetryBudget.Automatic" /> -
        // are free of cost for single-attempt policies.
        //
        // The automatic marker is free because budgets are only materialized when <c>Attempts > 1</c>.
        && Budget is { IsNone: true } or { IsAutomatic: true };

    /// <summary>
    ///     Checks the policy and throws <see cref="ResilienceConfigurationException" /> listing every
    ///     problem at once.
    ///     <para>
    ///         This is not called for you at construction time, and that is the one real cost of the
    ///         value-based design: a builder gets a natural validation hook at <c>Build()</c>, and a
    ///         record with <c>init</c> properties does not, because <c>with</c> runs the copy constructor
    ///         before the init setters. Validation therefore happens eagerly when you call this, and
    ///         lazily on the first execution of each policy instance.
    ///     </para>
    /// </summary>
    /// <exception cref="ResilienceConfigurationException">The policy cannot be executed.</exception>
    public void Validate()
    {
        var problems = new List<string>();

        if (Attempts < 1)
            problems.Add($"Attempts must be at least 1; it is {Attempts}.");

        CheckDuration(Deadline, nameof(Deadline), problems);
        CheckDuration(AttemptTimeout, nameof(AttemptTimeout), problems);

        if (Classifier is null)
            problems.Add("Classifier must not be null.");

        if (Time is null)
            problems.Add("Time must not be null.");

        Backoff.Validate(problems);

        if (AttemptCeiling is { } ceiling)
        {
            ceiling.Validate(problems);

            // A floor at or above the configured ceiling makes the measured term unreachable: the
            // clamp would hand back AttemptTimeout on every attempt, whatever the dependency did.
            // Rejected rather than ignored, for the reason the Hedge check below is: silently doing
            // nothing is how a caller ends up believing a ceiling is being measured when it is not.
            if (AttemptTimeout != Timeout.InfiniteTimeSpan && ceiling.Floor >= AttemptTimeout)
            {
                problems.Add(
                    $"AttemptCeiling.Floor must be below AttemptTimeout; they are {ceiling.Floor} and {AttemptTimeout}. " +
                    "The measured ceiling is clamped by AttemptTimeout, so a floor at or above it can never lower anything.");
            }
        }

        if (!Adaptive)
        {
            // Suppressing the defaults is what Adaptive does; refusing what the caller wrote is not.
            // A policy that turns measurement off and then configures a measured term has said two
            // incompatible things, and the library reports both rather than ranking them.
            if (AttemptCeiling is not null)
            {
                problems.Add(
                    "Adaptive is false, so this policy measures nothing, but AttemptCeiling is set. " +
                    "Remove one: drop AttemptCeiling to keep the policy deterministic, or drop Adaptive = false to keep the measured ceiling.");
            }

            if (Backoff.MeasuredBase is not null)
            {
                problems.Add(
                    "Adaptive is false, so this policy measures nothing, but Backoff.MeasuredBase is set. " +
                    "Remove one: use Backoff.Exponential(...) to keep the policy deterministic, or drop " +
                    "Adaptive = false to keep the measured backoff base.");
            }

            if (Hedge is not null)
            {
                problems.Add(
                    "Adaptive is false, so this policy measures nothing, but Hedge is set. " +
                    "Hedging has no constant form - its threshold is always a measured quantile - so remove one of the two.");
            }
        }

        if (Hedge is { } hedge)
        {
            hedge.Validate(problems);

            // A hedge is a second attempt that starts early, so a policy with one attempt has nothing
            // to hedge with. Rejected rather than ignored: silently doing nothing is how a caller ends
            // up believing a dependency's tail is being managed when it is not.
            if (Attempts <= 1)
                problems.Add($"Hedge needs more than one attempt to work with; Attempts is {Attempts}.");
        }

        if (problems.Count > 0)
            throw new ResilienceConfigurationException(problems);
    }

    /// <summary>
    ///     This policy with one more listener on <see cref="OnEvent" />, <i>added</i> to whatever is
    ///     already there rather than replacing it.
    ///     <para>
    ///         <c>with { OnEvent = mine }</c> replaces, which silently drops the telemetry or logging a
    ///         registration attached - <c>AddResilience()</c> attaches both. This is the way to add one
    ///         without taking one away, and it is what <c>WithTelemetry()</c> and <c>WithLogging()</c>
    ///         do to each other.
    ///     </para>
    ///     <para>
    ///         Listeners run in the order they were added, on the executor's own thread, and one that
    ///         throws is swallowed - see <see cref="OnEvent" /> for what a listener may do. Adding a
    ///         listener takes the policy out of passthrough for the reason given there.
    ///     </para>
    /// </summary>
    /// <param name="listener">The listener to add.</param>
    /// <returns>A new policy. The receiver is unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="listener" /> is null.</exception>
    /// <example>
    ///     <code>
    /// var counted = Policies.Api.WithListener(e => Metrics.Record(e.Kind));
    /// </code>
    /// </example>
    public Resilience WithListener(Action<CallEvent> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        return this with { OnEvent = OnEvent + listener };
    }

    /// <summary>
    ///     Runs <see cref="Validate" /> and returns this policy, so a bad configuration throws where it
    ///     is written rather than on the first call. The shape for a <c>static readonly</c> field, where
    ///     a lazily-thrown configuration error surfaces as a <see cref="TypeInitializationException" />
    ///     much later.
    ///     <para>
    ///         A <c>with</c> expression needs parentheses before the call:
    ///         <c>(Resilience.Http with { Deadline = ... }).Validated()</c>.
    ///     </para>
    /// </summary>
    /// <returns>This policy.</returns>
    /// <exception cref="ResilienceConfigurationException">The policy cannot be executed.</exception>
    public Resilience Validated()
    {
        Validate();
        return this;
    }

    /// <summary>
    ///     <see cref="Timeout.InfiniteTimeSpan" /> is the explicit "no bound" value rather than a
    ///     non-positive duration to reject; every other non-positive duration is a mistake.
    /// </summary>
    private static void CheckDuration(TimeSpan value, string name, List<string> problems)
    {
        if (value == Timeout.InfiniteTimeSpan)
            return;

        if (value <= TimeSpan.Zero)
            problems.Add($"{name} must be positive, or Timeout.InfiniteTimeSpan for no bound; it is {value}.");
    }

    /// <summary>The measured ceiling a caller who never mentioned one gets.</summary>
    /// <returns>The configuration, or null when the library declines to default one on.</returns>
    /// <remarks>
    ///     <para>
    ///         Two policies get no default, and both for the same reason: the measured term is only safe
    ///         to default on because it can do nothing but tighten a ceiling the caller chose, so where
    ///         there is no such ceiling to tighten there is nothing to default.
    ///     </para>
    ///     <para>
    ///         An <see cref="Timeout.InfiniteTimeSpan" /> <see cref="AttemptTimeout" /> is the caller
    ///         saying the deadline is the only per-attempt bound, and a measured ceiling under it would
    ///         be a bound nothing they wrote clamps. Writing <see cref="AttemptCeiling" /> alongside an
    ///         infinite <see cref="AttemptTimeout" /> is a different statement - "bound me by the
    ///         dependency's own latency and nothing else" - and that one is honoured.
    ///     </para>
    ///     <para>
    ///         A floor at or above <see cref="AttemptTimeout" /> makes the measured term unreachable,
    ///         which is a configuration error when the caller wrote it. A caller who only tightened
    ///         <see cref="AttemptTimeout" /> wrote no such thing, and the library does not refuse a
    ///         configuration on account of a value it supplied itself - so there the default steps aside
    ///         instead.
    ///     </para>
    /// </remarks>
    private AttemptCeiling? DefaultAttemptCeiling()
    {
        if (!Adaptive || AttemptTimeout == Timeout.InfiniteTimeSpan)
            return null;

        // Qualified because the property of the same name shadows the type inside this class.
        var ceiling = NResilience.AttemptCeiling.Above();

        return ceiling.Floor >= AttemptTimeout ? null : ceiling;
    }

    private static class HttpHolder
    {
        internal static readonly Resilience Instance = Default with { Classifier = Classifier.Http, Name = "http" };
    }
}
