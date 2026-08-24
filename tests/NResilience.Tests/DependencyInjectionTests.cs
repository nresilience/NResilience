using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NResilience.Extensions;

namespace NResilience.Tests;

/// <summary>
///     Registration, resolution and hot reload.
///     <para>
///         The design's claim is that hot reload is a reference swap rather than a
///         machine: a policy is an immutable value, so there is no in-flight execution to drain and no
///         pipeline to rebuild. The tests that matter here are therefore about what survives the swap - a
///         breaker's state must, and the configuration must not.
///     </para>
/// </summary>
public sealed class DependencyInjectionTests
{
    private static IResiliencePolicies Build(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        register(services);
        return services.BuildServiceProvider().GetRequiredService<IResiliencePolicies>();
    }

    // ---- Registration ----

    [Fact]
    public void A_registered_policy_resolves_by_name()
    {
        var policies = Build(s =>
            s.AddResilience("api", Resilience.Http with { Deadline = TimeSpan.FromSeconds(10) }));

        Assert.Equal(TimeSpan.FromSeconds(10), policies["api"].Deadline);
        Assert.Equal(["api"], policies.Names);
    }

    /// <summary>The name a developer typed into the container is the name every diagnostic and every telemetry tag uses.</summary>
    [Fact]
    public void A_registered_policy_is_named_after_its_registration()
    {
        var policies = Build(s => s.AddResilience("payments", Resilience.Default));

        Assert.Equal("payments", policies["payments"].Name);
    }

    /// <summary>
    ///     The registration name beats a name the policy value carried, and this is the case that
    ///     forced the rule: <see cref="Resilience.Http" /> is named "http", so the most likely line
    ///     anybody writes would otherwise tag every client in the process "http" and make four of them
    ///     indistinguishable in the metrics.
    /// </summary>
    [Fact]
    public void The_registration_name_beats_a_preset_name()
    {
        var policies = Build(s => s
            .AddResilience("api", Resilience.Http)
            .AddResilience("reports", Resilience.Http));

        Assert.Equal("api", policies["api"].Name);
        Assert.Equal("reports", policies["reports"].Name);
    }

    /// <summary>Naming the policy in configuration is the deliberate override, and it wins.</summary>
    [Fact]
    public void An_explicitly_configured_name_is_not_overwritten()
    {
        var policies = Build(s => s.AddResilience("api", o =>
        {
            o.Preset = "Http";
            o.Name = "upstream";
        }));

        Assert.Equal("upstream", policies["api"].Name);
    }

    /// <summary>
    ///     Validation happens at registration, which is one of the three places the design promises it.
    ///     A deadline of minus one second should not survive until the first request.
    /// </summary>
    [Fact]
    public void An_invalid_policy_fails_at_registration()
    {
        var services = new ServiceCollection();

        Assert.Throws<ResilienceConfigurationException>(() => services.AddResilience("api", Resilience.Default with { Attempts = 0 }));
    }

