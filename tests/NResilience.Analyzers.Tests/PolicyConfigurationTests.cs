namespace NResilience.Analyzers.Tests;

/// <summary>NRES003 and NRES004: what the policy would say on first execution, said at build time.</summary>
public sealed class PolicyConfigurationTests
{
    [Fact]
    public void Fewer_than_one_attempt_is_not_a_policy()
    {
        var reported = Assert.Single(Harness.Run(Harness.InFile(
            "internal static class Policies { internal static readonly Resilience Api = Resilience.Http with { Attempts = 0 }; }")));

        Assert.Equal("NRES003", reported.Id);
        Assert.Equal("Attempts must be at least 1; it is 0", reported.GetMessage());
    }

    [Fact]
    public void A_zero_deadline_is_the_mistake_that_Timeout_InfiniteTimeSpan_exists_to_avoid()
    {
        var reported = Assert.Single(Harness.Run(Harness.InFile(
            "internal static class Policies { internal static readonly Resilience Api = Resilience.Http with { Deadline = TimeSpan.Zero }; }")));

        Assert.Equal("NRES003", reported.Id);
        Assert.Contains("Deadline must be positive, or Timeout.InfiniteTimeSpan", reported.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_negative_attempt_timeout_is_folded_out_of_the_expression()
    {
        Assert.Equal(["NRES003"], Harness.Ids(Harness.InFile(
            "internal static class Policies { internal static readonly Resilience Api = Resilience.Http with { AttemptTimeout = TimeSpan.FromSeconds(-1) }; }")));
    }

    [Fact]
    public void Timeout_InfiniteTimeSpan_is_the_way_to_say_no_bound()
    {
        Assert.Equal([], Harness.Ids(Harness.InFile(
            "internal static class Policies { internal static readonly Resilience Api = Resilience.Http with { Deadline = Timeout.InfiniteTimeSpan, AttemptTimeout = Timeout.InfiniteTimeSpan }; }")));
    }

    [Fact]
    public void The_object_initializer_is_the_same_surface_as_with()
    {
        Assert.Equal(["NRES003"], Harness.Ids(Harness.InFile(
            "internal static class Policies { internal static readonly Resilience Api = new Resilience { Attempts = -2 }; }")));
    }

    [Fact]
    public void An_attempt_timeout_above_the_deadline_can_never_be_reached()
    {
        var reported = Assert.Single(Harness.Run(Harness.InFile("""
                                                                internal static class Policies
                                                                {
                                                                    internal static readonly Resilience Api = Resilience.Http with
                                                                    {
                                                                        Deadline = TimeSpan.FromSeconds(5),
                                                                        AttemptTimeout = TimeSpan.FromSeconds(10),
                                                                    };
                                                                }
                                                                """)));

        Assert.Equal("NRES004", reported.Id);
        Assert.Contains("0:00:10", reported.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("0:00:05", reported.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_two_bounds_in_the_order_that_works_are_clean()
    {
        Assert.Equal([], Harness.Ids(Harness.InFile("""
                                                    internal static class Policies
                                                    {
                                                        internal static readonly Resilience Api = Resilience.Http with
                                                        {
                                                            Deadline = TimeSpan.FromSeconds(10),
                                                            AttemptTimeout = TimeSpan.FromSeconds(3),
                                                        };
                                                    }
                                                    """)));
    }

    [Fact]
    public void An_unbounded_attempt_inside_a_deadline_is_the_documented_shape()
    {
        Assert.Equal([], Harness.Ids(Harness.InFile("""
                                                    internal static class Policies
                                                    {
                                                        internal static readonly Resilience Api = Resilience.Http with
                                                        {
                                                            Deadline = TimeSpan.FromSeconds(10),
                                                            AttemptTimeout = Timeout.InfiniteTimeSpan,
                                                        };
                                                    }
                                                    """)));
    }

    [Fact]
    public void A_value_the_compiler_cannot_see_is_left_to_Validate()
    {
        Assert.Equal([], Harness.Ids(Harness.InFile("""
                                                    internal static class Policies
                                                    {
                                                        internal static Resilience Of(int attempts, TimeSpan deadline) =>
                                                            Resilience.Http with { Attempts = attempts, Deadline = deadline };
                                                    }
                                                    """)));
    }

    [Fact]
    public void The_constructed_TimeSpan_is_folded_too()
    {
        Assert.Equal(["NRES003"], Harness.Ids(Harness.InFile(
            "internal static class Policies { internal static readonly Resilience Api = Resilience.Http with { Deadline = new TimeSpan(0, 0, 0) }; }")));
    }
}
