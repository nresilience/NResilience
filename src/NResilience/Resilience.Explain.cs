using System.Globalization;
using NResilience.Internal;

namespace NResilience;

/// <content>
///     What the policy will do, in the text a person reads.
///     <para>
///         One renderer, four consumers - that ratio is the argument for it existing.
///         <see cref="Explain()" /> is the one a person calls; the other three are the message
///         <see cref="ResilienceConfigurationException" /> carries, the resilience health check's
///         payload, and the <c>NRES004</c> diagnostic, which shares the format because an analyzer
///         cannot share the code.
///     </para>
/// </content>
public sealed partial record Resilience
{
    /// <summary>How wide a rendered line is allowed to get before the wrapper breaks it.</summary>
    private const int ExplainWidth = 96;

    /// <summary>How much of each line the label column takes, and therefore how far a continuation is indented.</summary>
    private const int ExplainLabel = 19;

    /// <summary>How many attempts get a row of their own before the timeline elides the middle.</summary>
    private const int ExplainRows = 6;

    /// <summary>
    ///     How many attempts the worst case is walked through at most. Beyond it the timeline reports a
    ///     lower bound rather than a total, which keeps the walk finite for an
    ///     <see cref="Attempts" /> nobody should have written.
    /// </summary>
    private const int ExplainSteps = 1000;

    private static readonly string ExplainIndent = new(' ', ExplainLabel);

    /// <summary>
    ///     What this policy will do, as text: which bound binds first, the worst-case timeline attempt
    ///     by attempt, the load a call can add, what each adaptive term currently measures, and what the
    ///     breaker and the classifier will do about a failure.
    ///     <para>
    ///         The worst-case wall clock of a call is a function of <see cref="Attempts" />,
    ///         <see cref="Deadline" />, <see cref="AttemptTimeout" />, <see cref="AttemptCeiling" />,
    ///         <see cref="Backoff" />, the breaker's state and the budget's fill, and nobody can compute
    ///         it by reading the record. This computes it.
    ///     </para>
    ///     <para>
    ///         Safe to call on a policy that cannot be executed: an invalid policy is reported as such
    ///         and the timeline is still laid out, because the timeline is usually what makes the problem
    ///         obvious. Nothing here runs the callback, contacts a dependency, or changes the policy.
    ///     </para>
    /// </summary>
    /// <returns>The explanation, as lines separated by <c>\n</c> with no trailing newline.</returns>
    /// <remarks>
    ///     Costs nothing on any call that does not ask for it: no field, no branch, and no allocation on
    ///     the executor's path. Reading the measured terms materializes the policy's execution state and
    ///     validates it, exactly as <see cref="Measured" /> does.
    ///     <para>
    ///         The same text reaches three other places: the message
    ///         <see cref="ResilienceConfigurationException" /> carries, the payload of the resilience
    ///         health check, and - in the format only, because an analyzer cannot reference the runtime -
    ///         the <c>NRES004</c> diagnostic.
    ///     </para>
    /// </remarks>
    /// <example>
    ///     <code>
    /// Console.WriteLine(Policies.Api.Explain());
    /// </code>
    /// </example>
    public string Explain()
    {
        var text = new StringWriter();
        Explain(text);
        return text.ToString();
    }

    /// <summary>
    ///     <see cref="Explain()" />, written straight to a writer. The form for a startup log or an
    ///     admin endpoint, which needs the text on a stream rather than on the large-object heap.
    /// </summary>
    /// <param name="writer">Where to write. Nothing is flushed or disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer" /> is null.</exception>
    /// <example>
    ///     <code>
    /// Policies.Api.Explain(Console.Out);
    /// </code>
    /// </example>
    public void Explain(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var readings = Read();
        var walk = Walk(readings.Ceiling);

        writer.Write(Headline());
        writer.Write('\n');

        if (readings.Problems.Count > 0)
        {
            writer.Write('\n');
            Rows(writer, "Not valid:", [.. readings.Problems.Select(problem => (problem, string.Empty, string.Empty))]);
        }

        writer.Write('\n');
        var bound = First(readings, walk);
        Paragraph(writer, "Bound first by:", bound.Name + ". " + Capitalize(bound.Detail) + ".");

        writer.Write('\n');
        WriteWalk(writer, walk);

        writer.Write('\n');
        Paragraph(writer, "Load:", Load(readings));

        writer.Write('\n');
        WriteMeasured(writer, readings);

        writer.Write('\n');
        Paragraph(writer, "Breaker:", BreakerText());

        writer.Write('\n');
        WriteClassifier(writer);
    }

