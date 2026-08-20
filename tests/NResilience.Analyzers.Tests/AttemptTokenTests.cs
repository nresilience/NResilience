using Microsoft.CodeAnalysis;

namespace NResilience.Analyzers.Tests;

/// <summary>NRES001 and NRES002: the callback's token has to reach the work.</summary>
public sealed class AttemptTokenTests
{
    [Fact]
    public void The_attempt_token_passed_to_the_call_is_the_whole_point()
    {
        string[] ids = Harness.Ids(Harness.InMethod(
            "        await api.RunAsync(attempt => Client.GetAsync(url, attempt), cancellationToken);"));

        Assert.Equal([], ids);
    }

    [Fact]
    public void The_callers_token_inside_the_callback_is_the_bug_this_exists_for()
    {
        Diagnostic reported = Assert.Single(Harness.Run(Harness.InMethod(
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
        Diagnostic reported = Assert.Single(Harness.Run(Harness.InMethod(
            "        await api.RunAsync(attempt => Helper(), cancellationToken);")));

        Assert.Equal("NRES001", reported.Id);
        Assert.Contains("'Helper' takes a cancellation token", reported.GetMessage(), StringComparison.Ordinal);
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
}
