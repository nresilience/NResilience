using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
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
    ///     A section that mentions only some of the backoff knobs patches the base policy's curve, so
    ///     anything it did not mention keeps the value the base policy carried.
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

    /// <summary>
    ///     A section setting one knob on top of a base policy with a non-default exponential curve
    ///     keeps the base policy's other delays, rather than silently substituting factory defaults.
    /// </summary>
    [Fact]
    public void A_single_backoff_knob_patches_the_base_policy_rather_than_replacing_it()
    {
        var baseline = Resilience.Default with
        {
            Backoff = Backoff.Exponential(
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromSeconds(4),
                3.0,
                TimeSpan.FromSeconds(60)) with { Jitter = Jitter.None },
        };

        var policy = new ResilienceOptions { MaxDelay = TimeSpan.FromSeconds(5) }.ToPolicy(baseline);

        Assert.Equal(TimeSpan.FromSeconds(5), policy.Backoff.Max);
        Assert.Equal(TimeSpan.FromMilliseconds(500), policy.Backoff.TransientBase);
        Assert.Equal(TimeSpan.FromSeconds(4), policy.Backoff.ThrottledBase);
        Assert.Equal(3.0, policy.Backoff.Factor);
        Assert.Equal(Jitter.None, policy.Backoff.Jitter);
    }

    /// <summary>
    ///     Patching only makes sense against an exponential curve, so a Constant or Custom base policy
    ///     whose section sets a knob gets a fresh exponential built on the shipped defaults.
    /// </summary>
    [Fact]
    public void A_backoff_knob_over_a_non_exponential_base_policy_starts_from_the_defaults()
    {
        var baseline = Resilience.Default with { Backoff = Backoff.Constant(TimeSpan.FromSeconds(2)) };

        var policy = new ResilienceOptions { MaxDelay = TimeSpan.FromSeconds(5), Jitter = Jitter.None }.ToPolicy(baseline);

        Assert.Equal(BackoffKind.Exponential, policy.Backoff.Kind);
        Assert.Equal(TimeSpan.FromSeconds(5), policy.Backoff.Max);
        Assert.Equal(Backoff.Default.TransientBase, policy.Backoff.TransientBase);
        Assert.Equal(Backoff.Default.ThrottledBase, policy.Backoff.ThrottledBase);
        Assert.Equal(Backoff.Default.Factor, policy.Backoff.Factor);
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
    ///     The presence of the section is what turns hedging on, and an empty one is a complete
    ///     configuration - so the shortest way to hedge from JSON is <c>"Hedge": {}</c>.
    /// </summary>
    [Fact]
    public void An_empty_hedge_section_is_a_complete_configuration()
    {
        var options = new ResilienceOptions();
        Config(("Hedge:MinimumSamples", "50")).Bind(options);

        var hedge = Assert.NotNull(options.ToPolicy().Hedge);

        Assert.Equal(0.95, hedge.Quantile);
        Assert.Equal(50, hedge.MinimumSamples);
        Assert.Equal(2, hedge.MaxConcurrent);
    }

    [Fact]
    public void A_policy_with_no_hedge_section_does_not_hedge() =>
        Assert.Null(new ResilienceOptions { Attempts = 3 }.ToPolicy().Hedge);

    /// <summary>
    ///     The same arrangement for the adaptive attempt ceiling: the presence of the section arms it
    ///     and every default is usable, so <c>"Timeouts": {}</c> is a complete configuration.
    /// </summary>
    [Fact]
    public void An_empty_timeouts_section_is_a_complete_configuration()
    {
        var options = new ResilienceOptions();
        Config(("Timeouts:Quantile", "0.9")).Bind(options);

        var timeouts = Assert.NotNull(options.ToPolicy().Timeouts);

        Assert.Equal(3, timeouts.Multiple);
        Assert.Equal(0.9, timeouts.Quantile);
        Assert.Equal(TimeSpan.FromMinutes(5), timeouts.Window);
        Assert.Equal(20, timeouts.MinimumSamples);
        Assert.Equal(TimeSpan.FromMilliseconds(50), timeouts.Floor);
    }

    [Fact]
    public void Every_attempt_timeout_setting_projects()
    {
        var options = new ResilienceOptions();

        Config(
            ("Timeouts:Multiple", "4"),
            ("Timeouts:Quantile", "0.99"),
            ("Timeouts:Window", "00:10:00"),
            ("Timeouts:MinimumSamples", "75"),
            ("Timeouts:Floor", "00:00:00.020")).Bind(options);

        var timeouts = Assert.NotNull(options.ToPolicy().Timeouts);

        Assert.Equal(4, timeouts.Multiple);
        Assert.Equal(0.99, timeouts.Quantile);
        Assert.Equal(TimeSpan.FromMinutes(10), timeouts.Window);
        Assert.Equal(75, timeouts.MinimumSamples);
        Assert.Equal(TimeSpan.FromMilliseconds(20), timeouts.Floor);
    }

    /// <summary>
    ///     A section is not what arms the measured ceiling any more - the policy has one by default -
    ///     so a section that named nothing is indistinguishable from no section at all.
    /// </summary>
    [Fact]
    public void A_policy_with_no_timeouts_section_still_has_the_default_measured_ceiling()
    {
        Assert.Equal(AttemptTimeouts.Above(3), new ResilienceOptions { Attempts = 3 }.ToPolicy().Timeouts);
        Assert.Equal(AttemptTimeouts.Above(3), new ResilienceOptions { Timeouts = new AttemptTimeoutsOptions() }.ToPolicy().Timeouts);
    }

    /// <summary>
    ///     A configuration section cannot say <c>null</c>, so a zero multiple is the off switch - the
    ///     same shape <c>BudgetFraction: 0</c> uses.
    /// </summary>
    [Fact]
    public void A_zero_multiple_turns_the_measured_ceiling_off()
    {
        var options = new ResilienceOptions();

        Config(("Timeouts:Multiple", "0")).Bind(options);

        Assert.Null(options.ToPolicy().Timeouts);
    }

    /// <summary>
    ///     The same arrangement for the adaptive slow-call trip, and for the same reason: the presence
    ///     of the section arms it, and every default is usable, so <c>"SlowCalls": {}</c> is a complete
    ///     brownout trip that needs nobody to have guessed a millisecond figure.
    /// </summary>
    [Fact]
    public void An_empty_slow_calls_section_is_a_complete_brownout_trip()
    {
        var options = new ResilienceOptions();
        Config(("Breaker:SlowCalls:Multiple", "5")).Bind(options);

        var slow = Assert.NotNull(options.ToPolicy().Breaker!.Settings.SlowCalls);

        Assert.Equal(5, slow.Multiple);
        Assert.Equal(0.5, slow.Quantile);
        Assert.Equal(TimeSpan.FromMinutes(5), slow.Window);
        Assert.Equal(20, slow.MinimumSamples);
    }

    [Fact]
    public void Every_slow_call_setting_projects()
    {
        var policy = new ResilienceOptions
        {
            Breaker = new BreakerOptions
            {
                SlowCalls = new SlowCallOptions
                {
                    Multiple = 4,
                    Quantile = 0.25,
                    Window = TimeSpan.FromMinutes(10),
                    MinimumSamples = 100,
                },
            },
        }.ToPolicy();

        var slow = Assert.NotNull(policy.Breaker!.Settings.SlowCalls);

        Assert.Equal(4, slow.Multiple);
        Assert.Equal(0.25, slow.Quantile);
        Assert.Equal(TimeSpan.FromMinutes(10), slow.Window);
        Assert.Equal(100, slow.MinimumSamples);
    }

    /// <summary>
    ///     And the same arrangement again for the relative failure trip, which is the third feature to
    ///     take this shape: a section whose presence arms it, and whose every property defaults.
    /// </summary>
    [Fact]
    public void An_empty_failures_section_is_a_complete_relative_trip()
    {
        var options = new ResilienceOptions();
        Config(("Breaker:Failures:Multiple", "4")).Bind(options);

        var failures = Assert.NotNull(options.ToPolicy().Breaker!.Settings.Failures);

        Assert.Equal(4, failures.Multiple);
        Assert.Equal(TimeSpan.FromMinutes(5), failures.Window);
        Assert.Equal(100, failures.MinimumSamples);
        Assert.Equal(0.05, failures.AbsoluteFloor);
    }

    [Fact]
    public void Every_relative_failure_setting_projects()
    {
        var policy = new ResilienceOptions
        {
            Breaker = new BreakerOptions
            {
                Failures = new FailureOptions
                {
                    Multiple = 3,
                    Window = TimeSpan.FromMinutes(10),
                    MinimumSamples = 200,
                    AbsoluteFloor = 0.1,
                },
            },
        }.ToPolicy();

        var failures = Assert.NotNull(policy.Breaker!.Settings.Failures);

        Assert.Equal(3, failures.Multiple);
        Assert.Equal(TimeSpan.FromMinutes(10), failures.Window);
        Assert.Equal(200, failures.MinimumSamples);
        Assert.Equal(0.1, failures.AbsoluteFloor);
    }

    /// <summary>
    ///     The break's jitter binds by name, so a test that needs a deterministic break can say so from
    ///     a configuration section rather than only from code.
    /// </summary>
    [Fact]
    public void The_break_jitter_binds_by_name()
    {
        var options = new ResilienceOptions();
        Config(("Breaker:BreakJitter", "None")).Bind(options);

        Assert.Equal(Jitter.None, options.ToPolicy().Breaker!.Settings.BreakJitter);
    }

    [Fact]
    public void A_breaker_with_no_jitter_named_jitters_its_break_by_default() =>
        Assert.Equal(Jitter.Equal, new ResilienceOptions { Breaker = new BreakerOptions() }.ToPolicy().Breaker!.Settings.BreakJitter);

    [Fact]
    public void Every_hedge_setting_projects()
    {
        var policy = new ResilienceOptions
        {
            Hedge = new HedgeOptions
            {
                Quantile = 0.99,
                MaxConcurrent = 3,
                MinimumSamples = 5,
                MinimumDelay = TimeSpan.FromMilliseconds(25),
                Window = TimeSpan.FromSeconds(10),
                SuppressAt = 0.25,
            },
        }.ToPolicy();

        var hedge = Assert.NotNull(policy.Hedge);

        Assert.Equal(0.99, hedge.Quantile);
        Assert.Equal(3, hedge.MaxConcurrent);
        Assert.Equal(5, hedge.MinimumSamples);
        Assert.Equal(TimeSpan.FromMilliseconds(25), hedge.MinimumDelay);
        Assert.Equal(TimeSpan.FromSeconds(10), hedge.Window);
        Assert.Equal(0.25, hedge.SuppressAt);
    }

    /// <summary>
    ///     The error-rate suppression is on for a section that says nothing about it, and
    ///     <c>"SuppressAt": 1</c> is how a section turns it off.
    /// </summary>
    [Fact]
    public void A_hedge_section_suppresses_on_an_elevated_error_rate_unless_it_says_otherwise()
    {
        var options = new ResilienceOptions();
        Config(("Hedge:Quantile", "0.99")).Bind(options);

        Assert.Equal(0.5, options.ToPolicy().Hedge!.Value.SuppressAt);

        Config(("Hedge:SuppressAt", "1")).Bind(options);

        Assert.Equal(1, options.ToPolicy().Hedge!.Value.SuppressAt);
    }

    /// <summary>
    ///     A section cannot name a clock, so a configured breaker and budget take the policy's. Without
    ///     it, a policy under a fake clock would still have a breaker running on wall time.
    /// </summary>
    [Fact]
    public void A_configured_breaker_and_budget_take_the_policys_clock()
    {
        var time = new FakeTimeProvider();

        var policy = new ResilienceOptions
        {
            BudgetFraction = 0.5,
            Breaker = new BreakerOptions { ConsecutiveFailures = 2 },
        }.ToPolicy(Resilience.Default with { Time = time });

        Assert.Same(time, policy.Breaker!.Settings.Time);

        // The budget's clock is not readable, so it is asserted through the refill it drives: on
        // TimeProvider.System no measurable time passes here and the bucket stays empty.
        var budget = policy.Budget!;

        while (budget.Utilization < 1)
        {
            Assert.True(budget.TrySpend());
        }

        time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(0, budget.Utilization);
    }

    /// <summary>An explicitly named breaker clock is not overwritten by the policy's.</summary>
    [Fact]
    public void A_hand_built_breaker_keeps_its_own_clock()
    {
        var breakerTime = new FakeTimeProvider();
        var settings = new BreakerSettings { Time = breakerTime };

        Assert.Same(breakerTime, settings.ConfiguredTime);
        Assert.Null(new BreakerSettings().ConfiguredTime);
        Assert.Same(TimeProvider.System, new BreakerSettings().Time);
    }

    /// <summary>
    ///     Off by default and in every preset. Inheriting an inbound deadline is a deliberate
    ///     configuration decision since the policy cannot see this bound at definition time.
    /// </summary>
    [Fact]
    public void An_unmentioned_ambient_deadline_stays_off()
    {
        Assert.False(new ResilienceOptions().ToPolicy().UseAmbientDeadline);
        Assert.False(new ResilienceOptions { Preset = "Http" }.ToPolicy().UseAmbientDeadline);

        // And a section that mentions it wins over a baseline that had it on, in both directions.
        var baseline = Resilience.Http with { UseAmbientDeadline = true };
        Assert.False(new ResilienceOptions { UseAmbientDeadline = false }.ToPolicy(baseline).UseAmbientDeadline);
        Assert.True(new ResilienceOptions().ToPolicy(baseline).UseAmbientDeadline);
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
                ("UseAmbientDeadline", "true"),
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
        Assert.True(policy.UseAmbientDeadline);
        Assert.Equal(TimeSpan.FromSeconds(4), policy.Backoff.Max);
        Assert.Equal(Jitter.Equal, policy.Backoff.Jitter);
        Assert.Same(Classifier.Http, policy.Classify);
        Assert.NotNull(policy.Budget);
        Assert.Equal(8, policy.Breaker!.Settings.ConsecutiveFailures);
        Assert.Equal(TimeSpan.FromSeconds(2), policy.Breaker.Settings.SlowCallThreshold);

        // The projection is not validation's substitute, but a projected policy must be executable.
        policy.Validate();
    }

    /// <summary>
    ///     Both relative trips are on by default, and a section cannot say <c>null</c> - so a zero
    ///     multiple is how configuration turns one off, the same way it turns the measured ceiling off.
    /// </summary>
    [Fact]
    public void A_zero_multiple_turns_a_relative_trip_off()
    {
        var options = new ResilienceOptions();

        Config(
                ("Breaker:SlowCalls:Multiple", "0"),
                ("Breaker:Failures:Multiple", "0"))
            .Bind(options);

        var settings = options.ToPolicy().Breaker!.Settings;

        Assert.Null(settings.SlowCalls);
        Assert.Null(settings.Failures);
    }

    /// <summary>An absolute threshold in a section replaces the default relative trip, as it does in code.</summary>
    [Fact]
    public void An_absolute_slow_call_threshold_in_a_section_replaces_the_default_relative_trip()
    {
        var options = new ResilienceOptions();

        Config(("Breaker:SlowCallThreshold", "00:00:02")).Bind(options);

        var settings = options.ToPolicy().Breaker!.Settings;

        Assert.Equal(TimeSpan.FromSeconds(2), settings.SlowCallThreshold);
        Assert.Null(settings.SlowCalls);
    }

    private static TimeSpan Delay(Resilience policy, int attemptNumber) =>
        policy.Backoff.Compute(new NextAttempt(attemptNumber, Verdict.Transient, null, Timeout.InfiniteTimeSpan, default));
}
