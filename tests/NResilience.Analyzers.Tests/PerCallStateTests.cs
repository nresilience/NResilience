using Microsoft.CodeAnalysis;

namespace NResilience.Analyzers.Tests;

/// <summary>
///     NRES005, NRES006 and NRES008: state that has to outlive the call - a breaker, a budget, a policy
///     scope, a client, and the latency estimate a policy instance carries.
/// </summary>
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
    public void A_policy_scope_built_per_call_is_a_dictionary_of_breakers_that_never_sees_a_second_call()
    {
        var reported = Assert.Single(Harness.Run(Harness.InFile("""
                                                                internal static class Tenants
                                                                {
                                                                    private static readonly Resilience Template = Resilience.Default with { Breaker = new Breaker() };

                                                                    internal static Resilience For(string tenant) => new PolicyScope<string>(Template).For(tenant);
                                                                }
                                                                """)));

        Assert.Equal("NRES005", reported.Id);
        Assert.Contains("policy scope", reported.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("'For'", reported.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_policy_scope_held_in_a_local_that_never_leaves_the_method_is_the_same_bug()
    {
        Assert.Equal(["NRES005"], Harness.Ids(Harness.InFile("""
                                                             internal static class Tenants
                                                             {
                                                                 internal static Resilience For(string tenant)
                                                                 {
                                                                     var scope = new PolicyScope<string>(Resilience.Default);
                                                                     return scope.For(tenant);
                                                                 }
                                                             }
                                                             """)));
    }

    [Fact]
    public void A_policy_scope_in_a_static_field_is_the_shape_the_docs_teach()
    {
        Assert.Equal([], Harness.Ids(Harness.InFile("""
                                                    internal static class Tenants
                                                    {
                                                        private static readonly PolicyScope<string> Scope = new(Resilience.Default with { Breaker = new Breaker() });

                                                        internal static Resilience For(string tenant) => Scope.For(tenant);
                                                    }
                                                    """)));
    }

    [Fact]
    public void A_policy_scope_the_method_hands_back_is_not_per_call()
    {
        Assert.Equal([], Harness.Ids(Harness.InFile("""
                                                    internal static class Tenants
                                                    {
                                                        internal static PolicyScope<string> Build() => new(Resilience.Default);

                                                        internal static PolicyScope<string> BuildLocal()
                                                        {
                                                            var scope = new PolicyScope<string>(Resilience.Default);
                                                            return scope;
                                                        }
                                                    }
                                                    """)));
    }

    [Fact]
    public void A_grpc_interceptor_built_per_call_is_the_same_bug_one_level_up()
    {
        var reported = Assert.Single(Harness.Run(Harness.InFile("""
                                                                internal static class Orders
                                                                {
                                                                    internal static IReadOnlyDictionary<string, Breaker> Breakers() =>
                                                                        new ResilienceInterceptor().Breakers();
                                                                }
                                                                """)));

        Assert.Equal("NRES005", reported.Id);
        Assert.Contains("gRPC resilience interceptor", reported.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("'Breakers'", reported.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_grpc_interceptor_held_in_a_local_that_never_leaves_the_method_is_reported()
    {
        Assert.Equal(["NRES005"], Harness.Ids(Harness.InFile("""
                                                             internal static class Orders
                                                             {
                                                                 internal static bool WillRetry(Grpc.Core.IMethod method)
                                                                 {
                                                                     var interceptor = new ResilienceInterceptor();
                                                                     return interceptor.WillRetry(method);
                                                                 }
                                                             }
                                                             """)));
    }

    [Fact]
    public void A_grpc_interceptor_in_a_static_field_is_the_shape_the_docs_teach()
    {
        Assert.Equal([], Harness.Ids(Harness.InFile("""
                                                    internal static class Orders
                                                    {
                                                        private static readonly ResilienceInterceptor Interceptor = new();

                                                        internal static bool WillRetry(Grpc.Core.IMethod method) => Interceptor.WillRetry(method);
                                                    }
                                                    """)));
    }

    [Fact]
    public void A_grpc_interceptor_handed_to_a_registration_is_not_followed_there()
    {
        // AddGrpcResilience() builds one per channel from a factory, and channel.Intercept(new ...)
        // hands it to something whose own lifetime is the question. Both are the same syntax as a
        // registration that keeps it forever, so neither is reported.
        Assert.Equal([], Harness.Ids(Harness.InFile("""
                                                    internal static class Orders
                                                    {
                                                        internal static Grpc.Core.CallInvoker Wrap(Grpc.Core.CallInvoker invoker) =>
                                                            Grpc.Core.Interceptors.CallInvokerExtensions.Intercept(invoker, new ResilienceInterceptor());
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

    // ---- NRES008: the latency estimate a policy instance carries ----

    /// <summary>
    ///     The estimate is keyed by the policy instance, so a policy built per call is permanently
    ///     cold - and both features that read one are documented to do nothing until they have samples.
    ///     A hedge that never fires is invisible: the call just behaves as if hedging were off.
    /// </summary>
    [Fact]
    public void A_hedging_policy_built_per_call_has_a_permanently_cold_estimate()
    {
        var reported = Assert.Single(Harness.Run(Harness.InFile("""
                                                                internal static class Dependencies
                                                                {
                                                                    internal static Resilience Search() => Resilience.Http with { Hedge = Hedge.At(0.95) };
                                                                }
                                                                """)));

        Assert.Equal("NRES008", reported.Id);
        Assert.Contains("'Hedge'", reported.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("'Search'", reported.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("MinimumSamples", reported.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     The same for a measured attempt ceiling, which fails the same way and just as quietly: the
    ///     attempt gets the configured AttemptTimeout, which is exactly what it would have got without
    ///     AttemptCeiling configured at all.
    /// </summary>
    [Fact]
    public void A_policy_with_a_measured_ceiling_built_per_call_is_reported()
    {
        var reported = Assert.Single(Harness.Run(Harness.InFile("""
                                                                internal static class Dependencies
                                                                {
                                                                    internal static Resilience Api() => Resilience.Http with { AttemptCeiling = AttemptCeiling.Above(3) };
                                                                }
                                                                """)));

        Assert.Equal("NRES008", reported.Id);
        Assert.Contains("'AttemptCeiling'", reported.GetMessage(), StringComparison.Ordinal);
    }

    /// <summary>The shape the docs teach, and the reason the rule excludes field initializers.</summary>
    [Fact]
    public void A_policy_held_in_a_static_field_keeps_its_estimate()
    {
        Assert.Equal([], Harness.Ids(Harness.InFile("""
                                                    internal static class Dependencies
                                                    {
                                                        internal static readonly Resilience Api =
                                                            Resilience.Http with { AttemptCeiling = AttemptCeiling.Above(3) };
                                                    }
                                                    """)));
    }

    /// <summary>
    ///     An expression-bodied property runs on every read, so it is a method for this rule's purposes -
    ///     the same distinction NRES005 draws.
    /// </summary>
    [Fact]
    public void An_expression_bodied_policy_property_is_reported()
    {
        Assert.Equal(["NRES008"], Harness.Ids(Harness.InFile("""
                                                             internal static class Dependencies
                                                             {
                                                                 internal static Resilience Api =>
                                                                     Resilience.Http with { Hedge = Hedge.At(0.95) };
                                                             }
                                                             """)));
    }

    /// <summary>
    ///     Setting the property to null removes the feature rather than configuring one. The HTTP
    ///     handler's own single-shot policy is written exactly this way, so reporting it would fire on
    ///     the library's own recommended shape.
    /// </summary>
    [Fact]
    public void Clearing_the_estimate_per_call_is_not_reported()
    {
        Assert.Equal([], Harness.Ids(Harness.InFile("""
                                                    internal static class Dependencies
                                                    {
                                                        internal static Resilience Single(Resilience policy) =>
                                                            policy with { Attempts = 1, Hedge = null };
                                                    }
                                                    """)));
    }

    /// <summary>
    ///     Narrowing a policy per call is a legitimate and documented thing to do - a per-request
    ///     deadline is the example - and this expression says nothing about whether the source carries an
    ///     estimator. Establishing that would mean following the referenced symbol, and a rule that is
    ///     merely usually right about a shape this common is a rule people turn off. Recorded as a test
    ///     so the limit is deliberate rather than an omission.
    /// </summary>
    [Fact]
    public void Narrowing_a_policy_per_call_without_naming_an_estimator_is_not_reported()
    {
        Assert.Equal([], Harness.Ids(Harness.InFile("""
                                                    internal static class Dependencies
                                                    {
                                                        internal static readonly Resilience Api =
                                                            Resilience.Http with { AttemptCeiling = AttemptCeiling.Above(3) };

                                                        internal static Resilience ForRequest(TimeSpan budget) =>
                                                            Api with { Deadline = budget, UseAmbientDeadline = true };
                                                    }
                                                    """)));
    }

    /// <summary>The object-initializer form reaches the same check, so neither syntax is a way around it.</summary>
    [Fact]
    public void The_object_initializer_form_is_reported_too()
    {
        Assert.Equal(["NRES008"], Harness.Ids(Harness.InFile("""
                                                             internal static class Dependencies
                                                             {
                                                                 internal static Resilience Api() =>
                                                                     new Resilience { Attempts = 2, AttemptCeiling = AttemptCeiling.Above(3) };
                                                             }
                                                             """)));
    }

    /// <summary>
    ///     One diagnostic per policy, not one per estimator. A policy configuring both is one mistake.
    /// </summary>
    [Fact]
    public void A_policy_configuring_both_estimators_is_reported_once()
    {
        Assert.Equal(["NRES008"], Harness.Ids(Harness.InFile("""
                                                             internal static class Dependencies
                                                             {
                                                                 internal static Resilience Api() => Resilience.Http with
                                                                 {
                                                                     Hedge = Hedge.At(0.95),
                                                                     AttemptCeiling = AttemptCeiling.Above(3),
                                                                 };
                                                             }
                                                             """)));
    }
}