    /// <summary>An unknown name is a mistake worth naming, and the message says what is registered.</summary>
    [Fact]
    public void An_unknown_name_throws_and_lists_what_is_registered()
    {
        var policies = Build(s => s
            .AddResilience("api", Resilience.Http)
            .AddResilience("reports", Resilience.Http));

        var error = Assert.Throws<ResilienceConfigurationException>(() => policies["apu"]);

        Assert.Contains("apu", error.Message, StringComparison.Ordinal);
        Assert.Contains("api, reports", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGet_does_not_throw_on_an_unknown_name()
    {
        var policies = Build(s => s.AddResilience("api", Resilience.Http));

        Assert.False(policies.TryGet("nope", out var missing));
        Assert.Equal(Resilience.Default, missing);
        Assert.True(policies.TryGet("api", out var found));
        Assert.Equal("api", found.Name);
    }

    /// <summary>The container is asked for a policy on every call, so resolution has to be cheap and stable.</summary>
    [Fact]
    public void Resolving_twice_returns_the_same_instance()
    {
        var policies = Build(s => s.AddResilience("api", Resilience.Http));

        Assert.Same(policies["api"], policies["api"]);
    }

    // ---- Configuration ----

    [Fact]
    public void A_section_registers_one_policy_per_child()
    {
        var source = new Source(new Dictionary<string, string?>
        {
            ["Resilience:api:Preset"] = "Http",
            ["Resilience:api:Attempts"] = "3",
            ["Resilience:api:Deadline"] = "00:00:10",
            ["Resilience:reports:Preset"] = "Http",
            ["Resilience:reports:Attempts"] = "5",
            ["Resilience:reports:Deadline"] = "00:05:00",
        });

        var policies = Build(s => s.AddResilience(source.Configuration.GetSection("Resilience")));

        Assert.Equal(["api", "reports"], policies.Names.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(TimeSpan.FromSeconds(10), policies["api"].Deadline);
        Assert.Equal(5, policies["reports"].Attempts);
        Assert.Same(Classifier.Http, policies["reports"].Classify);
    }

    /// <summary>Configuration overrides the registered policy rather than replacing it, so code sets the shape and the file sets the numbers.</summary>
    [Fact]
    public void Configuration_is_projected_onto_the_registered_policy()
    {
        var source = new Source(new Dictionary<string, string?> { ["api:Attempts"] = "7" });

        var policies = Build(s =>
        {
            s.AddResilience("api", Resilience.Http with { Deadline = TimeSpan.FromSeconds(11) });
            s.AddResilience("api", source.Configuration.GetSection("api"));
        });

        Assert.Equal(7, policies["api"].Attempts);
        Assert.Equal(TimeSpan.FromSeconds(11), policies["api"].Deadline);
    }

    /// <summary>
    ///     A classifier is a lambda and JSON cannot hold one, so the configure callback is where it
    ///     goes - and it runs last, so it wins.
    /// </summary>
    [Fact]
    public void The_configure_callback_runs_after_configuration()
    {
        var source = new Source(new Dictionary<string, string?> { ["api:Attempts"] = "7" });
        var custom = Classifier.RetryEverything;

        var policies = Build(s => s.AddResilience(
            "api",
            source.Configuration.GetSection("api"),
            p => p with { Classify = custom, Attempts = p.Attempts + 1 }));

        Assert.Same(custom, policies["api"].Classify);
        Assert.Equal(8, policies["api"].Attempts);
    }

    [Fact]
    public void A_policy_can_be_configured_in_code_without_a_section()
    {
        var policies = Build(s => s.AddResilience("api", o =>
        {
            o.Preset = "Http";
            o.Attempts = 4;
        }));

        Assert.Equal(4, policies["api"].Attempts);
        Assert.Same(Classifier.Http, policies["api"].Classify);
    }

    /// <summary>An empty or missing section still yields a working service, rather than a container that cannot resolve it.</summary>
    [Fact]
    public void An_empty_section_yields_an_empty_roster()
    {
        var policies = Build(s =>
            s.AddResilience(new ConfigurationBuilder().Build().GetSection("Resilience")));

        Assert.Empty(policies.Names);
    }

    // ---- Hot reload ----

    /// <summary>
    ///     The swap. <c>IOptionsMonitor</c> fires, the DTO is projected onto a new
    ///     <see cref="Resilience" />, and the next resolve hands out the new one - no drain, no rebuild.
    /// </summary>
    [Fact]
    public void Editing_configuration_swaps_the_policy()
    {
        var source = new Source(new Dictionary<string, string?>
        {
            ["Resilience:api:Attempts"] = "3",
        });

        var policies = Build(s => s.AddResilience(source.Configuration.GetSection("Resilience")));

        Assert.Equal(3, policies["api"].Attempts);

        source.Replace(new Dictionary<string, string?> { ["Resilience:api:Attempts"] = "9" });

        Assert.Equal(9, policies["api"].Attempts);
    }

    /// <summary>
    ///     The consequence the design says is documented rather than hidden: a policy captured into a
    ///     field at construction time is a snapshot, and the swap never reaches it. Resolve per call.
    /// </summary>
    [Fact]
    public void A_policy_captured_into_a_field_does_not_see_the_swap()
    {
        var source = new Source(new Dictionary<string, string?> { ["Resilience:api:Attempts"] = "3" });
        var policies = Build(s => s.AddResilience(source.Configuration.GetSection("Resilience")));

        var captured = policies["api"];

        source.Replace(new Dictionary<string, string?> { ["Resilience:api:Attempts"] = "9" });

        Assert.Equal(3, captured.Attempts);
        Assert.Equal(9, policies["api"].Attempts);
    }

    /// <summary>
    ///     The other consequence, and the one that would be a production incident if it went the other
    ///     way: a breaker that is open because a dependency is down stays open across a configuration
    ///     edit. Its state is the point of having it, and handing the traffic straight back to a dead
    ///     dependency because somebody edited a JSON file is the worst available reading of "reload".
    /// </summary>
    [Fact]
    public void A_live_breaker_survives_a_reload()
    {
        var source = new Source(new Dictionary<string, string?>
        {
            ["Resilience:api:Attempts"] = "3",
            ["Resilience:api:Breaker:ConsecutiveFailures"] = "1",
        });

        var policies = Build(s => s.AddResilience(source.Configuration.GetSection("Resilience")));

        var breaker = policies["api"].Breaker!;
        breaker.Isolate();
        Assert.Equal(BreakerState.Isolated, breaker.State);

        source.Replace(new Dictionary<string, string?>
        {
            ["Resilience:api:Attempts"] = "9",
            ["Resilience:api:Breaker:ConsecutiveFailures"] = "1",
        });

        Assert.Equal(9, policies["api"].Attempts);
        Assert.Same(breaker, policies["api"].Breaker);
        Assert.Equal(BreakerState.Isolated, policies["api"].Breaker!.State);
    }

    /// <summary>
    ///     The same rule for the budget, including the default one. A budget's whole job is to remember
    ///     how much traffic succeeded recently, and a null <see cref="Resilience.Budget" /> means the
    ///     core creates one keyed by policy <i>instance</i> - so reload would silently reset it. The
    ///     registration pins it to the name instead.
    /// </summary>
    [Fact]
    public void A_live_budget_survives_a_reload_even_when_it_was_never_configured()
    {
        var source = new Source(new Dictionary<string, string?> { ["Resilience:api:Attempts"] = "3" });
        var policies = Build(s => s.AddResilience(source.Configuration.GetSection("Resilience")));

        var budget = policies["api"].Budget!;

        source.Replace(new Dictionary<string, string?> { ["Resilience:api:Attempts"] = "9" });

        Assert.Same(budget, policies["api"].Budget);
    }

    /// <summary>A single-attempt policy has nothing to spend and gets no budget, exactly as the core decides.</summary>
    [Fact]
    public void A_single_attempt_policy_gets_no_budget()
    {
        var policies = Build(s => s.AddResilience("once", Resilience.Default with { Attempts = 1 }));

        Assert.Null(policies["once"].Budget);
    }

    /// <summary>
    ///     Sharing a breaker between two policies cannot be said in JSON, so the configure callback is
    ///     how it is said - and the registration must not overwrite what it chose.
    /// </summary>
    [Fact]
    public void A_breaker_shared_through_the_configure_callback_is_kept()
    {
        var shared = new Breaker { Name = "shared" };

        var policies = Build(s => s
            .AddResilience("api", Resilience.Http, p => p with { Breaker = shared })
            .AddResilience("reports", Resilience.Http, p => p with { Breaker = shared }));

        Assert.Same(shared, policies["api"].Breaker);
        Assert.Same(shared, policies["reports"].Breaker);
    }

    /// <summary>Two policies that did not ask to share do not share, which is the blast-radius decision the design argues for.</summary>
    [Fact]
    public void Two_policies_do_not_share_a_breaker_by_accident()
    {
        var policies = Build(s => s
            .AddResilience("api", o => o.Breaker = new BreakerOptions { ConsecutiveFailures = 2 })
            .AddResilience("reports", o => o.Breaker = new BreakerOptions { ConsecutiveFailures = 2 }));

        Assert.NotSame(policies["api"].Breaker, policies["reports"].Breaker);
    }

    /// <summary>An in-memory source whose values can be replaced, so reload is testable without a file system.</summary>
    private sealed class Source
    {
        private readonly ConfigurationManager _manager = new();
        private Dictionary<string, string?> _values;

        public Source(Dictionary<string, string?> values)
        {
            _values = values;
            _manager.AddInMemoryCollection(_values);
        }

        public IConfiguration Configuration => _manager;

        public void Replace(Dictionary<string, string?> values)
        {
            _values = values;
            _manager.Sources.Clear();

            // Clearing and re-adding raises the change token every IOptionsMonitor is watching,
            // which is exactly what editing appsettings.json does.
            _manager.AddInMemoryCollection(_values);
        }
    }
}