    /// <summary>
    ///     The header and the worst-case timeline, for the message
    ///     <see cref="ResilienceConfigurationException" /> carries.
    /// </summary>
    /// <returns>The timeline, or null when the policy has no bound worth laying out.</returns>
    /// <remarks>
    ///     Deliberately reads no measured term. <see cref="Validate" /> is what materializes a policy's
    ///     execution state, and a reading taken from inside the throw path would ask that state for a
    ///     policy it has just refused to build.
    /// </remarks>
    internal string? Timeline()
    {
        var walk = Walk(ceiling: null);

        // A policy with no bound at all has no timeline to lay out, and a block whose only row says
        // "no bound of any kind" would add length to a message that already said everything.
        if (walk.Bound == Bound.Nothing)
            return null;

        var text = new StringWriter();
        text.Write(Headline());
        text.Write('\n');
        text.Write('\n');
        WriteWalk(text, walk);
        return text.ToString();
    }

    /// <summary>
    ///     One line: what the policy is configured to do and which bound binds first. The form a health
    ///     payload can carry per policy, where the full <see cref="Explain()" /> would be kilobytes of
    ///     ASCII per row.
    /// </summary>
    /// <returns>The summary.</returns>
    internal string Summarize()
    {
        var readings = Read();
        var bound = First(readings, Walk(readings.Ceiling));

        return Headline() + "; bound first by " + bound.Name + " (" + bound.Detail + ")";
    }

    /// <summary>What the policy is, in one line.</summary>
    private string Headline()
    {
        var name = Name is { Length: > 0 } given ? "\"" + given + "\"" : "(unnamed)";

        return "Policy " + name + " - "
               + Count(Attempts < 1 ? 1 : Attempts, "attempt") + ", "
               + (Deadline == Timeout.InfiniteTimeSpan ? "no deadline" : Compact(Deadline) + " deadline") + ", "
               + (AttemptTimeout == Timeout.InfiniteTimeSpan ? "no attempt timeout" : Compact(AttemptTimeout) + " attempt timeout");
    }

    /// <summary>
    ///     Everything the explanation needs that is not on the record: whether the policy is valid, what
    ///     each adaptive term currently reads, and which budget a retry would actually charge.
    /// </summary>
    /// <returns>The readings. All null when the policy cannot be executed.</returns>
    /// <remarks>
    ///     The validity check comes first and the readings are only taken when it passes, because
    ///     reading one validates the policy - see <see cref="Measured" /> - and
    ///     <see cref="Explain()" /> is called on invalid policies on purpose.
    /// </remarks>
    private Readings Read()
    {
        var problems = Problems();

        return problems.Count > 0
            ? new Readings(problems, null, null, null, null)
            : new Readings([], ReadCeiling(), ReadBackoffBase(), ReadHedgeThreshold(), ExecutionState.BudgetFor(this));
    }

    /// <summary>
    ///     This attempt's bound before the deadline is applied, the same way <c>Ceiling</c> resolves it:
    ///     <see cref="AttemptTimeout" />, lowered by the measured ceiling when there is one below it.
    /// </summary>
    /// <param name="ceiling">The measured ceiling, or null when none is configured or it is still cold.</param>
    /// <returns>The bound, or <see cref="Timeout.InfiniteTimeSpan" /> when there is none.</returns>
    private TimeSpan PerAttempt(TimeSpan? ceiling) =>
        ceiling is { } measured && (AttemptTimeout == Timeout.InfiniteTimeSpan || measured < AttemptTimeout)
            ? measured
            : AttemptTimeout;

