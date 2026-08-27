using Microsoft.CodeAnalysis;

namespace NResilience.Analyzers.Tests;

/// <summary>NRES007: a state machine per attempt, for one await that did not need wrapping.</summary>
public sealed class RedundantAsyncTests
{
    [Fact]
    public void An_async_callback_that_awaits_one_call_does_not_need_to_be_async()
    {
        var reported = Assert.Single(Harness.Run(Harness.InMethod(
            "        await api.RunAsync(async attempt => await Client.GetAsync(url, attempt), cancellationToken);")));

        Assert.Equal("NRES007", reported.Id);
        Assert.Equal(DiagnosticSeverity.Info, reported.Severity);
    }

    [Fact]
    public void Returning_the_task_directly_is_the_shape_the_overloads_were_built_for()
    {
        Assert.Equal([], Harness.Ids(Harness.InMethod(
            "        await api.RunAsync(attempt => Client.GetAsync(url, attempt), cancellationToken);")));
    }

    [Fact]
    public void A_void_callback_is_the_same_rewrite()
    {
        Assert.Equal(["NRES007"], Harness.Ids(Harness.InMethod("""
                                                                       await api.RunAsync(async attempt =>
                                                                       {
                                                                           await Helper(attempt);
                                                                       }, cancellationToken);
                                                               """)));
    }

    [Fact]
    public void More_than_one_statement_may_well_need_the_machine()
    {
        Assert.Equal([], Harness.Ids(Harness.InMethod("""
                                                              await api.RunAsync(async attempt =>
                                                              {
                                                                  int first = await Helper(attempt);
                                                                  return first + await Numbered(first, attempt);
                                                              }, cancellationToken);
                                                      """)));
    }

    [Fact]
    public void A_configured_awaiter_cannot_be_dropped_because_its_type_is_not_the_task()
    {
        Assert.Equal([], Harness.Ids(Harness.InMethod(
            "        await api.RunAsync(async attempt => await Client.GetAsync(url, attempt).ConfigureAwait(false), cancellationToken);")));
    }

    /// <summary>
    ///     Awaiting a <c>ValueTask</c> only to hand back a <c>Task</c> is the shape with the most to
    ///     gain: dropping <c>async</c> re-binds the call to the <c>ValueTask</c> overload, which saves
    ///     the state machine and the task built for a result the callback already had.
    /// </summary>
    [Fact]
    public void Awaiting_a_ValueTask_is_the_rewrite_with_the_most_to_save()
    {
        var reported = Assert.Single(Harness.Run(Harness.InMethod(
            "        await api.RunAsync(async attempt => await Buffered(attempt), cancellationToken);")));

        Assert.Equal("NRES007", reported.Id);
    }

    [Fact]
    public void The_rewritten_ValueTask_callback_is_clean()
    {
        Assert.Equal([], Harness.Ids(Harness.InMethod(
            "        await api.RunAsync(attempt => Buffered(attempt), cancellationToken);")));
    }

    [Fact]
    public void A_void_ValueTask_callback_is_the_same_rewrite()
    {
        Assert.Equal(["NRES007"], Harness.Ids(Harness.InMethod("""
                                                                       await api.RunAsync(async attempt =>
                                                                       {
                                                                           await Drain(attempt);
                                                                       }, cancellationToken);
                                                               """)));
    }

    [Fact]
    public void The_rewritten_void_ValueTask_callback_is_clean()
    {
        Assert.Equal([], Harness.Ids(Harness.InMethod(
            "        await api.RunAsync(attempt => Drain(attempt), cancellationToken);")));
    }

    /// <summary>
    ///     A callback that returns nothing while awaiting a <c>ValueTask&lt;T&gt;</c> is left alone.
    ///     The rewrite compiles, but it moves the call from the void overload to the generic one, and
    ///     the result the caller discarded would start reaching the classifier.
    /// </summary>
    [Fact]
    public void Discarding_a_ValueTask_result_is_not_offered_as_a_rewrite()
    {
        Assert.Equal([], Harness.Ids(Harness.InMethod("""
                                                              await api.RunAsync(async attempt =>
                                                              {
                                                                  await Buffered(attempt);
                                                              }, cancellationToken);
                                                      """)));
    }

    /// <summary>A lambda already on the ValueTask overload, written out. Identity, so it still applies.</summary>
    [Fact]
    public void An_explicit_ValueTask_return_type_that_matches_is_still_redundant()
    {
        Assert.Equal(["NRES007"], Harness.Ids(Harness.InMethod(
            "        await api.RunAsync(async ValueTask<int> (attempt) => await Buffered(attempt), cancellationToken);")));
    }

    /// <summary>
    ///     A written return type pins the rewritten body to that type rather than resolving again, so a
    ///     <c>Task</c> awaited inside a <c>ValueTask</c>-returning lambda cannot lose its <c>async</c>.
    /// </summary>
    [Fact]
    public void An_explicit_return_type_the_awaited_task_does_not_match_keeps_its_machine()
    {
        Assert.Equal([], Harness.Ids(Harness.InMethod(
            "        await api.RunAsync(async ValueTask<int> (attempt) => await Helper(attempt), cancellationToken);")));
    }

    /// <summary>
    ///     The same rebind, blocked by a written <c>Task</c> return type. Dropping <c>async</c> here
    ///     would leave a <c>ValueTask&lt;int&gt;</c> body under a <c>Task&lt;int&gt;</c> signature, which
    ///     does not compile - the return type is written down, so there is no resolution to re-run.
    /// </summary>
    [Fact]
    public void A_written_Task_return_type_blocks_the_ValueTask_rebind()
    {
        Assert.Equal([], Harness.Ids(Harness.InMethod(
            "        await api.RunAsync(async Task<int> (attempt) => await Buffered(attempt), cancellationToken);")));
    }
}
