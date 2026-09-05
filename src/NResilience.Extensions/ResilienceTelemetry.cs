using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace NResilience.Extensions;

/// <summary>
///     The metrics and traces. One <see cref="System.Diagnostics.Metrics.Meter" />, one
///     <see cref="System.Diagnostics.ActivitySource" />, and a listener that turns
///     <see cref="CallEvent" />s into both.
///     <para>
///         The instrument set is chosen around one number. <c>nresilience.attempts ÷ nresilience.calls</c>
///         is the <b>retry fraction</b> - the characteristic metric of a retry feedback loop, and the one
///         that tells you whether you are approaching a storm rather than merely serving errors. You cannot
///         compute it unless the library distinguishes logical operations from wire-level attempts, which
///         is Finagle's distinction and is why the counters are split.
///     </para>
/// </summary>
/// <example>
///     <code>
/// builder.Services.AddOpenTelemetry()
///     .WithMetrics(m => m.AddMeter(ResilienceTelemetry.MeterName))
///     .WithTracing(t => t.AddSource(ResilienceTelemetry.ActivitySourceName));
/// </code>
/// </example>
/// <remarks>
///     Metric names, tag names and event names deliberately share nothing with Polly's
///     <c>resilience.polly.*</c> vocabulary, so a process running both is legible.
/// </remarks>
public static class ResilienceTelemetry
{
    /// <summary>The meter's name, for <c>AddMeter</c>.</summary>
    public const string MeterName = "NResilience";

    /// <summary>The activity source's name, for <c>AddSource</c>.</summary>
    public const string ActivitySourceName = "NResilience";

    /// <summary>The meter every instrument is created on.</summary>
    /// <remarks>Declared above the instruments: static initializers run in source order.</remarks>
    public static Meter Meter { get; } = new(MeterName);