    /// <summary>
    ///     Walks the worst case: every attempt runs to its full bound and every backoff takes the
    ///     longest delay its jitter allows, which is the sequence the executor can actually reach.
    /// </summary>
    /// <param name="ceiling">The measured ceiling, or null to walk the configuration alone.</param>
    /// <returns>The rows to print and the facts the bound sentence is built from.</returns>
    private Walked Walk(TimeSpan? ceiling)
    {
        var walked = new Walked();
        var perAttempt = PerAttempt(ceiling);
        var measuredCeiling = perAttempt != AttemptTimeout;
        var attempts = Attempts < 1 ? 1 : Attempts;
        var bounded = Deadline != Timeout.InfiniteTimeSpan;
        var offset = TimeSpan.Zero;

        // Read once. The walk asks for a delay per attempt, and re-reading a memoized window per
        // iteration would charge a diagnostic for work whose answer cannot change while it runs.
        var measuredBase = ceiling is null ? null : ReadBackoffBase();

        walked.CeilingWon = measuredCeiling;

        for (var number = 1; ; number++)
        {
            var left = bounded ? Deadline - offset : Timeout.InfiniteTimeSpan;

            if (bounded && left <= TimeSpan.Zero)
            {
                walked.Stop(Bound.Deadline, Deadline);
                walked.Add("deadline", Clock(Deadline), "DeadlineExceededException");
                return walked;
            }

            var attempt = Effective(perAttempt, left);
            var clamped = bounded && attempt != perAttempt;

            if (number <= ExplainRows)
            {
                walked.Add(
                    "attempt " + number.ToString(CultureInfo.InvariantCulture),
                    Clock(offset) + " -> " + (attempt == Timeout.InfiniteTimeSpan ? "unbounded" : Clock(offset + attempt)),
                    AttemptNote(perAttempt, left, measuredCeiling));
            }

            walked.Started = number;

            if (clamped && walked.Clamped == 0)
            {
                walked.Clamped = number;
                walked.ClampedTo = attempt;
            }

            // No deadline and no per-attempt bound: there is no arithmetic to do, because there is no
            // number a caller can be told. This is the one case where the honest answer is "forever".
            if (attempt == Timeout.InfiniteTimeSpan)
            {
                walked.Stop(Bound.Nothing, Timeout.InfiniteTimeSpan);
                return walked;
            }

            var end = Add(offset, attempt);

            if (bounded && end >= Deadline)
            {
                walked.Stop(Bound.Deadline, Deadline);
                walked.Add("deadline", Clock(Deadline), "DeadlineExceededException");
                return walked;
            }

            if (number >= attempts)
            {
                walked.Stop(measuredCeiling ? Bound.Ceiling : Bound.AttemptTimeout, end);
                walked.Add("exhausted", Clock(end), Count(attempts, "attempt") + " spent, the last failure rethrown");
                return walked;
            }

            if (number >= ExplainSteps)
            {
                walked.Truncated = true;
                walked.Partial = true;
                walked.Stop(measuredCeiling ? Bound.Ceiling : Bound.AttemptTimeout, end);
                walked.Add("...", "at " + Clock(end), Count(number, "attempt") + " of " + attempts.ToString(CultureInfo.InvariantCulture) + " walked");
                return walked;
            }

            // A custom curve is the caller's own function, and running it to render an explanation
            // would run their code at a point they never asked for. So the walk stops and says so,
            // rather than reporting a total that assumed a delay.
            if (!Backoff.TryRange(number + 1, measuredBase, out var shortest, out var longest))
            {
                walked.Custom = true;
                walked.Stop(bounded ? Bound.Deadline : Bound.Nothing, bounded ? Deadline : Timeout.InfiniteTimeSpan);
                walked.Add("backoff", "unknown", "Backoff.Custom() decides each delay at the call");
                return walked;
            }

            // The retry decision refuses a delay with nothing left behind it, so the deadline ends the
            // call here rather than sleeping through it.
            if (bounded && longest >= Deadline - end)
            {
                walked.Stop(Bound.Deadline, end);
                walked.Add("deadline", Clock(end), "the backoff would outlast the deadline");
                return walked;
            }

            if (number < ExplainRows)
            {
                walked.Add(
                    "backoff",
                    "+" + Compact(shortest) + " -> " + Compact(longest),
                    number == 1 ? Curve() : string.Empty);
            }
            else if (!walked.Truncated)
            {
                walked.Truncated = true;
                walked.Add("...", string.Empty, "attempts " + (ExplainRows + 1).ToString(CultureInfo.InvariantCulture) + " onwards not shown");
            }

            offset = Add(end, longest);
        }
    }

    /// <summary>What bounds an attempt, in the shape the timeline's note column wants.</summary>
    private string AttemptNote(TimeSpan perAttempt, TimeSpan left, bool measured)
    {
        var name = measured ? "measured ceiling" : "attempt timeout";

        if (perAttempt == Timeout.InfiniteTimeSpan)
            return left == Timeout.InfiniteTimeSpan ? "no bound of any kind" : Clock(left) + " left on the deadline";

        if (left == Timeout.InfiniteTimeSpan)
            return Compact(perAttempt) + " " + name + ", no deadline";

        return "min(" + Compact(perAttempt) + " " + name + ", " + Clock(left) + " left)";
    }

