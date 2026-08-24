using Microsoft.CodeAnalysis;

namespace NResilience.Analyzers.Tests;

/// <summary>NRES005 and NRES006: state that has to outlive the call.</summary>
public sealed class PerCallStateTests
{
    [Fact]
    public void A_breaker_built_inside_a_method_has_never_seen_a_failure()
    {
        var reported = Assert.Single(Harness.Run(Harness.InFile("""
                                                                internal static class Dependencies
                                                                {
                                                                    internal static Resilience Payments() => Resilience.Http with { Breaker = new Breaker() };
                                                                }
                                                                """)));

        Assert.Equal("NRES005", reported.Id);
        Assert.Contains("breaker", reported.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("'Payments'", reported.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("can never open", reported.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_breaker_held_in_a_static_field_is_the_shape_the_docs_teach()
    {
        Assert.Equal([], Harness.Ids(Harness.InFile("""
                                                    internal static class Dependencies
                                                    {
                                                        internal static readonly Resilience Payments = Resilience.Http with { Breaker = new Breaker() };
                                                    }
                                                    """)));
    }

    [Fact]
    public void An_expression_bodied_property_is_a_method_that_runs_every_time()
    {
        Assert.Equal(["NRES005"], Harness.Ids(Harness.InFile("""
                                                             internal static class Dependencies
                                                             {
                                                                 internal static Resilience Payments => Resilience.Http with { Breaker = new Breaker() };
                                                             }
                                                             """)));
    }

    [Fact]
    public void A_get_only_property_with_an_initializer_runs_once()
    {
        Assert.Equal([], Harness.Ids(Harness.InFile("""
                                                    internal sealed class Dependencies
                                                    {
                                                        internal Resilience Payments { get; } = Resilience.Http with { Breaker = new Breaker() };
                                                    }
                                                    """)));
    }

    [Fact]
    public void A_fresh_budget_per_call_has_never_seen_a_deposit()
    {
        var reported = Assert.Single(Harness.Run(Harness.InFile("""
                                                                internal static class Dependencies
                                                                {
                                                                    internal static Resilience Api() => Resilience.Http with { Budget = RetryBudget.Of() };
                                                                }
                                                                """)));

        Assert.Equal("NRES005", reported.Id);
        Assert.Contains("retry budget", reported.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("can never refill", reported.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_shared_budget_is_looked_up_by_name_so_asking_per_call_is_correct()
    {
        Assert.Equal([], Harness.Ids(Harness.InFile("""
                                                    internal static class Dependencies
                                                    {
                                                        internal static Resilience Api() => Resilience.Http with { Budget = RetryBudget.Shared("api") };
                                                    }
                                                    """)));
    }

    [Fact]
    public void A_breaker_that_is_a_local_first_may_be_going_somewhere_that_keeps_it()
    {
        Assert.Equal([], Harness.Ids(Harness.InFile("""
                                                    internal static class Dependencies
                                                    {
                                                        internal static Resilience Api(Breaker breaker) => Resilience.Http with { Breaker = breaker };
                                                    }
                                                    """)));
    }

    [Fact]
    public void A_client_created_and_disposed_inside_a_method_keeps_no_per_host_state()
    {
        var reported = Assert.Single(Harness.Run(Harness.InFile("""
                                                                internal static class Reader
                                                                {
                                                                    internal static async Task<string> ReadAsync(Uri url, CancellationToken cancellationToken)
                                                                    {
                                                                        using HttpClient client = ResilienceHttp.CreateClient();
                                                                        return await client.GetStringAsync(url, cancellationToken);
                                                                    }
                                                                }
                                                                """)));

        Assert.Equal("NRES006", reported.Id);
        Assert.Equal(DiagnosticSeverity.Info, reported.Severity);
        Assert.Contains("'ReadAsync'", reported.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_older_using_statement_is_the_same_lifetime()
    {
        Assert.Equal(["NRES006"], Harness.Ids(Harness.InFile("""
                                                             internal static class Reader
                                                             {
                                                                 internal static async Task<string> ReadAsync(Uri url, CancellationToken cancellationToken)
                                                                 {
                                                                     using (HttpClient client = ResilienceHttp.CreateClient())
                                                                     {
                                                                         return await client.GetStringAsync(url, cancellationToken);
                                                                     }
                                                                 }
                                                             }
                                                             """)));
    }

    [Fact]
    public void A_client_made_inside_someone_elses_using_block_is_not_the_subject()
    {
        Assert.Equal([], Harness.Ids(Harness.InFile("""
                                                    internal static class Reader
                                                    {
                                                        internal static HttpClient Build(CancellationTokenSource source)
                                                        {
                                                            using (source)
                                                            {
                                                                return ResilienceHttp.CreateClient();
                                                            }
                                                        }
                                                    }
                                                    """)));
    }

    [Fact]
    public void A_client_the_method_hands_back_is_not_per_call()
    {
        Assert.Equal([], Harness.Ids(Harness.InFile("""
                                                    internal static class Clients
                                                    {
                                                        internal static HttpClient Build() => ResilienceHttp.CreateClient();
                                                    }
                                                    """)));
    }

    [Fact]
    public void Startup_is_allowed_to_do_once_what_a_called_method_must_not_do_per_call()
    {
        Assert.Equal(
            [],
            Harness.Ids(
                Harness.InFile("""
                               internal static class Program
                               {
                                   internal static async Task Main()
                                   {
                                       using HttpClient client = ResilienceHttp.CreateClient();
                                       await client.GetStringAsync(new Uri("https://example.com"), CancellationToken.None);
                                   }
                               }
                               """),
                OutputKind.ConsoleApplication));
    }
}
