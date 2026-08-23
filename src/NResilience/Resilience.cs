namespace NResilience;

/// <summary>
/// A policy, as a value. This is the one type most users ever touch.
/// <para>
/// It is a record, so <c>with</c> is the configuration language: there is no builder, no
/// <c>Build()</c>, no mutable-to-immutable transition, and no fluent chain whose order matters.
/// Deriving a variant of a house policy is one expression, and the result is an ordinary
/// immutable value you can hold in a <c>static readonly</c> field, pass around, and print.
/// </para>
/// <para>
/// It is not generic, and there is no generic variant. The result type is a property of the
/// <i>call</i>, not of the policy: one policy covers <c>HttpResponseMessage</c>, <c>int</c>,
/// <c>Stream</c> and <c>void</c>.
/// </para>
/// </summary>
/// <example>
/// <code>
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
    /// Passthrough. Every bound is off, so the executor returns the callback's own task and the
    /// call allocates nothing at all - the only genuinely free configuration in the library.
    /// </summary>
    public static Resilience None { get; } = new()
    {
        Attempts = 1,
        Deadline = Timeout.InfiniteTimeSpan,
        AttemptTimeout = Timeout.InfiniteTimeSpan,
        Backoff = Backoff.None,

        // Redundant against Attempts = 1, and stated anyway: passthrough means *every* bound is
        // off, and a reader should not have to derive "so no budget either" from the attempt count.
        Budget = RetryBudget.None,
    };

    /// <summary>
    /// The shipped defaults: three attempts, a 30 s deadline, a 10 s attempt ceiling, exponential
    /// backoff with full jitter, and <see cref="Classifier.Default"/> - which does not retry
    /// exceptions it does not recognize.
    /// </summary>
    public static Resilience Default { get; } = new();

    /// <summary>
    /// <see cref="Default"/> with <see cref="Classifier.Http"/>, which knows that a 429 is
    /// throttling, a 5xx or 408 is transient, and a 404 is an answer rather than a failure.
    /// </summary>
    /// <remarks>
    /// Held behind a nested holder so an application that never touches HTTP does not root
    /// <c>System.Net.Http</c> just by reading <see cref="Default"/>.
    /// </remarks>
    public static Resilience Http => HttpHolder.Instance;

    /// <summary>
    /// TOTAL attempts including the first. 3 means try, retry, retry.
    /// </summary>
    public int Attempts { get; init; } = 3;

    /// <summary>
    /// Wall-clock budget for the whole operation, retries and backoff included.
    /// <see cref="Timeout.InfiniteTimeSpan"/> means unbounded.
    /// </summary>
    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Ceiling for one attempt. The effective value is <c>min(this, time left on the Deadline)</c>,
    /// so the "is that per attempt or total?" question has no answer to get wrong.
    /// <see cref="Timeout.InfiniteTimeSpan"/> means the deadline is the only bound.
    /// </summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The delay between one attempt and the next.</summary>
    public Backoff Backoff { get; init; } = Backoff.Default;

    /// <summary>What counts as what. Said once, and read by everything.</summary>
    public Classifier Classify { get; init; } = Classifier.Default;

    /// <summary>
    /// Null means no circuit breaking. Breakers are shared only where you share the object: this is
    /// a live, mutable object rather than configuration, and <c>with</c> copies the reference.
    /// </summary>
    public Breaker? Breaker { get; init; }

    /// <summary>
    /// Null means an automatic retry budget private to this policy instance.
    /// <see cref="RetryBudget.None"/> disables it; <see cref="RetryBudget.Shared(string, double, int)"/>
    /// or one shared instance opts into sharing.
    /// <para>
    /// Deliberately <b>not</b> a process-wide singleton by default. A single global budget would let
    /// a storm against payments throttle retries to search, which is the blast-radius inversion a
    /// resilience library exists to prevent.
    /// </para>
    /// </summary>
    /// <remarks>
    /// The automatic budget cannot live in a field on this record: the synthesized equality compares
    /// every instance field, so a lazily-created budget would make two identically-configured
    /// policies stop being equal as a side effect of one of them having executed. It lives in the
    /// same <c>ConditionalWeakTable</c> as the validated flag, keyed by reference identity.
    /// </remarks>
    public RetryBudget? Budget { get; init; }

    /// <summary>
    /// Runs before every attempt, including the first. The place to build a fresh request or
    /// refresh a token - retry re-invokes the callback, so anything single-use has to be rebuilt.
    /// <para>
    /// Returns <see cref="Task"/> rather than <see cref="ValueTask"/> for the same reason the
    /// execution callbacks do, plus one of its own: the executor already awaits a
    /// <see cref="Task"/> for the attempt and the backoff delay, so a <see cref="Task"/>-returning
    /// hook shares their hoisted awaiter field instead of adding one to every suspending call.
    /// Measured: 16 B/call cheaper for every caller, whether or not the hook is set.
    /// </para>
    /// </summary>
    public Func<NextAttempt, Task>? BeforeAttempt { get; init; }

    /// <summary>
    /// Told about everything that happens during a call. Null - the default - means the executor
    /// raises nothing and pays nothing, which is what "pay-for-play telemetry" has to mean if it
    /// is to mean anything.
    /// <para>
    /// Synchronous, and called on the thread the executor is running on, so a listener that blocks
    /// blocks the call. Log, count, enqueue; do not do I/O. An exception thrown by a listener is
    /// swallowed: telemetry that can fail the operation it is observing is worse than no
    /// telemetry.
    /// </para>
    /// </summary>
    public Action<CallEvent>? OnEvent { get; init; }

    /// <summary>A name for this policy, used in diagnostics.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// The clock. Leave it alone in production: the timeout-source pool is only available on
    /// <see cref="TimeProvider.System"/>, because <c>CancellationTokenSource.TryReset()</c> always
    /// returns false on a source built with a custom provider.
    /// </summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// Checks the policy and throws <see cref="ResilienceConfigurationException"/> listing every
    /// problem at once.
    /// <para>
    /// This is not called for you at construction time, and that is the one real cost of the
    /// value-based design: a builder gets a natural validation hook at <c>Build()</c>, and a
    /// record with <c>init</c> properties does not, because <c>with</c> runs the copy constructor
    /// before the init setters. Validation therefore happens eagerly when you call this, and
    /// lazily on the first execution of each policy instance.
    /// </para>
    /// </summary>
    /// <exception cref="ResilienceConfigurationException">The policy cannot be executed.</exception>
    public void Validate()
    {
        var problems = new List<string>();

        if (Attempts < 1)
        {
            problems.Add($"Attempts must be at least 1; it is {Attempts}.");
        }

        CheckDuration(Deadline, nameof(Deadline), problems);
        CheckDuration(AttemptTimeout, nameof(AttemptTimeout), problems);

        if (Classify is null)
        {
            problems.Add("Classify must not be null.");
        }

        if (Time is null)
        {
            problems.Add("Time must not be null.");
        }

        Backoff.Validate(problems);

        if (problems.Count > 0)
        {
            throw new ResilienceConfigurationException(problems);
        }
    }

    /// <summary>
    /// True when the policy imposes nothing at all, so a call can hand back the callback's own
    /// task without an executor frame.
    /// </summary>
    /// <remarks>
    /// Computed rather than cached in a field: a record's synthesized equality compares every
    /// instance field, so a cached flag would make two identically-configured policies compare
    /// unequal.
    /// </remarks>
    private bool IsPassthrough =>
        Attempts <= 1
        && Deadline == Timeout.InfiniteTimeSpan
        && AttemptTimeout == Timeout.InfiniteTimeSpan
        && BeforeAttempt is null

        // A listener takes a policy out of passthrough even though it imposes no bound. Handing
        // back the callback's own task would be cheaper and would silently raise nothing, and a
        // listener that never fires is a worse surprise than a policy that stopped being free the
        // moment it was explicitly instrumented.
        && OnEvent is null
        && Breaker is null

        // A budget on a policy that cannot retry still needs its deposits, because a *shared* budget
        // is funded by the successful traffic of every policy holding it - including single-attempt
        // ones. Only the absence of a budget, or RetryBudget.None, is free.
        && Budget is null or { IsNone: true };

    /// <summary>
    /// <see cref="Timeout.InfiniteTimeSpan"/> is the explicit "no bound" value rather than a
    /// non-positive duration to reject; every other non-positive duration is a mistake.
    /// </summary>
    private static void CheckDuration(TimeSpan value, string name, List<string> problems)
    {
        if (value == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        if (value <= TimeSpan.Zero)
        {
            problems.Add($"{name} must be positive, or Timeout.InfiniteTimeSpan for no bound; it is {value}.");
        }
    }

    private static class HttpHolder
    {
        internal static readonly Resilience Instance = Default with { Classify = Classifier.Http, Name = "http" };
    }
}