    /// <summary>The backoff curve, in one clause.</summary>
    private string Curve()
    {
        var curve = Backoff.Kind switch
        {
            BackoffKind.Constant => "constant " + Compact(Backoff.TransientBase),
            BackoffKind.Custom => "custom",
            _ => "exponential x" + Backoff.Factor.ToString("0.##", CultureInfo.InvariantCulture)
                 + " from " + Compact(Backoff.TransientBase),
        };

        if (Backoff.Kind == BackoffKind.Custom)
            return curve;

        var jitter = Backoff.Jitter switch
        {
            Jitter.Full => "full jitter",
            Jitter.Equal => "equal jitter",
            _ => "no jitter",
        };

        var capped = Backoff.MaximumDelay == Timeout.InfiniteTimeSpan
            ? ", uncapped"
            : ", capped at " + Compact(Backoff.MaximumDelay);

        return curve + ", " + jitter + capped;
    }

    /// <summary>The bound that ends the worst case, named and explained.</summary>
    /// <param name="readings">The live readings, for the two bounds that are a current state rather than a number.</param>
    /// <param name="walked">The worst-case walk.</param>
    /// <returns>The name and the sentence behind it.</returns>
    /// <remarks>
    ///     A refusal comes before any arithmetic: a call refused by the breaker or by a spent budget
    ///     never reaches the timeline below it, so naming the deadline there would name a bound the call
    ///     will not live long enough to meet.
    /// </remarks>
    private BoundFirst First(in Readings readings, Walked walked)
    {
        if (Breaker is { } breaker && breaker.State != BreakerState.Closed)
        {
            return new BoundFirst(
                "the breaker",
                "it is " + Lower(breaker.State.ToString()) + ", so calls are refused now; everything below applies again when it closes");
        }

        if (Attempts > 1 && readings.Budget is { } budget && budget.Utilization >= 1)
        {
            return new BoundFirst(
                "the retry budget",
                "it is fully spent, so no retry is funded right now and each call gets one attempt");
        }

        var unused = walked.Worst != Timeout.InfiniteTimeSpan && Deadline != Timeout.InfiniteTimeSpan
            ? Compact(Deadline - walked.Worst)
            : null;

        return walked.Bound switch
        {
            // Checked before the two per-attempt arms below, which would otherwise report a lower
            // bound as if it were the total.
            Bound.AttemptTimeout or Bound.Ceiling when walked.Partial => new BoundFirst(
                "the attempt count",
                "the first " + Count(walked.Started, "attempt") + " of " + Attempts.ToString(CultureInfo.InvariantCulture)
                + " already reach " + Clock(walked.Worst) + " at " + Compact(PerAttempt(readings.Ceiling))
                + " each, and there is no deadline to stop the rest"),

            Bound.Nothing when walked.Custom => new BoundFirst(
                "nothing you can state in advance",
                "a custom backoff curve decides the delays and there is no deadline behind it, so the total is whatever that function returns"),

            Bound.Nothing when IsPassthrough => new BoundFirst(
                "nothing",
                "every bound is off, so a call is the callback's own task and the executor adds no frame"),

            Bound.Nothing => new BoundFirst(
                "nothing",
                "neither Deadline nor AttemptTimeout is set, so one attempt can run for as long as the callback does"),

            Bound.Deadline when walked.Custom => new BoundFirst(
                "the deadline",
                "a custom backoff curve decides the delays, so " + Compact(Deadline) + " is the only bound that can be stated in advance"),

            Bound.Deadline when walked.Clamped > 0 && PerAttempt(readings.Ceiling) == Timeout.InfiniteTimeSpan => new BoundFirst(
                "the deadline",
                "there is no per-attempt bound, so attempt " + walked.Clamped.ToString(CultureInfo.InvariantCulture)
                + " gets whatever is left of the deadline - " + Clock(walked.ClampedTo) + Never(walked.Clamped)),

            Bound.Deadline when walked.Clamped > 0 => new BoundFirst(
                "the deadline",
                "attempt " + walked.Clamped.ToString(CultureInfo.InvariantCulture) + " is clamped to " + Clock(walked.ClampedTo)
                + ", " + Share(walked.ClampedTo, PerAttempt(readings.Ceiling)) + " of the "
                + Compact(PerAttempt(readings.Ceiling)) + (walked.CeilingWon ? " measured ceiling" : " attempt timeout")
                + Never(walked.Clamped)),

            Bound.Deadline => new BoundFirst(
                "the deadline",
                "the worst case reaches " + Clock(walked.Worst) + " after " + Count(walked.Started, "attempt")
                + ", leaving too little of the " + Compact(Deadline) + " deadline for attempt "
                + (walked.Started + 1).ToString(CultureInfo.InvariantCulture) + " to run in"),

            Bound.Ceiling => new BoundFirst(
                "the measured attempt ceiling",
                "it currently reads " + Compact(PerAttempt(readings.Ceiling)) + ", below the " + Compact(AttemptTimeout)
                + " attempt timeout, so it is what bounds each attempt; the worst case is " + Clock(walked.Worst)
                + (unused is null ? "" : " and leaves " + unused + " of the deadline unused")),

            _ => new BoundFirst(
                "the attempt timeout",
                Count(walked.Started, "attempt") + " of at most " + Compact(AttemptTimeout)
                + " gives a worst case of " + Clock(walked.Worst)
                + (unused is null ? ", with no deadline behind it" : ", leaving " + unused + " of the deadline unused")),
        };
    }

