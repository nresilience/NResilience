using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using NResilience.Extensions;
using NResilience.Testing;

namespace NResilience.Docs;

/// <summary>The log records: what is on by default, how to filter them, and how to assert on them.</summary>
public sealed class LoggingDocs
{
    [Fact]
    public void A_registered_policy_logs_without_any_extra_call()
    {
        var services = new ServiceCollection();

        // <snippet:logging-registered>
        services.AddLogging();
        services.AddResilience(name: "payments", policy: Resilience.Http);

        // Nothing else to call. The policy logs under "NResilience.payments", which is the category
        // an appsettings.json filter matches.
        // </snippet:logging-registered>

        var payments = services.BuildServiceProvider().GetRequiredService<IResiliencePolicies>()[name: "payments"];

        Assert.Equal(expected: "NResilience.payments", actual: ResilienceLogging.CategoryFor(policyName: payments.Name));
        Assert.NotNull(@object: payments.OnEvent);
    }

    [Fact]
    public void The_filter_is_read_from_configuration()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonFile(path: "appsettings.logging.json")
            .Build();

        Assert.Equal(expected: "Warning", actual: configuration[key: "Logging:LogLevel:NResilience"]);
        Assert.Equal(expected: "Debug", actual: configuration[key: "Logging:LogLevel:NResilience.payments"]);
    }

    [Fact]
    public void A_section_chooses_the_profile_per_policy()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonFile(path: "appsettings.logging-verbose.json")
            .Build();

        var provider = new FakeLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(provider: provider).SetMinimumLevel(level: LogLevel.Trace));
        services.AddResilience(section: configuration.GetSection(key: "Resilience"));

        var policies = services.BuildServiceProvider().GetRequiredService<IResiliencePolicies>();

        Assert.NotNull(@object: policies[name: "payments"].OnEvent);

        // "Off" attaches no listener at all, so it costs nothing rather than costing a suppressed
        // call. Telemetry is unaffected, which is why OnEvent is still not null.
        Assert.Contains(expectedSubstring: "logging Verbose", actualString: provider.Collector.GetSnapshot().First(r => r.Id.Id == 1020).Message,
            comparisonType: StringComparison.Ordinal);

        Assert.NotNull(@object: policies[name: "reports"].OnEvent);
    }

    [Fact]
    public async Task A_hand_built_policy_opts_in()
    {
        ILogger logger = new FakeLogger();
        var cancellationToken = TestContext.Current.CancellationToken;

        // <snippet:logging-hand-built>
        // A policy registered in a container logs for you. A policy in a static field does not -
        // this says it, and the logger's category is what a filter matches.
        var payments = (Resilience.Http with { Name = "payments" }).WithLogging(logger: logger);

        // </snippet:logging-hand-built>

        Assert.Equal(expected: 1, actual: await payments.RunAsync(_ => Task.FromResult(result: 1), cancellationToken: cancellationToken));
    }

    [Fact]
    public void A_console_logger_is_one_line()
    {
        // <snippet:logging-console>
        using var factory = LoggerFactory.Create(b => b
            .AddConsole()
            .SetMinimumLevel(level: LogLevel.Debug));

        var payments = (Resilience.Http with { Name = "payments" })
            .WithLogging(logger: factory.CreateLogger(categoryName: ResilienceLogging.CategoryFor(policyName: "payments")));

        // </snippet:logging-console>

        Assert.NotNull(@object: payments.OnEvent);
    }

    [Fact]
    public void A_level_delegate_retunes_one_record()
    {
        ILogger logger = new FakeLogger();

        // <snippet:logging-level>
        // Event 1013 is "the circuit breaker opened". Everything else keeps the profile's level:
        // return null to say nothing, or LogLevel.None to drop the record.
        var payments = (Resilience.Http with { Name = "payments" }).WithLogging(
            logger: logger,
            options: new ResilienceLoggingOptions
            {
                Level = (id, _) => id.Id == 1013 ? LogLevel.Critical : null,
            });

        // </snippet:logging-level>

        Assert.NotNull(@object: payments.OnEvent);
    }

    [Fact]
    public void Sampling_keeps_a_share_of_the_steady_state()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // <snippet:logging-sampling>
        // One call in twenty is written while the policy is healthy, and every record for a minute
        // after its breaker opens or starts refusing calls. The first 20 of each record are written
        // in full whatever happens, so a development run logs exactly as it did before.
        services.AddResilienceLogging(o => o.Sampling = LogSampling.OneIn(keepOneIn: 20));

        // </snippet:logging-sampling>

        services.AddResilience(name: "payments", policy: Resilience.Http);

        Assert.NotNull(@object: services.BuildServiceProvider().GetRequiredService<IResiliencePolicies>()[name: "payments"].OnEvent);
    }

    [Fact]
    public void Interleaved_records_correlate_on_the_trace_id()
    {
        var services = new ServiceCollection();

        // <snippet:logging-correlation>
        // A busy process interleaves records from many concurrent calls of the same policy. The
        // trace and span IDs are what line them back up, and for an HTTP client the telemetry
        // handler already starts one span per logical operation.
        services.AddLogging(b => b.Configure(o => o.ActivityTrackingOptions =
            ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId));

        // </snippet:logging-correlation>

        services.AddResilience(name: "payments", policy: Resilience.Http);

        Assert.NotNull(@object: services.BuildServiceProvider().GetRequiredService<IResiliencePolicies>()[name: "payments"].OnEvent);
    }

    [Fact]
    public async Task Records_are_asserted_on_with_a_fake_logger()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var calls = Sequence.For<int>().Throws(exception: new IOException()).Returns(result: 1);

        // <snippet:logging-assert>
        var logger = new FakeLogger();

        var payments = (Resilience.Http with { Name = "payments", Backoff = Backoff.None })
            .WithLogging(logger: logger);

        await payments.RunAsync(attempt => calls.NextAsync(cancellationToken: attempt), cancellationToken: cancellationToken);

        // 1005 is "succeeded on attempt N". Every ID is tabled in docs/reference/events.md.
        Assert.Contains(expected: 1005, collection: logger.Collector.GetSnapshot().Select(record => record.Id.Id));

        // </snippet:logging-assert>
    }
}
