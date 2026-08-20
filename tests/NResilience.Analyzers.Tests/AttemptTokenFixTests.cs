namespace NResilience.Analyzers.Tests;

/// <summary>The fix for NRES001 and NRES002, applied to the shapes people actually write.</summary>
public sealed class AttemptTokenFixTests
{
    [Fact]
    public void The_wrong_token_is_replaced_with_the_attempts()
    {
        string fixedSource = Harness.ApplyFix(Harness.InMethod(
            "        await api.RunAsync(attempt => Client.GetAsync(url, cancellationToken), cancellationToken);"));

        Assert.Contains(
            "await api.RunAsync(attempt => Client.GetAsync(url, attempt), cancellationToken);",
            fixedSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_discarded_parameter_is_named_before_it_is_passed()
    {
        string fixedSource = Harness.ApplyFix(Harness.InMethod(
            "        await api.RunAsync(_ => Client.GetAsync(url, cancellationToken), cancellationToken);"));

        Assert.Contains(
            "await api.RunAsync(attempt => Client.GetAsync(url, attempt), cancellationToken);",
            fixedSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_omitted_last_parameter_is_appended_without_a_name()
    {
        string fixedSource = Harness.ApplyFix(Harness.InMethod(
            "        await api.RunAsync(attempt => Numbered(2), cancellationToken);"));

        Assert.Contains("Numbered(2, attempt)", fixedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void A_skipped_optional_in_the_middle_forces_a_named_argument()
    {
        string fixedSource = Harness.ApplyFix(Harness.InMethod(
            "        await api.RunAsync(attempt => Optional(1), cancellationToken);"));

        Assert.Contains("Optional(1, cancellationToken: attempt)", fixedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_already_in_use_is_not_shadowed()
    {
        string fixedSource = Harness.ApplyFix(Harness.InFile("""
            internal sealed class Target
            {
                internal async Task Run(CancellationToken cancellationToken)
                {
                    int attempt = 1;
                    var api = Resilience.Http;
                    await api.RunAsync(_ => Numbered(attempt, cancellationToken), cancellationToken);
                }

                internal static Task<int> Numbered(int value, CancellationToken cancellationToken = default) => Task.FromResult(value);
            }
            """));

        Assert.Contains("Numbered(attempt, attemptToken)", fixedSource, StringComparison.Ordinal);
        Assert.Contains("await api.RunAsync(attemptToken =>", fixedSource, StringComparison.Ordinal);
    }
}
