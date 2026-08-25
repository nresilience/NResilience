namespace NResilience.Tests;

/// <summary>Tests for the policy value: including presets, derivation, equality, and validation.</summary>
public sealed class PolicyTests
{
    [Fact]
    public void The_defaults_are_the_ones_the_design_declares()
    {
        var policy = Resilience.Default;

        Assert.Equal(3, policy.Attempts);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.Deadline);
        Assert.Equal(TimeSpan.FromSeconds(10), policy.AttemptTimeout);
        Assert.Same(Classifier.Default, policy.Classify);
        Assert.Same(TimeProvider.System, policy.Time);
        Assert.Null(policy.BeforeAttempt);
    }

    [Fact]
    public void None_turns_every_bound_off()
    {
        var policy = Resilience.None;

        Assert.Equal(1, policy.Attempts);
        Assert.Equal(Timeout.InfiniteTimeSpan, policy.Deadline);
        Assert.Equal(Timeout.InfiniteTimeSpan, policy.AttemptTimeout);
    }

    [Fact]
    public void Http_is_the_default_with_the_http_classifier()
    {
        Assert.Same(Classifier.Http, Resilience.Http.Classify);
        Assert.Equal(Resilience.Default.Attempts, Resilience.Http.Attempts);
    }

    [Fact]
    public void Deriving_with_with_leaves_the_original_alone()
    {
        var derived = Resilience.Http with { Attempts = 5, Deadline = TimeSpan.FromSeconds(20) };

        Assert.Equal(5, derived.Attempts);
        Assert.Equal(3, Resilience.Http.Attempts);
        Assert.Same(Classifier.Http, derived.Classify);
    }

    [Fact]
    public async Task Executing_a_policy_does_not_disturb_its_equality()
    {
        var a = Resilience.Default with { Name = "a" };
        var b = Resilience.Default with { Name = "a" };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        await a.RunAsync(ct => Task.FromResult(1));

        // Validation state lives outside the record, so having run cannot change what the record
        // compares as. A private flag field would have broken this.
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Validate_reports_every_problem_at_once()
    {
        var policy = Resilience.Default with
        {
            Attempts = 0,
            Deadline = TimeSpan.Zero,
            AttemptTimeout = TimeSpan.FromSeconds(-5),
        };

        var caught = Assert.Throws<ResilienceConfigurationException>(policy.Validate);

        Assert.Equal(3, caught.Problems.Count);
        Assert.Contains(caught.Problems, p => p.Contains("Attempts", StringComparison.Ordinal));
        Assert.Contains(caught.Problems, p => p.Contains("Deadline", StringComparison.Ordinal));
        Assert.Contains(caught.Problems, p => p.Contains("AttemptTimeout", StringComparison.Ordinal));
    }

    [Fact]
    public void Infinite_is_the_explicit_no_bound_value_rather_than_an_error()
    {
        Resilience.None.Validate();
        Resilience.Default.Validate();
        Resilience.Http.Validate();
    }

    /// <summary>
    ///     Validated() moves the throw from the first execution to where the policy is written, which
    ///     is what a static readonly field needs - the parentheses around the `with` are required.
    /// </summary>
    [Fact]
    public void Validated_returns_the_policy_it_checked()
    {
        var policy = (Resilience.Http with { Deadline = TimeSpan.FromSeconds(3) }).Validated();

        Assert.Equal(TimeSpan.FromSeconds(3), policy.Deadline);
    }

    [Fact]
    public void Validated_throws_where_the_policy_is_written()
    {
        var error = Assert.Throws<ResilienceConfigurationException>(() => (Resilience.Default with { Attempts = 0 }).Validated());

        Assert.Contains(error.Problems, p => p.Contains("Attempts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_invalid_policy_fails_on_first_execution()
    {
        var policy = Resilience.Default with { Attempts = 0 };

        await Assert.ThrowsAsync<ResilienceConfigurationException>(async () =>
            await policy.RunAsync(ct => Task.FromResult(1)));
    }

    [Fact]
    public async Task A_null_callback_is_rejected_before_anything_happens()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await Resilience.Default.RunAsync((Func<CancellationToken, Task<int>>)null!));
    }

    [Fact]
    public void A_policy_can_be_a_dictionary_key()
    {
        var byPolicy = new Dictionary<Resilience, string>
        {
            [Resilience.Default with { Name = "one" }] = "one",
        };

        Assert.Equal("one", byPolicy[Resilience.Default with { Name = "one" }]);
    }
}
