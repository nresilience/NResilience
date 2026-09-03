using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using NResilience.Extensions;

namespace NResilience.Tests;

/// <summary>
///     Adaptive log sampling: what a healthy policy is allowed to write, what an incident restores, and
///     the records that are never sampled at all.
///     <para>
///         The counting is exact rather than random, so every assertion here is on a number rather than
///         on a range - which is the point of the design as much as it is a convenience for the tests.
///     </para>
/// </summary>
public sealed class LogSamplingTests
{
    // ---- The steady state ----

    [Fact]
    public void The_first_records_are_written_in_full_and_the_rest_are_sampled()
    {
        var logger = new FakeLogger();
        var listener = Listener(logger, LogSampling.OneIn(4) with { MinimumSamples = 2 }, new FakeTimeProvider());

        // Two through the cold start, then one in four: 2 + 3 of the remaining twelve.
        for (var i = 0; i < 14; i++)
        {
            listener(Succeeded());
        }

        Assert.Equal(5, Count(logger, 1004));
    }

    [Fact]
    public void Each_record_is_counted_on_its_own()
    {
        var logger = new FakeLogger();
        var listener = Listener(logger, LogSampling.OneIn(10) with { MinimumSamples = 0 }, new FakeTimeProvider());

        // Nine successes and one attempt record. A shared counter would keep the attempt record
        // because it happened to be tenth; a per-record counter keeps neither.
        for (var i = 0; i < 9; i++)
        {
            listener(Succeeded());
        }

        listener(CallEvent.Create(CallEventKind.Attempt, "api", 1, Verdict.Ok));

        Assert.Empty(logger.Collector.GetSnapshot());
    }

    [Fact]
    public void Sampling_is_off_until_it_is_configured()
    {
        var logger = new FakeLogger();
        var listener = ResilienceLogging.Listener(logger, new ResilienceLoggingOptions(), new FakeTimeProvider());

        for (var i = 0; i < 50; i++)
        {
            listener(Succeeded());
        }

        Assert.Equal(50, Count(logger, 1004));
    }

    [Fact]
    public void One_in_one_is_no_sampling()
    {
        var logger = new FakeLogger();
        var listener = Listener(logger, LogSampling.OneIn(1) with { MinimumSamples = 0 }, new FakeTimeProvider());

        for (var i = 0; i < 50; i++)
        {
            listener(Succeeded());
        }

        Assert.Equal(50, Count(logger, 1004));
    }

    // ---- The incident window ----

    [Fact]
    public void An_incident_restores_every_record_for_the_window()
    {
        var logger = new FakeLogger();
        var time = new FakeTimeProvider();

        var listener = Listener(
            logger,
            LogSampling.OneIn(10) with { MinimumSamples = 0, IncidentWindow = TimeSpan.FromSeconds(30) },
            time);

        listener(CallEvent.Create(CallEventKind.BreakerOpened, "api", 3));

        for (var i = 0; i < 5; i++)
        {
            listener(Succeeded());
        }

        Assert.Equal(5, Count(logger, 1004));

        // The window closes, and the steady state costs a tenth again.
        time.Advance(TimeSpan.FromSeconds(31));

        for (var i = 0; i < 5; i++)
        {
            listener(Succeeded());
        }

        Assert.Equal(5, Count(logger, 1004));
    }

    [Fact]
    public void The_window_runs_from_the_most_recent_incident()
    {
        var logger = new FakeLogger();
        var time = new FakeTimeProvider();

        var listener = Listener(
            logger,
            LogSampling.OneIn(10) with { MinimumSamples = 0, IncidentWindow = TimeSpan.FromSeconds(30) },
            time);

        listener(CallEvent.Create(CallEventKind.BreakerOpened, "api", 3));
        time.Advance(TimeSpan.FromSeconds(20));

        // A breaker still refusing calls is still an incident, so the window moves with it.
        listener(CallEvent.Create(CallEventKind.RejectedByBreaker, "api", reason: StopReason.DependencyUnavailable));
        time.Advance(TimeSpan.FromSeconds(20));

        listener(Succeeded());

        Assert.Equal(1, Count(logger, 1004));
    }

    [Fact]
    public void The_window_opens_on_the_event_rather_than_on_the_written_record()
    {
        var logger = new FakeLogger();

        var listener = ResilienceLogging.Listener(
            logger,
            new ResilienceLoggingOptions
            {
                Sampling = LogSampling.OneIn(10) with { MinimumSamples = 0 },

                // The sink is not carrying the warning. The process is still in an incident.
                Level = (id, _) => id.Id == 1013 ? LogLevel.None : null,
            },
            new FakeTimeProvider());

        listener(CallEvent.Create(CallEventKind.BreakerOpened, "api", 3));

        for (var i = 0; i < 5; i++)
        {
            listener(Succeeded());
        }

        Assert.DoesNotContain(logger.Collector.GetSnapshot(), r => r.Id.Id == 1013);
        Assert.Equal(5, Count(logger, 1004));
    }