    /// <summary>Which attempts a deadline-clamped one leaves with nothing to run in.</summary>
    private string Never(int clamped)
    {
        if (clamped >= Attempts)
            return string.Empty;

        return clamped + 1 == Attempts
            ? ", and attempt " + Attempts.ToString(CultureInfo.InvariantCulture) + " never starts"
            : ", and attempts " + (clamped + 1).ToString(CultureInfo.InvariantCulture) + "-"
              + Attempts.ToString(CultureInfo.InvariantCulture) + " never start";
    }

    /// <summary>The load a single call can put on the dependency, and what holds it there.</summary>
    private string Load(in Readings readings)
    {
        if (Attempts <= 1 && Hedge is null)
            return "one attempt per call, so no retry load and no budget to fund one.";

        var text = "up to " + Attempts.ToString("0.0", CultureInfo.InvariantCulture) + "x per call from retries";

        if (Hedge is { } hedge)
        {
            text += ", plus up to " + Count(hedge.MaximumConcurrent, "concurrent hedged leg")
                    + " once a call passes the hedge threshold";
        }

        if (Budget.IsNone)
            return text + ". No retry budget, so every retry the bounds allow is made.";

        var fraction = readings.Budget?.Fraction ?? RetryBudget.DefaultFraction;
        var floor = readings.Budget?.MinimumPerSecond ?? RetryBudget.DefaultMinimumPerSecond;

        var which = Budget.IsAutomatic
            ? "automatic retry budget, which is private to this policy,"
            : Budget.Name is { Length: > 0 } shared
                ? "shared retry budget \"" + shared + "\""
                : "retry budget";

        text += ". The " + which + " holds the sustained rate to "
                + (1 + fraction).ToString("0.0#", CultureInfo.InvariantCulture) + "x - "
                + Ratio(fraction) + " of successful attempts, with a "
                + floor.ToString("0.##", CultureInfo.InvariantCulture) + "/s floor";

        return readings.Budget is { } live
            ? text + " - and it is " + Ratio(live.Utilization) + " spent."
            : text + ".";
    }

    /// <summary>The breaker's state and every condition that would open it.</summary>
    private string BreakerText()
    {
        if (Breaker is not { } breaker)
            return "not configured, so nothing stops a call reaching a dependency that is already down.";

        var settings = breaker.Settings;
        var text = Lower(breaker.State.ToString());

        if (breaker.OpenedAt is { } since)
            text += " since " + since.ToString("O", CultureInfo.InvariantCulture);

        var trips = new List<string>(4);

        if (settings.ConsecutiveFailures > 0)
            trips.Add(Count(settings.ConsecutiveFailures, "consecutive failure"));

        if (settings.FailureRatio is { } ratio)
        {
            trips.Add(Ratio(ratio) + " of " + settings.MinimumCalls.ToString(CultureInfo.InvariantCulture)
                      + "+ calls in " + Compact(settings.TripWindow));
        }

        if (settings.Failures is { } failures)
        {
            trips.Add(failures.Multiple.ToString("0.##", CultureInfo.InvariantCulture) + "x its own failure rate ("
                      + (breaker.NormalFailureRate is { } normal ? "now " + Ratio(normal) : "cold") + ")");
        }

        if (settings.SlowCallThreshold is { } slow)
            trips.Add(Ratio(settings.SlowCallRatio) + " of calls slower than " + Compact(slow));

        if (settings.SlowCalls is { } measured)
        {
            trips.Add(Ratio(settings.SlowCallRatio) + " of calls slower than "
                      + measured.Multiple.ToString("0.##", CultureInfo.InvariantCulture) + "x normal ("
                      + (breaker.NormalLatency is { } latency ? "now " + Compact(latency) : "cold") + ")");
        }

        text += trips.Count == 0
            ? ". Nothing opens it."
            : ". Opens at " + Join(trips) + ".";

        text += " A break lasts " + Compact(settings.BreakDuration);

        if (settings.MaximumBreakDuration > settings.BreakDuration)
            text += ", doubling per consecutive open to at most " + Compact(settings.MaximumBreakDuration);

        text += "; " + Count(settings.ProbeSuccesses, "probe success")
                + (settings.ProbeSuccesses == 1 ? " closes it" : " close it");

        return text + (settings.Recovery is { } recovery
            ? ", then traffic ramps back over " + Ratio(recovery.Fraction) + " steps."
            : ".");
    }

