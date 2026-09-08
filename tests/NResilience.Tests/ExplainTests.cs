using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace NResilience.Tests;

/// <summary>
///     <c>Resilience.Explain()</c>: the worst-case arithmetic nobody can do by reading the record.
///     <para>
///         The assertions here are about the one sentence that earns the method - which bound binds
///         first - and about the arithmetic behind it. Six configurations, one per bound: the deadline,
///         the attempt timeout, the measured ceiling, an exhausted budget, an open breaker, and
///         <see cref="Resilience.None" />, where the honest answer is "nothing".
///     </para>
/// </summary>
public sealed class ExplainTests
{
    /// <summary>Long enough that no test rolls a window slice and loses the samples it just recorded.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    // ---- Which bound binds ----

    /// <summary>
    ///     The shipped default is deadline-bound, and only just: three ten-second attempts and two
    ///     backoffs fit inside thirty seconds with 300 ms to spare, so the third attempt is the one the
    ///     remaining deadline shortens.
    /// </summary>
    [Fact]
    public void The_deadline_binds_when_the_attempts_would_outlast_it()
    {
        var explained = Prose(Resilience.Default);

        Assert.Contains("Bound first by:  the deadline.", explained, StringComparison.Ordinal);
        Assert.Contains("Attempt 3 is clamped to 9.70s, 97% of the 10s attempt timeout", explained, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A deadline shorter than one attempt timeout is the shape <c>NRES004</c> exists for, and the
    ///     explanation says which attempts never start rather than only that the number is unreachable.
    /// </summary>
    [Fact]
    public void The_deadline_binds_hard_when_it_is_shorter_than_one_attempt()
    {
        var explained = Prose(Resilience.Http with { Deadline = TimeSpan.FromSeconds(5) });

        Assert.Contains("Attempt 1 is clamped to 5.00s, 50% of the 10s attempt timeout", explained, StringComparison.Ordinal);
        Assert.Contains("attempts 2-3 never start", explained, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Attempts that all fit inside the deadline are bounded by the constant, and then the useful
    ///     fact is how much of the deadline goes unused - the number that says a deadline was copied
    ///     rather than chosen.
    /// </summary>
    [Fact]
    public void The_attempt_timeout_binds_when_every_attempt_fits_inside_the_deadline()
    {
        var explained = Prose(Resilience.Default with { Attempts = 2, AttemptTimeout = TimeSpan.FromSeconds(1) });

        Assert.Contains("Bound first by:  the attempt timeout.", explained, StringComparison.Ordinal);
        Assert.Contains("worst case of 2.10s", explained, StringComparison.Ordinal);
        Assert.Contains("leaving 27.9s of the deadline unused", explained, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Once the estimate is warm the measured ceiling is below the constant, and it is the ceiling -
    ///     not the constant a reader can see in the record - that bounds every attempt. This is the
    ///     distinction the method exists to make visible: configured is not in effect.
    /// </summary>
    [Fact]
    public async Task The_measured_ceiling_binds_once_it_is_warm_and_below_the_constant()
    {
        var time = new FakeTimeProvider();

        var policy = Resilience.Default with
        {
            Attempts = 2,
            Deadline = TimeSpan.FromMinutes(5),
            AttemptTimeout = TimeSpan.FromSeconds(30),
            AttemptCeiling = AttemptCeiling.Above(3) with { Window = Window },
            Time = time,
        };

        Assert.Contains("Bound first by:  the attempt timeout.", Prose(policy), StringComparison.Ordinal);

        await WarmAsync(policy, time, TimeSpan.FromMilliseconds(100), 20);

        var explained = Prose(policy);

        Assert.Contains("Bound first by:  the measured attempt ceiling.", explained, StringComparison.Ordinal);
        Assert.Contains("below the 30s attempt timeout", explained, StringComparison.Ordinal);

        // And the reading itself is reported as warm, beside the configuration that produced it.
        Assert.Contains("3x p95 over 1h", explained, StringComparison.Ordinal);
        Assert.DoesNotContain("attempt ceiling  -", explained, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A spent budget refuses every retry, so naming a bound the call will not live long enough to
    ///     meet would be worse than useless. The budget comes first for the same reason the breaker does.
    /// </summary>
    [Fact]
    public void An_exhausted_retry_budget_binds_before_any_arithmetic()
    {
        var time = new FakeTimeProvider();
        var budget = RetryBudget.Of(fraction: 0.1, minimumPerSecond: 1, time: time);

        // Drain the bucket. A cold one starts full at ten seconds of the floor rate.
        while (budget.Utilization < 1)
        {
            Assert.True(budget.Utilization < 1);
            Drain(budget);
        }

        var explained = Prose(Resilience.Default with { Budget = budget, Time = time });

        Assert.Contains("Bound first by:  the retry budget.", explained, StringComparison.Ordinal);
        Assert.Contains("no retry is funded right now", explained, StringComparison.Ordinal);
    }

    /// <summary>An open breaker refuses the call outright, which is a state and not a number.</summary>
    [Fact]
    public void An_open_breaker_binds_before_any_arithmetic()
    {
        var breaker = Breaker.Of(name: "payments");
        breaker.Isolate();

        var explained = Prose(Resilience.Default with { Breaker = breaker });

        Assert.Contains("Bound first by:  the breaker.", explained, StringComparison.Ordinal);
        Assert.Contains("It is isolated", explained, StringComparison.Ordinal);

        // And the breaker's own section reports the state, since when, and every condition that would
        // open it - the three facts a support question about a refused call needs at once.
        Assert.Contains("Breaker:         isolated since ", explained, StringComparison.Ordinal);
        Assert.Contains("Opens at 5 consecutive failures", explained, StringComparison.Ordinal);
        Assert.Contains("2 probe successes close it", explained, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Passthrough has nothing to explain and says so. The alternative - printing a timeline for a
    ///     policy that imposes no bound - is the kind of output that teaches a reader to stop reading it.
    /// </summary>
    [Fact]
    public void Passthrough_reports_that_nothing_binds()
    {
        var explained = Prose(Resilience.None);

        Assert.Contains("Bound first by:  nothing.", explained, StringComparison.Ordinal);
        Assert.Contains("Every bound is off", explained, StringComparison.Ordinal);
        Assert.Contains("one attempt per call, so no retry load", explained, StringComparison.Ordinal);
        Assert.Contains("Adaptive is false", explained, StringComparison.Ordinal);
    }

    /// <summary>
    ///     No deadline and no attempt timeout is a call that can hang forever, and that is worth being
    ///     told in the first sentence rather than derived from two absent numbers.
    /// </summary>
    [Fact]
    public void An_unbounded_policy_says_so_rather_than_reporting_a_total()
    {
        var explained = Prose(Resilience.Default with
        {
            Deadline = Timeout.InfiniteTimeSpan,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            AttemptCeiling = null,
        });

        Assert.Contains("Bound first by:  nothing.", explained, StringComparison.Ordinal);
        Assert.Contains("as long as the callback does", explained, StringComparison.Ordinal);
        Assert.Contains("no bound of any kind", explained, StringComparison.Ordinal);
    }

    // ---- The timeline ----

    /// <summary>
    ///     Every attempt's start offset is the previous attempt's ceiling plus the longest delay its
    ///     jitter allows, which is the sequence the executor can actually reach. The numbers here are the
    ///     shipped defaults, worked by hand: 10s, +100ms, 10s, +200ms, then 9.7s of a spent deadline.
    /// </summary>
    [Fact]
    public void The_timeline_walks_the_sequence_the_executor_can_actually_reach()
    {
        var explained = Prose(Resilience.Default);

        Assert.Contains("attempt 1  0.00s -> 10.00s", explained, StringComparison.Ordinal);
        Assert.Contains("backoff    +0ms -> 100ms", explained, StringComparison.Ordinal);
        Assert.Contains("attempt 2  10.10s -> 20.10s", explained, StringComparison.Ordinal);
        Assert.Contains("backoff    +0ms -> 200ms", explained, StringComparison.Ordinal);
        Assert.Contains("attempt 3  20.30s -> 30.00s", explained, StringComparison.Ordinal);
        Assert.Contains("deadline   30.00s", explained, StringComparison.Ordinal);
        Assert.Contains("DeadlineExceededException", explained, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Equal jitter halves the range rather than opening it to zero, and the timeline reports both
    ///     ends: the shortest a delay can be and the longest, which is the one the walk spends.
    /// </summary>
    [Fact]
    public void The_timeline_reports_the_range_the_jitter_allows()
    {
        var explained = Prose(Resilience.Default with
        {
            Attempts = 2,
            AttemptTimeout = TimeSpan.FromSeconds(1),
            Backoff = Backoff.Exponential(transientBase: TimeSpan.FromMilliseconds(400)) with { Jitter = Jitter.Equal },
        });

        Assert.Contains("backoff    +200ms -> 400ms", explained, StringComparison.Ordinal);
        Assert.Contains("equal jitter", explained, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A custom curve computes its delays at the call, so the worst case genuinely is not knowable
    ///     in advance. The walk says that instead of running the caller's function to find out.
    /// </summary>
    [Fact]
    public void A_custom_backoff_curve_is_reported_as_unknown_rather_than_probed()
    {
        var called = 0;

        var explained = Prose(Resilience.Default with
        {
            Backoff = Backoff.Custom(_ =>
            {
                called++;
                return TimeSpan.FromSeconds(1);
            }),
        });

        Assert.Equal(0, called);
        Assert.Contains("Backoff.Custom() decides each delay at the call", explained, StringComparison.Ordinal);
        Assert.Contains("A custom backoff curve decides the delays", explained, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The walk assumes transient failures, because that is the curve it printed. Throttling takes
    ///     the other base and a server's <c>Retry-After</c> beats both, and a timeline that quietly
    ///     omitted that would be a timeline a reader could be surprised by.
    /// </summary>
    [Fact]
    public void The_timeline_names_what_it_assumed_about_the_failure()
    {
        Assert.Contains(
            "(a throttled attempt uses the 1s throttled base; Retry-After wins over both)",
            Prose(Resilience.Default),
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     A hedged leg and a <c>BeforeAttempt</c> hook both run outside the rows the walk lays out, so
    ///     each is named rather than left to make the timeline wrong.
    /// </summary>
    [Fact]
    public void The_timeline_names_the_features_that_run_outside_it()
    {
        var explained = Prose(Resilience.Default with
        {
            Hedge = Hedge.At(),
            BeforeAttempt = _ => Task.CompletedTask,
        });

        Assert.Contains("a hedged leg runs alongside", explained, StringComparison.Ordinal);
        Assert.Contains("BeforeAttempt runs outside the attempt timeout", explained, StringComparison.Ordinal);
        Assert.Contains("plus up to 2 concurrent hedged legs", explained, StringComparison.Ordinal);
    }

    /// <summary>An attempt count nobody should have written still terminates, and reports what it walked.</summary>
    [Fact]
    public void An_absurd_attempt_count_terminates_and_says_it_was_truncated()
    {
        var explained = Prose(Resilience.Default with
        {
            Attempts = 100_000,
            Deadline = Timeout.InfiniteTimeSpan,
            AttemptTimeout = TimeSpan.FromSeconds(1),
            AttemptCeiling = null,
        });

        Assert.Contains("attempts 7 onwards not shown", explained, StringComparison.Ordinal);
        Assert.Contains("of 100000 walked", explained, StringComparison.Ordinal);

        // And the sentence reports a lower bound rather than passing off what it walked as the total.
        Assert.Contains("The first 1000 attempts of 100000 already reach", explained, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A custom curve with no deadline behind it has an attempt bound and no total, and the
    ///     sentence says exactly that rather than claiming no bound is set at all.
    /// </summary>
    [Fact]
    public void A_custom_curve_with_no_deadline_names_what_is_actually_unknowable()
    {
        var explained = Prose(Resilience.Default with
        {
            Deadline = Timeout.InfiniteTimeSpan,
            AttemptCeiling = null,
            Backoff = Backoff.Custom(static _ => TimeSpan.FromSeconds(value: 1)),
        });

        Assert.Contains("Bound first by:  nothing you can state in advance.", explained, StringComparison.Ordinal);
        Assert.Contains("there is no deadline behind it", explained, StringComparison.Ordinal);
        Assert.DoesNotContain("neither Deadline nor AttemptTimeout is set", explained, StringComparison.Ordinal);
    }

    // ---- The other three consumers ----

    /// <summary>
    ///     The message a bad policy throws carries the timeline, because for the mistake this diagnostic
    ///     is most often about - two bounds in the wrong order - the timeline is what makes it obvious.
    ///     <see cref="ResilienceConfigurationException.Problems" /> is unchanged: the timeline is
    ///     context, not a problem of its own.
    /// </summary>
    [Fact]
    public void The_configuration_exception_carries_the_timeline()
    {
        var policy = Resilience.Default with { Attempts = 0, AttemptTimeout = TimeSpan.FromSeconds(60) };
        var thrown = Assert.Throws<ResilienceConfigurationException>(policy.Validate);

        Assert.Equal(["Attempts must be at least 1; it is 0."], thrown.Problems);
        Assert.Contains("Attempts must be at least 1", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("Worst case:", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("min(1m attempt timeout, 30.00s left)", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("DeadlineExceededException", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An invalid policy explains itself rather than throwing. It has to: the message the exception
    ///     carries is one of this method's consumers, and a renderer that threw on the values it was
    ///     built to describe would be no renderer at all.
    /// </summary>
    [Fact]
    public void An_invalid_policy_explains_itself_rather_than_throwing()
    {
        var explained = Prose(Resilience.Default with { Attempts = 0, Classifier = null! });

        Assert.Contains("Not valid:", explained, StringComparison.Ordinal);
        Assert.Contains("Classifier must not be null.", explained, StringComparison.Ordinal);

        // The readings are the one section an invalid policy cannot have: taking one validates.
        Assert.Contains("unavailable until the policy is valid", explained, StringComparison.Ordinal);
        Assert.Contains("The policy is not valid without one", explained, StringComparison.Ordinal);
    }

    /// <summary>The writer overload writes the same text, for a log that wants it on a stream.</summary>
    [Fact]
    public void The_writer_overload_writes_the_same_text()
    {
        var writer = new StringWriter();
        Resilience.Default.Explain(writer);

        Assert.Equal(Resilience.Default.Explain(), writer.ToString());
        Assert.Throws<ArgumentNullException>(() => Resilience.Default.Explain(null!));
    }

    /// <summary>
    ///     Every rule the classifier will apply, in evaluation order, from the same renderer
    ///     <c>Classifier.ToString()</c> uses. Two renderers would be two answers to "what will this
    ///     retry?".
    /// </summary>
    [Fact]
    public void The_classifier_section_lists_every_rule_in_evaluation_order()
    {
        var explained = Prose(Resilience.Http);

        Assert.Contains("exception HttpRequestException -> Transient", explained, StringComparison.Ordinal);
        Assert.Contains("result HttpResponseMessage -> (predicate)", explained, StringComparison.Ordinal);
        Assert.Contains("any other exception -> Permanent", explained, StringComparison.Ordinal);
        Assert.Contains("any other result -> Ok", explained, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Reading the explanation does not change the policy, and two identically-configured policies
    ///     stay equal after one of them has explained itself - the same rule every other per-instance
    ///     derived value in the library follows.
    /// </summary>
    [Fact]
    public void Explaining_a_policy_leaves_it_equal_to_its_twin()
    {
        var one = Resilience.Http with { Deadline = TimeSpan.FromSeconds(5) };
        var two = Resilience.Http with { Deadline = TimeSpan.FromSeconds(5) };

        _ = one.Explain();

        Assert.Equal(two, one);
        Assert.Equal(two.GetHashCode(), one.GetHashCode());
        Assert.Equal(one.Explain(), two.Explain());
    }

    /// <summary>
    ///     The explanation with its wrapping undone: every line trimmed of the indent that aligns it
    ///     under its label, and the lot joined with single spaces. An assertion about a sentence should
    ///     not have to know where the renderer chose to break the line.
    /// </summary>
    private static string Prose(Resilience policy)
    {
        var text = new StringBuilder();

        foreach (var line in policy.Explain().Split('\n'))
        {
            if (text.Length > 0)
                text.Append(' ');

            text.Append(line.TrimStart());
        }

        return text.ToString();
    }

    /// <summary>
    ///     Spends the bucket until it refuses. The floor rate is one per second and a cold bucket banks
    ///     ten seconds of it, so this takes ten withdrawals and then reads full.
    /// </summary>
    private static void Drain(RetryBudget budget)
    {
        var policy = Resilience.Default with { Attempts = 2, Budget = budget, Backoff = Backoff.None };

        _ = policy.TryRunAsync(Task<int> (_) => throw new IOException()).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>Records <paramref name="times" /> samples of <paramref name="duration" /> into the policy's estimate.</summary>
    private static async Task WarmAsync(Resilience policy, FakeTimeProvider time, TimeSpan duration, int times)
    {
        for (var i = 0; i < times; i++)
        {
            await policy.RunAsync(_ =>
            {
                time.Advance(duration);
                return Task.FromResult(1);
            });
        }
    }
}
