namespace NResilience.Analyzers.Tests;

/// <summary>NRES001 and NRES002: the callback's token has to reach the work.</summary>
public sealed class AttemptTokenTests
{
    [Fact]
    public void The_attempt_token_passed_to_the_call_is_the_whole_point()
    {
        var ids = Harness.Ids(Harness.InMethod(
            "        await api.RunAsync(attempt => Client.GetAsync(url, attempt), cancellationToken);"));

        Assert.Equal([], ids);
    }

    [Fact]
    public void The_callers_token_inside_the_callback_is_the_bug_this_exists_for()
    {
        var reported = Assert.Single(Harness.Run(Harness.InMethod(
            "        await api.RunAsync(attempt => Client.GetAsync(url, cancellationToken), cancellationToken);")));

        Assert.Equal("NRES002", reported.Id);
        Assert.Contains("'cancellationToken' is passed inside the callback", reported.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("attempt", reported.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void CancellationToken_None_is_the_same_bug_written_more_confidently()
    {
        Assert.Equal(["NRES002"], Harness.Ids(Harness.InMethod(
            "        await api.RunAsync(attempt => Client.GetAsync(url, CancellationToken.None), cancellationToken);")));
    }

    [Fact]
    public void A_discarded_parameter_is_still_the_attempts_token()
    {
        Assert.Equal(["NRES002"], Harness.Ids(Harness.InMethod(
            "        await api.RunAsync(_ => Client.GetAsync(url, cancellationToken), cancellationToken);")));
    }

    [Fact]
    public void An_omitted_optional_token_is_a_call_the_attempt_timeout_cannot_reach()
    {
        var reported = Assert.Single(Harness.Run(Harness.InMethod(
            "        await api.RunAsync(attempt => Helper(), cancellationToken);")));

        Assert.Equal("NRES001", reported.Id);
        Assert.Contains("'Helper' takes a cancellation token", reported.GetMessage(), StringComparison.Ordinal);
    }

/// <summary>
///     The <c>ValueTask</c> execution overloads are extension methods, not members of <c>Resilience</c>. 
///     A rule that only matches record methods would ignore these overloads, leaving the zero-allocation 
///     path without the guard this rule provides.
/// </summary>
    [Fact]
    public void A_ValueTask_callback_is_an_execution_overload_too()
    {
        var reported = Assert.Single(Harness.Run(Harness.InMethod(
            "        await api.RunAsync(attempt => Buffered(), cancellationToken);")));

        Assert.Equal("NRES001", reported.Id);
        Assert.Contains("'Buffered' takes a cancellation token", reported.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_wrong_token_in_a_ValueTask_callback_is_the_same_bug()
    {
        Assert.Equal(["NRES002"], Harness.Ids(Harness.InMethod(
            "        await api.RunAsync(attempt => Buffered(cancellationToken), cancellationToken);")));
    }

    [Fact]
    public void A_ValueTask_callback_that_passes_the_attempt_token_is_not_reported()
    {
        Assert.Equal([], Harness.Ids(Harness.InMethod(
            "        await api.RunAsync(attempt => Buffered(attempt), cancellationToken);")));
    }

    [Fact]
    public void The_non_throwing_ValueTask_overload_is_covered_as_well()
    {
        Assert.Equal(["NRES002"], Harness.Ids(Harness.InMethod(
            "        await api.TryRunAsync(attempt => Buffered(cancellationToken), cancellationToken);")));
    }

    [Fact]
    public void Work_that_takes_no_token_at_all_is_not_reported()
    {
        // Nothing to pass it to. A diagnostic here would fire on every synchronously-completing
        // callback in the ecosystem and teach people to turn the rule off.
        Assert.Equal([], Harness.Ids(Harness.InMethod(
            "        await api.RunAsync(attempt => Task.FromResult(1), cancellationToken);")));
    }

    [Fact]
    public void One_diagnostic_per_call_that_missed_it()
    {
        Assert.Equal(
            ["NRES001", "NRES001"],
            Harness.Ids(Harness.InMethod("""
                                                 await api.RunAsync(async attempt =>
                                                 {
                                                     await Helper();
                                                     await Numbered(2);
                                                 }, cancellationToken);
                                         """)));
    }

    [Fact]
    public void A_token_used_anywhere_is_taken_as_threaded()
    {
        // Conservative on purpose: a callback that passes the attempt's token to one call and
        // forgets a second is CA2016's business, and guessing which of the two was deliberate is
        // how an analyzer earns a NoWarn.
        Assert.Equal([], Harness.Ids(Harness.InMethod("""
                                                              await api.RunAsync(async attempt =>
                                                              {
                                                                  await Helper(attempt);
                                                                  await Numbered(2);
                                                              }, cancellationToken);
                                                      """)));
    }

    [Fact]
    public void The_state_overloads_carry_the_token_last()
    {
        Assert.Equal(["NRES002"], Harness.Ids(Harness.InMethod(
            "        await api.RunAsync(static (client, attempt) => client.GetAsync(\"/\", CancellationToken.None), Client, cancellationToken);")));
    }

    [Fact]
    public void The_state_overloads_are_clean_when_the_token_is_threaded()
    {
        Assert.Equal([], Harness.Ids(Harness.InMethod(
            "        await api.RunAsync(static (client, attempt) => client.GetAsync(\"/\", attempt), Client, cancellationToken);")));
    }

    [Fact]
    public void TryRunAsync_is_the_same_contract()
    {
        Assert.Equal(["NRES002"], Harness.Ids(Harness.InMethod(
            "        await api.TryRunAsync(attempt => Helper(cancellationToken), cancellationToken);")));
    }

    [Fact]
    public void A_method_group_is_left_alone()
    {
        // The body may be in another assembly. A rule that fires only when the source happens to be
        // visible is a rule that reports on some builds and not others.
        Assert.Equal([], Harness.Ids(Harness.InMethod(
            "        await api.RunAsync(Helper, cancellationToken);")));
    }

    [Fact]
    public void A_nested_call_that_threads_the_token_is_clean()
    {
        Assert.Equal([], Harness.Ids(Harness.InMethod("""
                                                              await api.RunAsync(
                                                                  attempt => api.RunAsync(inner => Helper(inner), attempt).AsTask(),
                                                                  cancellationToken);
                                                      """)));
    }

    /// <summary>
    ///     The streaming execution overloads name their callback parameter <c>source</c> rather than
    ///     <c>work</c>, because it is a cold source the policy re-invokes per attempt rather than the
    ///     work itself. These are the same rules over the new parameter name.
    /// </summary>
    [Fact]
    public void The_streaming_overloads_are_clean_when_the_token_is_threaded()
    {
        Assert.Equal([], Harness.Ids(Harness.InMethod(
            "        await foreach (var item in api.RunAsync(attempt => Listed(attempt), cancellationToken)) { }")));
    }

    [Fact]
    public void The_wrong_token_in_a_streaming_callback_is_the_same_bug()
    {
        var reported = Assert.Single(Harness.Run(Harness.InMethod(
            "        await foreach (var item in api.RunAsync(attempt => Listed(cancellationToken), cancellationToken)) { }")));

        Assert.Equal("NRES002", reported.Id);
        Assert.Contains("attempt", reported.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_omitted_token_in_a_streaming_callback_is_a_stream_the_attempt_ceiling_cannot_stop()
    {
        var reported = Assert.Single(Harness.Run(Harness.InMethod(
            "        await foreach (var item in api.RunAsync(attempt => Listed(), cancellationToken)) { }")));

        Assert.Equal("NRES001", reported.Id);
        Assert.Contains("'Listed' takes a cancellation token", reported.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_stateful_streaming_overload_is_clean_when_the_token_is_threaded()
    {
        // `Client` here is a stand-in for any state a caller threads to its source factory.
        Assert.Equal([], Harness.Ids(Harness.InMethod(
            "        await foreach (var item in api.RunAsync(static (client, attempt) => Target.Listed(attempt), Client, cancellationToken)) { }")));
    }

    [Fact]
    public void A_streaming_method_group_is_left_alone()
    {
        // A method group binds to the streaming overload without ever naming the token, and the
        // rule is deliberately quiet about bodies it cannot see - the same contract as the call
        // overloads.
        Assert.Equal([], Harness.Ids(Harness.InMethod(
            "        await foreach (var item in api.RunAsync(Listed, cancellationToken)) { }")));
    }
}