    [Fact]
    public void A_footgun_does_not_hold_the_window_open()
    {
        var logger = new FakeLogger();
        var listener = Listener(logger, LogSampling.OneIn(10) with { MinimumSamples = 0 }, new FakeTimeProvider());

        // Nested retry warns once and recurs for the life of the process. If it opened the window,
        // sampling would be off forever and nothing would say so.
        for (var i = 0; i < 5; i++)
        {
            listener(CallEvent.Create(CallEventKind.NestedRetry, "api"));
            listener(Succeeded());
        }

        Assert.Equal(0, Count(logger, 1004));
    }

    // ---- What is never sampled ----

    [Fact]
    public void Transitions_and_terminal_failures_are_never_sampled()
    {
        var logger = new FakeLogger();

        var listener = Listener(
            logger,
            LogSampling.OneIn(100) with { MinimumSamples = 0, IncidentWindow = TimeSpan.Zero },
            new FakeTimeProvider());

        for (var i = 0; i < 5; i++)
        {
            listener(CallEvent.Create(CallEventKind.BreakerHalfOpened, "api"));
            listener(CallEvent.Create(CallEventKind.BreakerClosed, "api"));
            listener(CallEvent.Create(CallEventKind.Exhausted, "api", 3, Verdict.Transient, exception: new TimeoutException()));
        }

        Assert.Equal(5, Count(logger, 1014));
        Assert.Equal(5, Count(logger, 1015));
        Assert.Equal(5, Count(logger, 1008));
    }

    // ---- Configuration ----

    [Fact]
    public void A_losing_configuration_is_refused_when_the_listener_is_built()
    {
        var options = new ResilienceLoggingOptions { Sampling = LogSampling.OneIn(0) };

        var thrown = Assert.Throws<ResilienceConfigurationException>(() => ResilienceLogging.Listener(new FakeLogger(), options));

        Assert.Contains("LogSampling.KeepOneIn", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_negative_window_and_a_negative_cold_start_are_both_reported()
    {
        var options = new ResilienceLoggingOptions
        {
            Sampling = LogSampling.OneIn(0) with
            {
                IncidentWindow = TimeSpan.FromSeconds(-1),
                MinimumSamples = -1,
            },
        };

        var thrown = Assert.Throws<ResilienceConfigurationException>(() => ResilienceLogging.Listener(new FakeLogger(), options));

        Assert.Equal(3, thrown.Problems.Count);
    }

    [Fact]
    public void Equality_is_over_the_effective_configuration()
    {
        Assert.Equal(
            LogSampling.OneIn(),
            LogSampling.OneIn() with { IncidentWindow = TimeSpan.FromMinutes(1), MinimumSamples = 20 });

        Assert.NotEqual(LogSampling.OneIn(), LogSampling.OneIn(10));
    }

    [Fact]
    public void The_configuration_describes_itself()
    {
        Assert.Equal(
            "1 in 20 while healthy (first 20 of each, all of them for 60s after an incident)",
            LogSampling.OneIn().ToString());
    }

    [Fact]
    public void A_process_wide_sampling_reaches_a_policy_that_overrode_the_profile()
    {
        var provider = new FakeLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Trace));
        services.AddResilienceLogging(o => o.Sampling = LogSampling.OneIn(10) with { MinimumSamples = 0 });

        services.AddResilience("api", o =>
        {
            o.Preset = "Http";
            o.Logging = "Verbose";
        });

        var api = services.BuildServiceProvider().GetRequiredService<IResiliencePolicies>()["api"];

        for (var i = 0; i < 9; i++)
        {
            api.OnEvent!(Succeeded());
        }

        Assert.DoesNotContain(provider.Collector.GetSnapshot(), r => r.Id.Id == 1004);
    }

    // ---- Helpers ----

    private static Action<CallEvent> Listener(FakeLogger logger, LogSampling sampling, TimeProvider time) =>
        ResilienceLogging.Listener(logger, new ResilienceLoggingOptions { Sampling = sampling }, time);

    private static CallEvent Succeeded() =>
        CallEvent.Create(CallEventKind.Succeeded, "api", 1, Verdict.Ok, reason: StopReason.Succeeded);

    private static int Count(FakeLogger logger, int id) =>
        logger.Collector.GetSnapshot().Count(record => record.Id.Id == id);
}
