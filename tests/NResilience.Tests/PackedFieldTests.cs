namespace NResilience.Tests;

/// <summary>
///     The three structs whose size is paid per call keep their optional value-type fields packed
///     behind properties of the original type: <see cref="Verdict.RetryAfter" />,
///     <see cref="Attempt.Verdict" />, <see cref="CallEvent.Delay" /> and
///     <see cref="CallEvent.Reason" />. The sizes are gated in <c>NResilience.Gates</c>; what is
///     asserted here is that the encodings say the same thing the fields did - in particular that
///     "absent" and "zero" stay distinguishable, which is the one thing a biased integer can get wrong.
/// </summary>
public sealed class PackedFieldTests
{
    [Fact]
    public void A_default_verdict_has_no_pushback()
    {
        Assert.Null(default(Verdict).RetryAfter);
        Assert.Null(Verdict.Ok.RetryAfter);
        Assert.Null(Verdict.Transient.RetryAfter);
        Assert.Null(Verdict.Throttled().RetryAfter);
    }

    [Fact]
    public void A_zero_pushback_is_not_the_absence_of_one()
    {
        Assert.Equal(TimeSpan.Zero, Verdict.Throttled(TimeSpan.Zero).RetryAfter);
        Assert.NotEqual(Verdict.Throttled(), Verdict.Throttled(TimeSpan.Zero));
    }

    [Fact]
    public void A_pushback_round_trips_to_the_tick()
    {
        var after = TimeSpan.FromMilliseconds(1234.5678);

        Assert.Equal(after, Verdict.Throttled(after).RetryAfter);
        Assert.Equal(after, Verdict.Limited(after).RetryAfter);
        Assert.True(Verdict.Limited(after).SelfImposed);
        Assert.False(Verdict.Throttled(after).SelfImposed);
    }

    /// <summary>
    ///     A pushback into the past has no reading other than "come back now", and
    ///     <c>Backoff.Compute</c> clamped it to zero on the way out anyway. It is clamped at
    ///     construction instead, so what the verdict reports and what the curve serves agree.
    /// </summary>
    [Fact]
    public void A_negative_pushback_reads_back_as_zero()
    {
        Assert.Equal(TimeSpan.Zero, Verdict.Throttled(TimeSpan.FromSeconds(-5)).RetryAfter);
        Assert.Equal(TimeSpan.Zero, Verdict.Limited(TimeSpan.FromSeconds(-5)).RetryAfter);
    }

    [Fact]
    public void Verdicts_that_differ_only_in_pushback_are_not_equal()
    {
        var one = Verdict.Throttled(TimeSpan.FromSeconds(1));
        var two = Verdict.Throttled(TimeSpan.FromSeconds(2));

        Assert.NotEqual(one, two);
        Assert.Equal(one, Verdict.Throttled(TimeSpan.FromSeconds(1)));
        Assert.Equal(one.GetHashCode(), Verdict.Throttled(TimeSpan.FromSeconds(1)).GetHashCode());
    }

    /// <summary>
    ///     The log stores the kind and the origin flag in one byte and rebuilds the verdict on the way
    ///     out, so the flag has to survive materialization - it is what lets a reader tell a limiter
    ///     this process runs from a 429 the dependency sent.
    /// </summary>
    [Fact]
    public async Task A_materialized_attempt_reports_the_kind_and_the_origin_it_was_recorded_with()
    {
        var policy = Resilience.Default with
        {
            Attempts = 2,
            Backoff = Backoff.None,
            Classify = Classifier.Default.OnResult<int>(static _ => Verdict.Limited(TimeSpan.FromSeconds(30))),
        };

        var result = await policy.TryRunAsync(static _ => Task.FromResult(0));

        Assert.Equal(2, result.Attempts.Count);

        foreach (var attempt in result.Attempts)
        {
            Assert.Equal(VerdictKind.Throttled, attempt.Verdict.Kind);
            Assert.True(attempt.Verdict.SelfImposed);

            // Documented as not round-tripped: the pushback is observable as the DelayBefore of the
            // attempt that followed it, and the log does not store it twice.
            Assert.Null(attempt.Verdict.RetryAfter);
        }
    }

    [Fact]
    public void An_event_with_no_delay_and_no_reason_reports_neither()
    {
        var raised = CallEvent.Create(CallEventKind.Attempt);

        Assert.Null(raised.Delay);
        Assert.Null(raised.Reason);
        Assert.Equal(CallEventKind.Attempt, raised.Kind);
    }

    [Fact]
    public void An_events_delay_and_reason_round_trip()
    {
        var raised = CallEvent.Create(
            CallEventKind.RejectedByBudget,
            delay: TimeSpan.FromMilliseconds(250),
            reason: StopReason.BudgetExhausted,
            verdict: Verdict.Throttled(TimeSpan.FromSeconds(3)));

        Assert.Equal(CallEventKind.RejectedByBudget, raised.Kind);
        Assert.Equal(TimeSpan.FromMilliseconds(250), raised.Delay);
        Assert.Equal(StopReason.BudgetExhausted, raised.Reason);
        Assert.Equal(TimeSpan.FromSeconds(3), raised.Verdict.RetryAfter);
    }

    /// <summary>
    ///     <see cref="StopReason.Succeeded" /> is the zero of its enum, so a biased byte is what keeps it
    ///     distinguishable from "nothing has stopped yet".
    /// </summary>
    [Fact]
    public void The_first_stop_reason_is_not_mistaken_for_no_reason()
    {
        Assert.Equal(StopReason.Succeeded, CallEvent.Create(CallEventKind.Succeeded, reason: StopReason.Succeeded).Reason);
        Assert.Null(CallEvent.Create(CallEventKind.Succeeded).Reason);
    }

    /// <summary>
    ///     <see cref="CallEvent.Kind" /> is stored in a byte, so every member of the enum has to fit in
    ///     one. This is the assertion that fails when the twenty-first kind is added past 255.
    /// </summary>
    [Fact]
    public void Every_event_kind_fits_the_byte_it_is_stored_in()
    {
        foreach (var kind in Enum.GetValues<CallEventKind>())
        {
            Assert.InRange((int)kind, 0, byte.MaxValue);
            Assert.Equal(kind, CallEvent.Create(kind).Kind);
        }
    }

    /// <summary>
    ///     And every <see cref="StopReason" /> has to fit in a byte with one value to spare, because the
    ///     event stores it biased by one.
    /// </summary>
    [Fact]
    public void Every_stop_reason_fits_the_byte_it_is_stored_in()
    {
        foreach (var reason in Enum.GetValues<StopReason>())
        {
            Assert.InRange((int)reason, 0, byte.MaxValue - 1);
            Assert.Equal(reason, CallEvent.Create(CallEventKind.Succeeded, reason: reason).Reason);
        }
    }
}