    /// <summary>
    /// The activity source. Used by the HTTP registration to give a logical operation - the whole
    /// retry sequence - a span of its own, which is the boundary a per-attempt HTTP span cannot
    /// show you.
    /// </summary>
    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName);

    private static readonly Counter<long> CallCounter = Meter.CreateCounter<long>(
        "nresilience.calls",
        "{call}",
        "Logical operations - one per call, whatever happened inside it.");

    private static readonly Counter<long> AttemptCounter = Meter.CreateCounter<long>(
        "nresilience.attempts",
        "{attempt}",
        "Wire-level attempts. Divided by nresilience.calls, this is the retry fraction.");

    private static readonly Counter<long> RejectionCounter = Meter.CreateCounter<long>(
        "nresilience.rejections",
        "{rejection}",
        "Calls a guard refused to make: an open breaker, or an exhausted retry budget.");

    private static readonly Histogram<double> CallDuration = Meter.CreateHistogram<double>(
        "nresilience.call.duration",
        "s",
        "End-to-end duration of a logical operation, retries and backoff included.");

    private static readonly Histogram<double> AttemptDuration = Meter.CreateHistogram<double>(
        "nresilience.attempt.duration",
        "s",
        "Duration of one attempt.");

    private static readonly Counter<long> LeaseCounter = Meter.CreateCounter<long>(
        "nresilience.limiter.leases",
        "{lease}",
        "Permits a limiter was asked for, tagged by whether it granted one.");

    private static readonly Counter<long> HedgeCounter = Meter.CreateCounter<long>(
        "nresilience.hedges",
        "{hedge}",
        "Hedged attempts, tagged started, won or discarded. Started against nresilience.calls is the extra load hedging is costing; won is what that load bought.");

    /// <summary>
    ///     The adaptive threshold, sampled at the moments it actually decided something.
    ///     <para>
    ///         A histogram rather than the observable gauge this obviously wants to be, and the reason is
    ///         worth stating: a gauge would have to reach into the live latency estimate of every hedging
    ///         policy in the process, which means a registry of policies that outlives them - and the
    ///         estimate is private to a policy instance on purpose. Recording the threshold each time a
    ///         hedge fires needs no registry and answers the same question, because the moments a hedge
    ///         fires are exactly the moments the number mattered. Watching this move during an incident is
    ///         how you tell a brownout from a tail.
    ///     </para>
    /// </summary>
    private static readonly Histogram<double> HedgeThreshold = Meter.CreateHistogram<double>(
        "nresilience.hedge.threshold",
        "s",
        "The latency quantile a hedge fired at, recorded when it fired.");

    /// <summary>
    ///     The limit an <see cref="AdaptiveLimiter" /> has discovered, recorded at the moments it moved.
    ///     <para>
    ///         A histogram rather than the observable gauge this obviously wants to be, for the reason
    ///         <see cref="HedgeThreshold" /> gives: a gauge would need a registry of live limiters that
    ///         outlives them, and a limiter belongs to whoever built it. Recording on change needs no
    ///         registry and loses nothing, because a limit that is not moving is a limit the previous
    ///         sample already reported.
    ///     </para>
    /// </summary>
    private static readonly Histogram<int> LimiterLimit = Meter.CreateHistogram<int>(
        "nresilience.limiter.limit",
        "{permit}",
        "The concurrency limit an adaptive limiter has settled on, recorded when it changes. Watching this fall is watching the dependency tell you it is queueing.");

    /// <summary>
    ///     Recorded when the per-attempt ceiling measured by <see cref="Resilience.AttemptCeiling" /> changes.
    ///     <para>
    ///         This is a histogram because the estimate is private to each policy instance. A ceiling
    ///         that does not move is not reported, and a ceiling clamped to
    ///         <see cref="Resilience.AttemptTimeout" /> also reports nothing, signaling that the
    ///         dependency has slowed beyond the point where measurement is useful.
    ///     </para>
    /// </summary>
    private static readonly Histogram<double> AttemptCeiling = Meter.CreateHistogram<double>(
        "nresilience.attempt.ceiling",
        "s",
        "The measured per-attempt ceiling, recorded when it changes. Watching this rise is watching the dependency get slower before anything has failed.");

    /// <summary>
    ///     The measured backoff base, recorded when it changes.
    ///     <para>
    ///         The companion to <c>nresilience.attempt.ceiling</c>, and read the same way: the gap
    ///         between this and the configured transient base is how wrong the constant was. A base
    ///         pinned to <see cref="MeasuredBase.Spread" />'s clamp stops moving, and that silence is
    ///         itself the signal that the band wants widening.
    ///     </para>
    /// </summary>
    private static readonly Histogram<double> MeasuredBase = Meter.CreateHistogram<double>(
        "nresilience.backoff.base",
        "s",
        "The measured backoff base, recorded when it changes. Reported only by a policy that configures Backoff.MeasuredBase.");

    private static readonly Histogram<double> LeaseWait = Meter.CreateHistogram<double>(
        "nresilience.limiter.wait.duration",
        unit: "s",
        description: "How long a caller waited on a limiter. Zero unless queueing is enabled.");

    /// <summary>
    /// The listener: records every event to the instruments above, and annotates the current
    /// <see cref="Activity"/> if there is one.
    /// <para>
    /// Stateless and allocation-free, so attaching it to a hot policy costs the 48 bytes the
    /// executor pays for having a listener at all and nothing more. Safe to attach to any number of
    /// policies - <see cref="CallEvent.PolicyName"/> is what separates them in the tag set.
    /// </para>
    /// </summary>
    public static Action<CallEvent> Listener { get; } = Record;

    /// <summary>
    ///     Returns the policy with <see cref="Listener" /> attached, chained after whatever
    ///     <see cref="Resilience.OnEvent" /> already held.
    /// </summary>
    /// <param name="policy">The policy.</param>
    /// <returns>The instrumented policy, or the same policy if it is already instrumented.</returns>
    /// <remarks>
    ///     Chained rather than assigned, because a policy that reached here through a <c>configure</c>
    ///     callback may already carry the caller's own listener, and silently dropping it to install
    ///     metrics would be the library choosing its own telemetry over the user's.
    /// </remarks>
    public static Resilience WithTelemetry(this Resilience policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var existing = policy.OnEvent;

        if (existing is null)
            return policy with { OnEvent = Listener };

        // Reference equality is enough: Listener is a singleton, and a chained combination
        // containing it reports it in GetInvocationList.
        if (existing == Listener || Array.IndexOf(existing.GetInvocationList(), Listener) >= 0)
            return policy;

        return policy with { OnEvent = existing + Listener };
    }

    /// <summary>
    ///     Starts a span covering one logical operation. Returns null when nobody is listening, which
    ///     is what makes an always-registered telemetry handler free.
    /// </summary>
    internal static Activity? StartCall(string policyName) =>
        ActivitySource.StartActivity($"resilience {policyName}");

    /// <summary>
    ///     Records one acquisition against a limiter.
    ///     <para>
    ///         Recorded here, by the adapter, rather than by <see cref="Listener" /> from a
    ///         <see cref="CallEvent" />: the limiter is the only thing that knows how long the caller waited,
    ///         and a refusal that the policy goes on to retry successfully raises no distinguishable event
    ///         at all. Nothing in the existing instruments or their tags changes, so a dashboard built on
    ///         them keeps working.
    ///     </para>
    /// </summary>
    internal static void RecordLease(string limiter, bool acquired, TimeSpan waited)
    {
        var limiterTag = new KeyValuePair<string, object?>("nresilience.limiter", limiter);
        var outcome = new KeyValuePair<string, object?>("nresilience.outcome", acquired ? "acquired" : "denied");

        LeaseCounter.Add(1, limiterTag, outcome);
        LeaseWait.Record(waited.TotalSeconds, limiterTag, outcome);
    }

    /// <summary>Records an adaptive limiter's new limit, at the moment the control loop moved it.</summary>
    internal static void RecordLimit(string limiter, int limit) =>
        LimiterLimit.Record(limit, new KeyValuePair<string, object?>("nresilience.limiter", limiter));

    private static void Record(CallEvent e)
    {
        var policy = e.PolicyName ?? "(unnamed)";

        switch (e.Kind)
        {
            case CallEventKind.Attempt:
                AttemptCounter.Add(1, new KeyValuePair<string, object?>("nresilience.policy", policy), Verdict(e));
                AttemptDuration.Record(e.Duration.TotalSeconds, new KeyValuePair<string, object?>("nresilience.policy", policy), Verdict(e));
                Annotate(e, "nresilience.attempt");
                break;

            case CallEventKind.Retrying:
                Annotate(e, "nresilience.retrying");
                break;

            case CallEventKind.RejectedByBreaker:
            case CallEventKind.RejectedByBudget:
                RejectionCounter.Add(1, new KeyValuePair<string, object?>("nresilience.policy", policy), Reason(e));
                Terminal(e, policy);
                break;

            case CallEventKind.Succeeded:
            case CallEventKind.NotRetried:
            case CallEventKind.DeadlineExceeded:
            case CallEventKind.Exhausted:
                Terminal(e, policy);
                break;

            case CallEventKind.HedgeStarted:
                HedgeCounter.Add(1, new KeyValuePair<string, object?>("nresilience.policy", policy), Hedge("started"));

                if (e.Delay is { } threshold)
                    HedgeThreshold.Record(threshold.TotalSeconds, new KeyValuePair<string, object?>("nresilience.policy", policy));

                Annotate(e, "nresilience.hedge_started");
                break;

            case CallEventKind.AttemptCeilingAdapted:
                if (e.Delay is { } ceiling)
                    AttemptCeiling.Record(ceiling.TotalSeconds, new KeyValuePair<string, object?>("nresilience.policy", policy));

                Annotate(e, "nresilience.attempt_ceiling_adapted");
                break;

            case CallEventKind.BackoffBaseAdapted:
                if (e.Delay is { } measured)
                    MeasuredBase.Record(measured.TotalSeconds, new KeyValuePair<string, object?>("nresilience.policy", policy));

                Annotate(e, "nresilience.backoff_base_adapted");
                break;

            case CallEventKind.HedgeSuppressed:
                HedgeCounter.Add(1, new KeyValuePair<string, object?>("nresilience.policy", policy), Hedge("suppressed"));
                Annotate(e, "nresilience.hedge_suppressed");
                break;

            case CallEventKind.HedgeWon:
                HedgeCounter.Add(1, new KeyValuePair<string, object?>("nresilience.policy", policy), Hedge("won"));
                Annotate(e, "nresilience.hedge_won");
                break;

            case CallEventKind.HedgeDiscarded:
                HedgeCounter.Add(1, new KeyValuePair<string, object?>("nresilience.policy", policy), Hedge("discarded"));
                Annotate(e, "nresilience.hedge_discarded");
                break;

            case CallEventKind.BreakerOpened:
            case CallEventKind.BreakerClosed:
            case CallEventKind.BreakerHalfOpened:
            case CallEventKind.OrphanedWork:
            case CallEventKind.NestedRetry:
                // Transitions and warnings are span events rather than instruments. A breaker
                // opening is a thing that happened at a moment, and the state it left behind is
                // readable off the Breaker object by whatever health endpoint wants it; a counter
                // of transitions is the shape that answers neither question well.
                Annotate(e, EventName(e.Kind));
                break;
        }
    }

    /// <summary>
    ///     The five terminal kinds close the logical operation, and the call counter and the call
    ///     duration histogram are recorded exactly once here - which is what makes
    ///     <c>attempts ÷ calls</c> a fraction rather than a coincidence.
    /// </summary>
    private static void Terminal(CallEvent e, string policy)
    {
        var policyTag = new KeyValuePair<string, object?>("nresilience.policy", policy);
        var outcome = Outcome(e);

        CallCounter.Add(1, policyTag, outcome);
        CallDuration.Record(e.Duration.TotalSeconds, policyTag, outcome);

        if (Activity.Current is { } activity && activity.IsAllDataRequested)
        {
            activity.SetTag("nresilience.policy", policy);
            activity.SetTag("nresilience.outcome", outcome.Value);
            activity.SetTag("nresilience.attempt", e.AttemptNumber);

            if (e.Kind != CallEventKind.Succeeded)
                activity.SetStatus(ActivityStatusCode.Error, e.Exception?.Message);
        }
    }

    /// <summary>
    ///     Span-event names as constants rather than <c>ToString()</c>, for the reason
    ///     <see cref="Name(VerdictKind)" /> gives.
    /// </summary>
    private static string EventName(CallEventKind kind) => kind switch
    {
        CallEventKind.BreakerOpened => "nresilience.breaker_opened",
        CallEventKind.BreakerClosed => "nresilience.breaker_closed",
        CallEventKind.BreakerHalfOpened => "nresilience.breaker_half_opened",
        CallEventKind.OrphanedWork => "nresilience.orphaned_work",
        _ => "nresilience.nested_retry",
    };

    /// <summary>The outcome tag on <c>nresilience.hedges</c>. Constants, so nothing here allocates.</summary>
    private static KeyValuePair<string, object?> Hedge(string outcome) => new("nresilience.outcome", outcome);

    private static void Annotate(CallEvent e, string name)
    {
        if (Activity.Current is { IsAllDataRequested: true } activity)
        {
            var tags = new ActivityTagsCollection
            {
                { "nresilience.attempt", e.AttemptNumber },
                { "nresilience.verdict", Name(e.Verdict.Kind) },
            };

            if (e.Delay is { } delay)
                tags["nresilience.delay"] = delay.TotalSeconds;

            if (e.Exception is { } error)
                tags["exception.type"] = error.GetType().FullName;

            activity.AddEvent(new ActivityEvent(name, tags: tags));
        }
    }

    private static KeyValuePair<string, object?> Verdict(CallEvent e) =>
        new("nresilience.verdict", Name(e.Verdict.Kind));

    /// <summary>
    ///     The rejection's cause, which is the difference between "the dependency is down" and "we are
    ///     retrying too hard" - two facts that call for opposite responses and that only
    ///     <see cref="CallEvent.Reason" /> can tell apart.
    /// </summary>
    private static KeyValuePair<string, object?> Reason(CallEvent e) =>
        new("nresilience.reason", e.Reason switch
        {
            StopReason.DependencyUnavailable => "dependency_unavailable",
            StopReason.BudgetExhausted => "budget_exhausted",
            _ => "rejected",
        });

    private static KeyValuePair<string, object?> Outcome(CallEvent e) =>
        new("nresilience.outcome", e.Reason switch
        {
            StopReason.Succeeded => "succeeded",
            StopReason.Permanent => "permanent",
            StopReason.DeadlineExceeded => "deadline_exceeded",
            StopReason.DependencyUnavailable => "dependency_unavailable",
            StopReason.BudgetExhausted => "budget_exhausted",
            _ => "attempts_exhausted",
        });

    /// <summary>
    ///     Verdict names as constants rather than <c>ToString()</c>, because a tag value is recorded
    ///     on every attempt and <c>Enum.ToString</c> allocates a string each time.
    /// </summary>
    private static string Name(VerdictKind kind) => kind switch
    {
        VerdictKind.Ok => "ok",
        VerdictKind.Transient => "transient",
        VerdictKind.Throttled => "throttled",
        _ => "permanent",
    };
}
