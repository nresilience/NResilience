using Microsoft.Extensions.DependencyInjection;
using NResilience.Extensions;

namespace NResilience.Docs;

/// <summary>
///     Drain-aware shutdown: what a registered policy already does, how to tune or turn it off, and how
///     a process without the hosting package latches it itself.
/// </summary>
/// <remarks>
///     Nothing here calls <c>Draining.Begin()</c>. The latch is process-wide and there is no public way
///     back, so a snippet that set it would drain every other test in this assembly. The behavior is
///     gated in <c>NResilience.IntegrationTests.DrainingTests</c>, which runs serially for that reason.
/// </remarks>
public sealed class DrainingDocs
{
    [Fact]
    public void A_registered_policy_drains_without_being_asked()
    {
        var services = new ServiceCollection();

        // <snippet:draining-register>
        // Nothing to turn on. Every registered policy stops retrying when the host starts shutting
        // down, and this is where that is tuned - a shorter grace period than the host's, to leave
        // room for whatever runs after the calls stop.
        services.AddResilience(name: "api", policy: Resilience.Http);
        services.AddResilienceDraining(o => o.Grace = TimeSpan.FromSeconds(value: 20));

        // Or turned off, for a process whose outbound calls must run to their own bounds.
        services.AddResilienceDraining(o => o.DrainOnShutdown = false);

        // </snippet:draining-register>

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(@object: provider.GetRequiredService<IResiliencePolicies>()["api"]);
    }

    [Fact]
    public void A_process_without_a_host_latches_it_itself()
    {
        // <snippet:draining-manual>
        // Core has no dependency on Microsoft.Extensions.Hosting and will not acquire one, so a
        // console app or a worker without it says so directly. The grace period is optional: without
        // one, draining stops retries and leaves deadlines alone.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Draining.Begin(grace: TimeSpan.FromSeconds(value: 30));

        // </snippet:draining-manual>

        Assert.False(condition: Draining.IsDraining);
    }

    [Fact]
    public void A_readiness_probe_can_say_the_process_is_going_away()
    {
        // <snippet:draining-readiness>
        // The latch is readable, so a readiness probe can stop advertising this instance before the
        // load balancer works it out on its own.
        var ready = !Draining.IsDraining;

        // </snippet:draining-readiness>

        Assert.True(condition: ready);
    }
}