    /// <summary>What each adaptive term currently reads, and which of them is warm.</summary>
    private void WriteMeasured(TextWriter writer, in Readings readings)
    {
        if (readings.Problems.Count > 0)
        {
            Paragraph(writer, "Measured now:", "unavailable until the policy is valid.");
            return;
        }

        if (!Adaptive)
        {
            Paragraph(writer, "Measured now:", "nothing. Adaptive is false, so this policy is bound only by the constants above.");
            return;
        }

        Rows(
            writer,
            "Measured now:",
            [
                Reading(
                    "attempt ceiling",
                    readings.Ceiling,
                    AttemptCeiling is { } ceiling ? Quantile(ceiling.Multiple, ceiling.Quantile, ceiling.Window, ceiling.MinimumSamples) : null),
                Reading(
                    "backoff base",
                    readings.BackoffBase,
                    Backoff.MeasuredBase is { } measured ? Quantile(measured.Multiple, measured.Quantile, measured.Window, measured.MinimumSamples) : null),
                Reading(
                    "hedge threshold",
                    readings.HedgeThreshold,
                    Hedge is { } hedge ? Quantile(multiple: 1, hedge.Quantile, hedge.Window, hedge.MinimumSamples) : null),
            ]);
    }

    /// <summary>One reading: its value when warm, and the configuration behind it either way.</summary>
    private static (string, string, string) Reading(string name, TimeSpan? value, string? configured) =>
        configured is null
            ? (name, "-", "not configured")
            : (name, value is { } warm ? Compact(warm) : "-", (value is null ? "cold - " : string.Empty) + configured);

    /// <summary>
    ///     A measured term's configuration, short enough for a column: which quantile of which window,
    ///     what it is multiplied by, and how many samples it needs before it says anything.
    /// </summary>
    private static string Quantile(double multiple, double quantile, TimeSpan window, int samples)
    {
        var term = multiple == 1
            ? "p" + (quantile * 100).ToString("0.##", CultureInfo.InvariantCulture)
            : multiple.ToString("0.##", CultureInfo.InvariantCulture) + "x p" + (quantile * 100).ToString("0.##", CultureInfo.InvariantCulture);

        return term + " over " + Compact(window) + ", " + Count(samples, "sample") + " minimum";
    }

    /// <summary>The classifier's name and every rule in evaluation order.</summary>
    private void WriteClassifier(TextWriter writer)
    {
        if (Classifier is null)
        {
            Paragraph(writer, "Classifier:", "none. The policy is not valid without one.");
            return;
        }

        Label(writer, "Classifier:");
        writer.Write(Classifier.Name);
        writer.Write('\n');
        Classifier.Describe(writer, ExplainIndent + "  ");
    }

    /// <summary>
    ///     The worst-case timeline, plus the notes that qualify it: what the walk assumed about the
    ///     failure, and which configured feature runs outside the rows it laid out.
    /// </summary>
    private void WriteWalk(TextWriter writer, Walked walked)
    {
        var rows = walked.Rows;

        // The walk assumes transient failures throughout, because that is the sequence the backoff
        // curve above describes. Throttling takes the other base, and a server that says when to come
        // back beats both - facts the timeline would otherwise quietly contradict.
        if (walked.Rows.Count > 1 && Backoff.ThrottledBase != Backoff.TransientBase && Backoff.Kind != BackoffKind.Custom)
        {
            rows.Add(("(a throttled attempt uses the " + Compact(Backoff.ThrottledBase)
                      + " throttled base; Retry-After wins over both)", string.Empty, string.Empty));
        }

        if (Hedge is not null)
            rows.Add(("(a hedged leg runs alongside, and does not extend the deadline)", string.Empty, string.Empty));

        if (BeforeAttempt is not null)
            rows.Add(("(BeforeAttempt runs outside the attempt timeout, so its own time is unbounded)", string.Empty, string.Empty));

        Rows(writer, "Worst case:", rows);
    }

