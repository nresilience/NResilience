using Microsoft.CodeAnalysis;

namespace NResilience.Analyzers.Tests;

/// <summary>NRES007: a state machine per attempt, for one await that did not need wrapping.</summary>
public sealed class RedundantAsyncTests
{
    [Fact]
    public void An_async_callback_that_awaits_one_call_does_not_need_to_be_async()
    {
        Diagnostic reported = Assert.Single(Harness.Run(Harness.InMethod(
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
}
