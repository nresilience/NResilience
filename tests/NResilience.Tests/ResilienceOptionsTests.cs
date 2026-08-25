using Microsoft.Extensions.Configuration;
using NResilience.Extensions;

namespace NResilience.Tests;

/// <summary>
///     The configuration DTO and its projection.
///     <para>
///         The design justifies this type with a measured claim: that
///         <c>Microsoft.Extensions.Configuration.Binder</c> does not set <c>init</c>-only properties, so
///         binding a section onto <see cref="Resilience" /> silently yields the record's defaults. Re-run
///         against the binder this package depends on, <b>that claim no longer holds</b> - simple
///         <c>init</c> scalars now bind. The conclusion survives the correction, and the first two tests
///         here are why: what binding onto the record does today is not "nothing", it is
///         <i>partial</i>, and partial without a word of complaint is worse.
///     </para>
/// </summary>
public sealed class ResilienceOptionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    // ---- The reason this type exists ----

    /// <summary>
    ///     The correction: <c>init</c>-only scalars <i>do</i> bind now. Kept as a test rather than a
    ///     footnote, because it is the premise the whole DTO rests on and the next binder version may
    ///     move it again - in either direction.
    /// </summary>
    [Fact]
    public void The_binder_now_does_set_init_only_scalars()
    {
        var policy = new Resilience();

        Config(("Attempts", "7"), ("Deadline", "00:00:10")).Bind(policy);

        Assert.Equal(7, policy.Attempts);
        Assert.Equal(TimeSpan.FromSeconds(10), policy.Deadline);
    }

    /// <summary>
    ///     And the reason the DTO is still the binding target: on everything that is not a scalar, the
    ///     binder does something silently wrong rather than nothing.
    ///     <list type="number">
    ///         <item>
    ///             <description>
    ///                 <c>Backoff:Max</c> is a computed property, so the cap is dropped - while <c>Backoff:Jitter</c>
    ///                 beside it in the same section is honored. A section that half-applies is the worst
    ///                 available outcome, because the half that worked is the evidence people use to conclude the
    ///                 other half did too.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <c>Classify</c> names a classifier and gets none, silently - so a policy configured
    ///                 <c>"Http"</c> keeps <see cref="Classifier.Default" />, which does not retry a 503.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <c>Breaker</c> is the dangerous one. The binder <b>constructs a live circuit breaker</b>
    ///                 because the type has a usable constructor, ignores the settings underneath it, and hands
    ///                 back a breaker with defaults that nobody asked for. Configuration should not be able to
    ///                 conjure a stateful guard by accident.
    ///             </description>
    ///         </item>
    ///     </list>
    /// </summary>
    [Fact]
    public void Binding_onto_the_record_is_silently_partial()
    {
        var backoff = new Resilience();
        Config(("Backoff:Max", "00:00:05"), ("Backoff:Jitter", "None")).Bind(backoff);

        Assert.Equal(Jitter.None, backoff.Backoff.Jitter);
        Assert.Equal(TimeSpan.FromSeconds(30), backoff.Backoff.Max);

        var classified = new Resilience();
        Config(("Classify", "Http")).Bind(classified);

        Assert.Same(Classifier.Default, classified.Classify);

        var broken = new Resilience();
        Config(("Breaker:ConsecutiveFailures", "2")).Bind(broken);

        Assert.NotNull(broken.Breaker);
        Assert.Equal(5, broken.Breaker.Settings.ConsecutiveFailures);
    }

    /// <summary>The DTO says all three of those things, and says them once.</summary>
    [Fact]
    public void The_dto_says_what_the_record_could_not()
    {
        var options = new ResilienceOptions();

        Config(
            ("Preset", "Http"),
            ("MaxDelay", "00:00:05"),
            ("Jitter", "None"),
            ("Breaker:ConsecutiveFailures", "2")).Bind(options);

        var policy = options.ToPolicy();

        Assert.Same(Classifier.Http, policy.Classify);
        Assert.Equal(TimeSpan.FromSeconds(5), policy.Backoff.Max);
        Assert.Equal(Jitter.None, policy.Backoff.Jitter);
        Assert.Equal(2, policy.Breaker!.Settings.ConsecutiveFailures);
    }

    /// <summary>And the DTO, being mutable, binds - which is the whole of the workaround.</summary>
    [Fact]
    public void The_dto_binds()
    {
        var options = new ResilienceOptions();

        Config(("Preset", "Http"), ("Attempts", "7"), ("Deadline", "00:00:10")).Bind(options);

        Assert.Equal("Http", options.Preset);
        Assert.Equal(7, options.Attempts);
        Assert.Equal(TimeSpan.FromSeconds(10), options.Deadline);
    }

    // ---- Projection ----

    /// <summary>Null means "say nothing", not "set the default". A section mentioning one field changes one field.</summary>
    [Fact]
    public void An_unset_property_leaves_the_baseline_alone()
    {
        var baseline = Resilience.Default with { Attempts = 9, Deadline = TimeSpan.FromMinutes(4) };

        var policy = new ResilienceOptions { Attempts = 2 }.ToPolicy(baseline);

        Assert.Equal(2, policy.Attempts);
        Assert.Equal(TimeSpan.FromMinutes(4), policy.Deadline);
    }

    /// <summary>The preset is a discriminator the binder has no concept of, so the projection resolves it explicitly.</summary>
    [Theory]
    [InlineData("Http")]
    [InlineData("http")]
    [InlineData("HTTP")]
    public void The_http_preset_resolves_case_insensitively(string preset)
    {
        var policy = new ResilienceOptions { Preset = preset }.ToPolicy();

        Assert.Same(Classifier.Http, policy.Classify);
    }

    /// <summary>A preset beats the baseline, because naming one is the more specific statement.</summary>
    [Fact]
    public void A_preset_replaces_the_baseline()
    {
        var policy = new ResilienceOptions { Preset = "None" }.ToPolicy(Resilience.Http with { Attempts = 9 });

        Assert.Equal(1, policy.Attempts);
    }

    /// <summary>A typo in a preset name fails loudly, at registration, naming the three that exist.</summary>
    [Fact]
    public void An_unknown_preset_throws()
    {
        var error = Assert.Throws<ResilienceConfigurationException>(() => new ResilienceOptions { Preset = "Htpp" }.ToPolicy());

        Assert.Contains("Htpp", error.Message, StringComparison.Ordinal);
        Assert.Contains("None, Default or Http", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Backoff is rebuilt rather than patched, because the shipped shape is a factory rather than a
    ///     bag of knobs - so a section that mentions only the cap keeps every other backoff default.
    /// </summary>
    [Fact]
    public void Backoff_settings_project_onto_an_exponential_backoff()
    {
        var policy = new ResilienceOptions
        {
            MaxDelay = TimeSpan.FromSeconds(5),
            Jitter = Jitter.None,
            TransientBaseDelay = TimeSpan.FromMilliseconds(50),
            BackoffFactor = 3,
        }.ToPolicy();

        Assert.Equal(TimeSpan.FromSeconds(5), policy.Backoff.Max);
        Assert.Equal(Jitter.None, policy.Backoff.Jitter);

        // 50ms, then ×3, with jitter off so the arithmetic is the assertion.
        Assert.Equal(TimeSpan.FromMilliseconds(50), Delay(policy, 2));
        Assert.Equal(TimeSpan.FromMilliseconds(150), Delay(policy, 3));
    }

    /// <summary>Jitter on its own is a modifier, not a reason to rebuild the curve.</summary>
    [Fact]
    public void Jitter_alone_leaves_the_rest_of_the_backoff_alone()
    {
        var baseline = Resilience.Default with { Backoff = Backoff.Constant(TimeSpan.FromSeconds(2)) };

        var policy = new ResilienceOptions { Jitter = Jitter.None }.ToPolicy(baseline);

        Assert.Equal(Jitter.None, policy.Backoff.Jitter);
        Assert.Equal(TimeSpan.FromSeconds(2), Delay(policy, 2));
    }

    /// <summary>
    ///     Zero is the off switch rather than a fraction to reject: it is the only obvious way to say
    ///     "no budget" in JSON, and making the obvious thing an error is how configuration files end up
    ///     with a superstition in them.
    /// </summary>
    [Fact]
    public void A_zero_budget_fraction_turns_the_budget_off()
    {
        var policy = new ResilienceOptions { BudgetFraction = 0 }.ToPolicy();

        Assert.Same(RetryBudget.None, policy.Budget);
    }

    /// <summary>A named budget is the shared one, which is the opt-in the design insists is an opt-in.</summary>
    [Fact]
    public void A_named_budget_is_shared_by_name()
    {
        var first = new ResilienceOptions { SharedBudget = "tier-1" }.ToPolicy();
        var second = new ResilienceOptions { SharedBudget = "tier-1" }.ToPolicy();
        var other = new ResilienceOptions { SharedBudget = "tier-2" }.ToPolicy();

        Assert.Same(first.Budget, second.Budget);
        Assert.NotSame(first.Budget, other.Budget);
    }

    /// <summary>An unmentioned budget leaves the base policy's marker in place.</summary>
    [Fact]
    public void An_unmentioned_budget_stays_automatic()
    {
        Assert.Same(RetryBudget.Automatic, new ResilienceOptions { Attempts = 3 }.ToPolicy().Budget);
    }

    /// <summary>A breaker in configuration is settings; the live object is built from them.</summary>
    [Fact]
    public void Breaker_settings_project_onto_a_live_breaker()
    {
        var policy = new ResilienceOptions
        {
            Name = "payments",
            Breaker = new BreakerOptions
            {
                ConsecutiveFailures = 2,
                BreakDuration = TimeSpan.FromSeconds(15),
            },
        }.ToPolicy();

        var breaker = Assert.IsType<Breaker>(policy.Breaker);
        Assert.Equal("payments", breaker.Name);
        Assert.Equal(2, breaker.Settings.ConsecutiveFailures);
        Assert.Equal(TimeSpan.FromSeconds(15), breaker.Settings.BreakDuration);
        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    /// <summary>
    ///     The whole DTO binds from one section, which is the claim the design makes about it: every
    ///     property is a primitive, a <see cref="TimeSpan" /> or an enum, and the platform's binder
    ///     handles all three.
    /// </summary>
    [Fact]
    public void A_realistic_section_binds_and_projects()
    {
        var options = new ResilienceOptions();

        Config(
                ("Preset", "Http"),
                ("Attempts", "5"),
                ("Deadline", "00:00:10"),
                ("AttemptTimeout", "00:00:03"),
                ("MaxDelay", "00:00:04"),
                ("Jitter", "Equal"),
                ("BudgetFraction", "0.25"),
                ("BudgetMinimumPerSecond", "10"),
                ("Breaker:ConsecutiveFailures", "8"),
                ("Breaker:SlowCallThreshold", "00:00:02"))
            .Bind(options);

        var policy = options.ToPolicy();

        Assert.Equal(5, policy.Attempts);
        Assert.Equal(TimeSpan.FromSeconds(10), policy.Deadline);
        Assert.Equal(TimeSpan.FromSeconds(3), policy.AttemptTimeout);
        Assert.Equal(TimeSpan.FromSeconds(4), policy.Backoff.Max);
        Assert.Equal(Jitter.Equal, policy.Backoff.Jitter);
        Assert.Same(Classifier.Http, policy.Classify);
        Assert.NotNull(policy.Budget);
        Assert.Equal(8, policy.Breaker!.Settings.ConsecutiveFailures);
        Assert.Equal(TimeSpan.FromSeconds(2), policy.Breaker.Settings.SlowCallThreshold);

        // The projection is not validation's substitute, but a projected policy must be executable.
        policy.Validate();
    }

    private static TimeSpan Delay(Resilience policy, int attemptNumber) =>
        policy.Backoff.Compute(new NextAttempt(attemptNumber, Verdict.Transient, null, Timeout.InfiniteTimeSpan, default));
}