    /// <summary>
    ///     A labelled block of up to three columns, aligned under the label. The column widths come from
    ///     the rows themselves, so a block is as narrow as its own contents allow and no row is padded
    ///     past its last non-empty cell.
    /// </summary>
    private static void Rows(TextWriter writer, string label, IReadOnlyList<(string First, string Second, string Third)> rows)
    {
        if (rows.Count == 0)
        {
            writer.Write(Label(label));
            writer.Write('\n');
            return;
        }

        var one = Widest(rows, static row => row.Second.Length + row.Third.Length == 0 ? 0 : row.First.Length);
        var two = Widest(rows, static row => row.Third.Length == 0 ? 0 : row.Second.Length);

        for (var i = 0; i < rows.Count; i++)
        {
            var (first, second, third) = rows[i];
            writer.Write(i == 0 ? Label(label) : ExplainIndent);

            if (second.Length + third.Length == 0)
            {
                writer.Write(first);
                writer.Write('\n');
                continue;
            }

            writer.Write(Pad(first, one));

            if (third.Length == 0)
            {
                writer.Write(second);
                writer.Write('\n');
                continue;
            }

            writer.Write(Pad(second, two));
            writer.Write(third);
            writer.Write('\n');
        }
    }

    /// <summary>The widest cell in a column, plus the two spaces that separate it from the next.</summary>
    private static int Widest(
        IReadOnlyList<(string First, string Second, string Third)> rows,
        Func<(string First, string Second, string Third), int> cell)
    {
        var widest = 0;

        for (var i = 0; i < rows.Count; i++)
        {
            var width = cell(rows[i]);

            if (width > widest)
                widest = width;
        }

        return widest + 2;
    }

    /// <summary>A labelled block of prose, wrapped at <see cref="ExplainWidth" /> and aligned under the label.</summary>
    private static void Paragraph(TextWriter writer, string label, string text)
    {
        var width = ExplainWidth - ExplainLabel;
        var start = 0;
        var first = true;

        while (start < text.Length)
        {
            var take = text.Length - start <= width ? text.Length - start : Break(text, start, width);

            writer.Write(first ? Label(label) : ExplainIndent);
            writer.Write(text.AsSpan(start, take).TrimEnd());
            writer.Write('\n');

            first = false;
            start += take;

            while (start < text.Length && text[start] == ' ')
            {
                start++;
            }
        }
    }

    /// <summary>How many characters of a line fit before the last space inside the width.</summary>
    private static int Break(string text, int start, int width)
    {
        for (var i = start + width; i > start; i--)
        {
            if (text[i] == ' ')
                return i - start;
        }

        return width;
    }

    private static void Label(TextWriter writer, string label) => writer.Write(Label(label));

    private static string Label(string label) => "  " + Pad(label, ExplainLabel - 2);

    private static string Pad(string value, int width) => value.Length >= width ? value + "  " : value.PadRight(width);

    /// <summary>A count and its noun, pluralized. Enough of a rule for the handful of nouns used here.</summary>
    private static string Count(int value, string noun)
    {
        var number = value.ToString(CultureInfo.InvariantCulture);

        if (value == 1)
            return number + " " + noun;

        return number + " " + noun + (noun.EndsWith('s') || noun.EndsWith('x') || noun.EndsWith("ch", StringComparison.Ordinal) ? "es" : "s");
    }

