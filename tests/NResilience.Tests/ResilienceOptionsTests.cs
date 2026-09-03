using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
            ("Backoff:Max", "00:00:05"),
            ("Backoff:Jitter", "None"),
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
            Backoff = new BackoffOptions
            {
                Max = TimeSpan.FromSeconds(5),
                Jitter = Jitter.None,
                TransientBase = TimeSpan.FromMilliseconds(50),
                Factor = 3,
            },
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
                    TimeSpan.FromSeconds(60)) with
                {
                    Jitter = Jitter.None,
                },
        };

        var policy = new ResilienceOptions
        {
            Backoff = new BackoffOptions { Max = TimeSpan.FromSeconds(5) },
        }.ToPolicy(baseline);

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

        var policy = new ResilienceOptions
        {
            Backoff = new BackoffOptions { Max = TimeSpan.FromSeconds(5), Jitter = Jitter.None },
        }.ToPolicy(baseline);

        Assert.Equal(BackoffKind.Exponential, policy.Backoff.Kind);
        Assert.Equal(TimeSpan.FromSeconds(5), policy.Backoff.Max);
        Assert.Equal(Backoff.Default.TransientBase, policy.Backoff.TransientBase);
        Assert.Equal(Backoff.Default.ThrottledBase, policy.Backoff.ThrottledBase);
        Assert.Equal(Backoff.Default.Factor, policy.Backoff.Factor);
    }

    /// <summary>
    ///     A measured backoff base binds like any other section, and it is opt-in: a policy whose
    ///     configuration says nothing about it keeps the constant it was written with.
    /// </summary>
    [Fact]
    public void A_measured_backoff_base_binds_from_configuration()
    {
        var options = new ResilienceOptions();

        Config(
            ("Backoff:TransientBase", "00:00:00.200"),
            ("Backoff:MeasuredBase:Multiple", "2"),
            ("Backoff:MeasuredBase:Spread", "4"),
            ("Backoff:MeasuredBase:MinimumSamples", "50")).Bind(options);

        var policy = options.ToPolicy();

        Assert.Equal(TimeSpan.FromMilliseconds(200), policy.Backoff.TransientBase);

        var measured = Assert.NotNull(policy.Backoff.MeasuredBase);

        Assert.Equal(2.0, measured.Multiple);
        Assert.Equal(4.0, measured.Spread);
        Assert.Equal(50, measured.MinimumSamples);

        policy.Validate();
    }

    /// <summary>The default is off, which is the difference from the measured attempt ceiling.</summary>
    [Fact]
    public void No_backoff_base_is_measured_unless_the_section_asks_for_one()
    {
        Assert.Null(new ResilienceOptions().ToPolicy().Backoff.MeasuredBase);
    }

    /// <summary><c>"Enabled": false</c> drops a measured base the base policy carried.</summary>
    [Fact]
    public void A_disabled_measured_section_drops_the_base_policys_measurement()
    {
        var baseline = Resilience.Default with { Backoff = Backoff.Measured() };

        var policy = new ResilienceOptions
        {
            Backoff = new BackoffOptions { MeasuredBase = new MeasuredBaseOptions { Enabled = false } },
        }.ToPolicy(baseline);

        Assert.Null(policy.Backoff.MeasuredBase);
    }

    /// <summary>Jitter on its own is a modifier, not a reason to rebuild the curve.</summary>
    [Fact]
    public void Jitter_alone_leaves_the_rest_of_the_backoff_alone()
    {
        var baseline = Resilience.Default with { Backoff = Backoff.Constant(TimeSpan.FromSeconds(2)) };

        var policy = new ResilienceOptions { Backoff = new BackoffOptions { Jitter = Jitter.None } }.ToPolicy(baseline);

        Assert.Equal(Jitter.None, policy.Backoff.Jitter);
        Assert.Equal(TimeSpan.FromSeconds(2), Delay(policy, 2));
    }

    /// <summary>
    ///     One spelling of "off", and it is the word. A section cannot say <c>null</c>, and the fraction
    ///     that used to stand in for it was a superstition every reader had to be taught.
    /// </summary>
    [Fact]
    public void A_disabled_budget_section_turns_the_budget_off()
    {
        var policy = new ResilienceOptions { Budget = new BudgetOptions { Enabled = false } }.ToPolicy();

        Assert.Same(RetryBudget.None, policy.Budget);
    }

    /// <summary>An enabled budget section with nothing else in it is a budget at the defaults.</summary>
    [Fact]
    public void An_enabled_budget_section_turns_one_on_at_the_defaults()
    {
        var policy = new ResilienceOptions { Budget = new BudgetOptions { Enabled = true } }.ToPolicy();

        Assert.NotNull(policy.Budget);
        Assert.False(policy.Budget!.IsNone);
        Assert.False(policy.Budget.IsAutomatic);
    }

    // ---- One spelling of "off", and it works from a configuration layer ----

    /// <summary>
    ///     The reason <c>Enabled</c> exists rather than a magic number per section. Configuration
    ///     providers merge and never remove a key, so before this there was no way for an
    ///     <c>appsettings.Production.json</c> to take back a breaker or a hedge that the base file
    ///     turned on: the section was still there, and its presence was the switch.
    /// </summary>
    [Fact]
    public void A_later_configuration_layer_can_turn_a_feature_off()
    {
        var options = new ResilienceOptions();

        new ConfigurationBuilder()
            .AddInMemoryCollection(
            [
                // The base file.
                new KeyValuePair<string, string?>("Breaker:ConsecutiveFailures", "5"),
                new KeyValuePair<string, string?>("Hedge:Quantile", "0.9"),
            ])
            .AddInMemoryCollection(
            [
                // The environment override, which can only add keys.
                new KeyValuePair<string, string?>("Breaker:Enabled", "false"),
                new KeyValuePair<string, string?>("Hedge:Enabled", "false"),
            ])
            .Build()
            .Bind(options);

        var policy = options.ToPolicy();

        Assert.Null(policy.Breaker);
        Assert.Null(policy.Hedge);
    }

    /// <summary>
    ///     And the same section without the override still describes what it always did, so
    ///     <c>Enabled</c> is an override rather than a thing every section now has to say.
    /// </summary>
    [Fact]
    public void An_unmentioned_enabled_leaves_every_section_meaning_what_it_meant()
    {
        var policy = new ResilienceOptions
        {
            Breaker = new BreakerOptions { ConsecutiveFailures = 5 },
            Hedge = new HedgeOptions(),
        }.ToPolicy();

        Assert.NotNull(policy.Breaker);
        Assert.NotNull(policy.Hedge);
        Assert.Equal(AttemptCeiling.Above(), policy.AttemptCeiling);
        Assert.NotNull(policy.Breaker!.Settings.SlowCalls);
        Assert.NotNull(policy.Breaker.Settings.Failures);
    }

    /// <summary>
    ///     Every retired off switch fails at registration naming the one that replaced it. A silent
    ///     behavior change here would be worse than a startup failure: the value used to mean "off" and
    ///     now means a threshold nobody could have intended, so the message is the migration note.
    /// </summary>
    [Theory]
    [InlineData("AttemptCeiling:Multiple", "AttemptCeiling")]
    [InlineData("Backoff:MeasuredBase:Multiple", "Backoff.MeasuredBase")]
    [InlineData("Budget:Fraction", "Budget")]
    [InlineData("Breaker:SlowCalls:Multiple", "SlowCalls")]
    [InlineData("Breaker:Failures:Multiple", "Failures")]
    [InlineData("Breaker:Recovery:Length", "Recovery")]
    [InlineData("Hedge:WinRate:Minimum", "Hedge.WinRate")]
    public void A_retired_off_switch_names_the_one_that_replaced_it(string key, string section)
    {
        var options = new ResilienceOptions();

        Config((key, "0")).Bind(options);

        var error = Assert.Throws<ResilienceConfigurationException>(() => options.ToPolicy());

        Assert.Contains(section, error.Message, StringComparison.Ordinal);
        Assert.Contains("\"Enabled\": false", error.Message, StringComparison.Ordinal);
    }

    // ---- Turning measurement off from a section ----

    /// <summary>
    ///     In configuration <c>Adaptive</c> reaches the breaker, which in code it cannot. A section
    ///     builds a breaker for this policy alone rather than sharing one, so there is no other holder
    ///     for the switch to surprise - and that makes <c>"Adaptive": false</c> the single key that
    ///     turns off every measured term in the policy.
    /// </summary>
    [Fact]
    public void One_adaptive_key_turns_measurement_off_for_the_policy_and_its_breaker()
    {
        var options = new ResilienceOptions();

        Config(("Adaptive", "false"), ("Breaker:ConsecutiveFailures", "5")).Bind(options);

        var policy = options.ToPolicy();

        Assert.Null(policy.AttemptCeiling);
        Assert.Null(policy.Breaker!.Settings.SlowCalls);
        Assert.Null(policy.Breaker.Settings.Failures);

        policy.Validate();
    }

    /// <summary>The breaker section's own answer wins over the policy's, in both directions.</summary>
    [Theory]
    [InlineData("false", "true", true)]
    [InlineData("true", "false", false)]
    public void A_breaker_section_overrides_the_policys_adaptive(string policyValue, string breakerValue, bool measures)
    {
        var options = new ResilienceOptions();

        Config(("Adaptive", policyValue), ("Breaker:Adaptive", breakerValue)).Bind(options);

        var settings = options.ToPolicy().Breaker!.Settings;

        Assert.Equal(measures, settings.SlowCalls is not null);
        Assert.Equal(measures, settings.Failures is not null);
    }

    /// <summary>A section that says nothing leaves every measured term on, as it always did.</summary>
    [Fact]
    public void An_unmentioned_adaptive_leaves_measurement_on()
    {
        var policy = new ResilienceOptions { Breaker = new BreakerOptions() }.ToPolicy();

        Assert.True(policy.Adaptive);
        Assert.NotNull(policy.AttemptCeiling);
        Assert.NotNull(policy.Breaker!.Settings.SlowCalls);
    }

    // ---- An unrecognized key is a mistake, not a comment ----

    /// <summary>
    ///     The other half of what the DTO is for. Binding a section onto the record is silently partial;
    ///     binding a section with a key the DTO does not have used to be silently partial too - the key
    ///     bound nothing, and the policy quietly kept its defaults, which reads exactly like a policy
    ///     nobody configured. Renaming keys made that gap worth closing.
    /// </summary>
    /// <param name="key">A key that no longer exists, at each nesting depth the binder has to reach.</param>
    /// <param name="named">
    ///     The segment the binder complains about: the first one it cannot resolve, which for a renamed
    ///     <i>section</i> is the section rather than the property inside it.
    /// </param>
    [Theory]
    [InlineData("Timeouts:Multiple", "Timeouts")]
    [InlineData("MaxDelay", "MaxDelay")]
    [InlineData("BudgetFraction", "BudgetFraction")]
    [InlineData("Breaker:Window", "Window")]
    [InlineData("Breaker:Recovery:Fraction", "Fraction")]
    [InlineData("Adaptivee", "Adaptivee")]
    public void A_key_the_dto_does_not_have_fails_at_resolution(string key, string named)
    {
        var services = new ServiceCollection();
        services.AddResilience("api", Config((key, "1")));

        using var provider = services.BuildServiceProvider();
        var policies = provider.GetRequiredService<IResiliencePolicies>();

        var error = Assert.Throws<ResilienceConfigurationException>(() => _ = policies["api"]);

        Assert.Contains(named, error.Message, StringComparison.Ordinal);
        Assert.Contains("renamed", error.Message, StringComparison.Ordinal);
    }

    /// <summary>And a section made entirely of keys that do exist still binds, at every depth.</summary>
    [Fact]
    public void A_section_of_recognized_keys_still_binds()
    {
        var services = new ServiceCollection();

        services.AddResilience(
            "api",
            Config(
                ("Preset", "Http"),
                ("Adaptive", "true"),
                ("Backoff:Max", "00:00:04"),
                ("Budget:Fraction", "0.25"),
                ("AttemptCeiling:Multiple", "5"),
                ("Breaker:TripWindow", "00:00:30"),
                ("Breaker:Recovery:Length", "0.5"),
                ("Breaker:SlowCalls:Enabled", "false")));

        using var provider = services.BuildServiceProvider();
        var policy = provider.GetRequiredService<IResiliencePolicies>()["api"];

        Assert.Equal(TimeSpan.FromSeconds(4), policy.Backoff.Max);
        Assert.Equal(5, policy.AttemptCeiling!.Value.Multiple);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.Breaker!.Settings.TripWindow);
        Assert.Equal(0.5, policy.Breaker.Settings.Recovery!.Value.Length);
        Assert.Null(policy.Breaker.Settings.SlowCalls);
    }

    /// <summary>A named budget is the shared one, which is the opt-in the design insists is an opt-in.</summary>
    [Fact]
    public void A_named_budget_is_shared_by_name()
    {
        var first = new ResilienceOptions { Budget = new BudgetOptions { Shared = "tier-1" } }.ToPolicy();
        var second = new ResilienceOptions { Budget = new BudgetOptions { Shared = "tier-1" } }.ToPolicy();
        var other = new ResilienceOptions { Budget = new BudgetOptions { Shared = "tier-2" } }.ToPolicy();

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
    ///     and every default is usable, so <c>"AttemptCeiling": {}</c> is a complete configuration.
    /// </summary>
    [Fact]
    public void An_empty_timeouts_section_is_a_complete_configuration()
    {
        var options = new ResilienceOptions();
        Config(("AttemptCeiling:Quantile", "0.9")).Bind(options);

        var timeouts = Assert.NotNull(options.ToPolicy().AttemptCeiling);

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
            ("AttemptCeiling:Multiple", "4"),
            ("AttemptCeiling:Quantile", "0.99"),
            ("AttemptCeiling:Window", "00:10:00"),
            ("AttemptCeiling:MinimumSamples", "75"),
            ("AttemptCeiling:Floor", "00:00:00.020")).Bind(options);

        var timeouts = Assert.NotNull(options.ToPolicy().AttemptCeiling);

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
        Assert.Equal(AttemptCeiling.Above(), new ResilienceOptions { Attempts = 3 }.ToPolicy().AttemptCeiling);
        Assert.Equal(AttemptCeiling.Above(), new ResilienceOptions { AttemptCeiling = new AttemptCeilingOptions() }.ToPolicy().AttemptCeiling);
    }

    /// <summary>
    ///     A configuration section cannot say <c>null</c>, so it says <c>Enabled: false</c> - the same
    ///     shape every other section uses.
    /// </summary>
    [Fact]
    public void A_disabled_timeouts_section_turns_the_measured_ceiling_off()
    {
        var options = new ResilienceOptions();

        Config(("AttemptCeiling:Enabled", "false")).Bind(options);

        Assert.Null(options.ToPolicy().AttemptCeiling);
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
                SlowCalls = new SlowCallsOptions
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
        Assert.Equal(0.05, failures.Floor);
    }

    [Fact]
    public void Every_relative_failure_setting_projects()
    {
        var policy = new ResilienceOptions
        {
            Breaker = new BreakerOptions
            {
                Failures = new FailuresOptions
                {
                    Multiple = 3,
                    Window = TimeSpan.FromMinutes(10),
                    MinimumSamples = 200,
                    Floor = 0.1,
                },
            },
        }.ToPolicy();

        var failures = Assert.NotNull(policy.Breaker!.Settings.Failures);

        Assert.Equal(3, failures.Multiple);
        Assert.Equal(TimeSpan.FromMinutes(10), failures.Window);
        Assert.Equal(200, failures.MinimumSamples);
        Assert.Equal(0.1, failures.Floor);
    }

    [Fact]
    public void Every_recovery_setting_projects()
    {
        var options = new ResilienceOptions();

        Config(
            ("Breaker:Recovery:Length", "0.5"),
            ("Breaker:Recovery:MinimumLength", "00:00:02"),
            ("Breaker:Recovery:MaximumLength", "00:01:00"),
            ("Breaker:Recovery:InitialFraction", "0.1")).Bind(options);

        var recovery = options.ToPolicy().Breaker!.Settings.Recovery!.Value;

        Assert.Equal(0.5, recovery.Length);
        Assert.Equal(TimeSpan.FromSeconds(2), recovery.MinimumLength);
        Assert.Equal(TimeSpan.FromMinutes(1), recovery.MaximumLength);
        Assert.Equal(0.1, recovery.InitialFraction);
    }

    /// <summary>
    ///     Off unless the section says otherwise, and a section that is present turns it back off with
    ///     the word a section can say in place of the null it cannot.
    /// </summary>
    [Fact]
    public void The_ramp_is_off_until_a_section_asks_for_it()
    {
        Assert.Null(new ResilienceOptions { Breaker = new BreakerOptions() }.ToPolicy().Breaker!.Settings.Recovery);

        var on = new ResilienceOptions { Breaker = new BreakerOptions { Recovery = new RecoveryOptions() } };
        Assert.Equal(Recovery.Over(), on.ToPolicy().Breaker!.Settings.Recovery);

        var off = new ResilienceOptions { Breaker = new BreakerOptions { Recovery = new RecoveryOptions { Enabled = false } } };
        Assert.Null(off.ToPolicy().Breaker!.Settings.Recovery);
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
                WinRate = new WinRateOptions
                {
                    Minimum = 0.3,
                    Window = TimeSpan.FromMinutes(2),
                    MinimumSamples = 25,
                    MinimumAllowance = 0.1,
                },
            },
        }.ToPolicy();

        var hedge = Assert.NotNull(policy.Hedge);

        Assert.Equal(0.99, hedge.Quantile);
        Assert.Equal(3, hedge.MaxConcurrent);
        Assert.Equal(5, hedge.MinimumSamples);
        Assert.Equal(TimeSpan.FromMilliseconds(25), hedge.MinimumDelay);
        Assert.Equal(TimeSpan.FromSeconds(10), hedge.Window);
        Assert.Equal(0.25, hedge.SuppressAt);

        var feedback = Assert.NotNull(hedge.WinRate);

        Assert.Equal(0.3, feedback.Minimum);
        Assert.Equal(TimeSpan.FromMinutes(2), feedback.Window);
        Assert.Equal(25, feedback.MinimumSamples);
        Assert.Equal(0.1, feedback.MinimumAllowance);
    }

    /// <summary>
    ///     The win-rate loop is opt-in, unlike the error-rate suppression beside it: it is a control
    ///     loop over a control loop, so a section that says nothing about it gets none.
    /// </summary>
    [Fact]
    public void No_win_rate_feedback_unless_the_section_asks_for_one()
    {
        var options = new ResilienceOptions();
        Config(("Hedge:Quantile", "0.99")).Bind(options);

        Assert.Null(options.ToPolicy().Hedge!.Value.WinRate);

        Config(("Hedge:WinRate:Minimum", "0.2")).Bind(options);

        Assert.Equal(WinRate.AtLeast(), options.ToPolicy().Hedge!.Value.WinRate);
    }

    /// <summary><c>"Enabled": false</c> drops a loop the base policy carried.</summary>
    [Fact]
    public void A_disabled_win_rate_section_drops_the_base_policys_loop()
    {
        var baseline = Resilience.Default with
        {
            Hedge = Hedge.At() with { WinRate = WinRate.AtLeast() },
        };

        var policy = new ResilienceOptions
        {
            Hedge = new HedgeOptions { WinRate = new WinRateOptions { Enabled = false } },
        }.ToPolicy(baseline);

        Assert.Null(policy.Hedge!.Value.WinRate);
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
            Budget = new BudgetOptions { Fraction = 0.5 },
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
                ("Backoff:Max", "00:00:04"),
                ("Backoff:Jitter", "Equal"),
                ("Budget:Fraction", "0.25"),
                ("Budget:MinimumPerSecond", "10"),
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
    ///     Both relative trips are on by default, and a section cannot say <c>null</c> - so
    ///     <c>Enabled: false</c> is how configuration turns one off, the same way it turns the measured
    ///     ceiling off.
    /// </summary>
    [Fact]
    public void A_disabled_section_turns_a_relative_trip_off()
    {
        var options = new ResilienceOptions();

        Config(
                ("Breaker:SlowCalls:Enabled", "false"),
                ("Breaker:Failures:Enabled", "false"))
            .Bind(options);

        var settings = options.ToPolicy().Breaker!.Settings;

        Assert.Null(settings.SlowCalls);
        Assert.Null(settings.Failures);
    }

    /// <summary>An absolute threshold in a section composes with the default relative trip, as it does in code.</summary>
    [Fact]
    public void An_absolute_slow_call_threshold_in_a_section_composes_with_the_default_relative_trip()
    {
        var options = new ResilienceOptions();

        Config(("Breaker:SlowCallThreshold", "00:00:02")).Bind(options);

        var settings = options.ToPolicy().Breaker!.Settings;

        Assert.Equal(TimeSpan.FromSeconds(2), settings.SlowCallThreshold);
        Assert.Equal(SlowCalls.Above(), settings.SlowCalls);
    }

    private static TimeSpan Delay(Resilience policy, int attemptNumber) =>
        policy.Backoff.Compute(new NextAttempt(attemptNumber, Verdict.Transient, null, Timeout.InfiniteTimeSpan, default));
}