    /// <summary>A fraction as a whole-number percentage, which is how every ratio in the library reads.</summary>
    private static string Ratio(double value) =>
        (value * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    /// <summary>How much of a bound something got, as a percentage of it.</summary>
    private static string Share(TimeSpan part, TimeSpan whole) =>
        whole <= TimeSpan.Zero || whole == Timeout.InfiniteTimeSpan
            ? "an unknown share"
            : Ratio(part.Ticks / (double)whole.Ticks);

    /// <summary>
    ///     The shortest honest rendering of a duration. The same format the <c>NRES004</c> diagnostic
    ///     uses, so the sentence a developer reads in the IDE and the one they get at runtime agree.
    /// </summary>
    private static string Compact(TimeSpan value)
    {
        if (value == Timeout.InfiniteTimeSpan)
            return "no bound";

        var magnitude = value == TimeSpan.MinValue ? TimeSpan.MaxValue : value < TimeSpan.Zero ? value.Negate() : value;

        return magnitude switch
        {
            _ when magnitude < TimeSpan.FromSeconds(1) => Number(value.TotalMilliseconds) + "ms",
            _ when magnitude < TimeSpan.FromMinutes(1) => Number(value.TotalSeconds) + "s",
            _ when magnitude < TimeSpan.FromHours(1) => Number(value.TotalMinutes) + "m",
            _ => Number(value.TotalHours) + "h",
        };

        static string Number(double amount) => amount.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>An offset on the timeline, at a fixed width so the column lines up.</summary>
    private static string Clock(TimeSpan value)
    {
        if (value == Timeout.InfiniteTimeSpan)
            return "unbounded";

        return value < TimeSpan.FromMinutes(10)
            ? value.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + "s"
            : value.TotalMinutes.ToString("0.00", CultureInfo.InvariantCulture) + "m";
    }

    /// <summary>
    ///     Two durations, saturating rather than overflowing. Only an unbounded policy with a
    ///     nonsensical attempt count can reach the saturation, and there the answer a caller needs is
    ///     "longer than anything you meant" rather than a wrapped negative duration.
    /// </summary>
    private static TimeSpan Add(TimeSpan first, TimeSpan second) =>
        second.Ticks > TimeSpan.MaxValue.Ticks - first.Ticks ? TimeSpan.MaxValue : first + second;

    private static string Join(List<string> parts) =>
        parts.Count switch
        {
            1 => parts[0],
            2 => parts[0] + " or " + parts[1],
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + ", or " + parts[^1],
        };

    private static string Capitalize(string text) =>
        text.Length == 0 || !char.IsLower(text[0]) ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Lower(string text) =>
        text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    /// <summary>Which bound ends the worst case.</summary>
    private enum Bound : byte
    {
        /// <summary>None of them: the call can run for as long as the callback does.</summary>
        Nothing,

        /// <summary>The wall-clock budget for the whole call.</summary>
        Deadline,

        /// <summary>The configured per-attempt constant, with the attempts spent inside the deadline.</summary>
        AttemptTimeout,

        /// <summary>The measured per-attempt ceiling, which is currently below the constant.</summary>
        Ceiling,
    }

    /// <summary>The name of the bound that binds, and the sentence behind it.</summary>
    private readonly record struct BoundFirst(string Name, string Detail);

    /// <summary>Everything the explanation needs that is not on the record.</summary>
    private readonly record struct Readings(
        List<string> Problems,
        TimeSpan? Ceiling,
        TimeSpan? BackoffBase,
        TimeSpan? HedgeThreshold,
        RetryBudget? Budget);

    /// <summary>The worst-case walk: its rows, and the facts the bound sentence is built from.</summary>
    private sealed class Walked
    {
        internal List<(string, string, string)> Rows { get; } = [];

        internal Bound Bound { get; private set; }

        internal TimeSpan Worst { get; private set; }

        /// <summary>How many attempts actually start.</summary>
        internal int Started { get; set; }

        /// <summary>The first attempt the remaining deadline shortened, or zero when none was.</summary>
        internal int Clamped { get; set; }

        /// <summary>What that attempt was shortened to.</summary>
        internal TimeSpan ClampedTo { get; set; }

        /// <summary>Whether the measured ceiling, rather than the constant, bounded each attempt.</summary>
        internal bool CeilingWon { get; set; }

        /// <summary>Whether a custom backoff curve stopped the arithmetic.</summary>
        internal bool Custom { get; set; }

        /// <summary>Whether rows were left out.</summary>
        internal bool Truncated { get; set; }

        /// <summary>
        ///     Whether the walk stopped short of the attempt count, so <see cref="Worst" /> is a lower
        ///     bound rather than the total.
        /// </summary>
        internal bool Partial { get; set; }

        internal void Add(string first, string second, string third) => Rows.Add((first, second, third));

        internal void Stop(Bound bound, TimeSpan worst)
        {
            Bound = bound;
            Worst = worst;
        }
    }
}
