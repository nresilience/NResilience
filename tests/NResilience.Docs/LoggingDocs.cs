using System.Diagnostics;
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
        services.AddResilience("payments", Resilience.Http);

        // Nothing else to call. The policy logs under "NResilience.payments", which is the category
        // an appsettings.json filter matches.
        // </snippet:logging-registered>

        var payments = services.BuildServiceProvider().GetRequiredService<IResiliencePolicies>()["payments"];

        Assert.Equal("NResilience.payments", ResilienceLogging.CategoryFor(payments.Name));
        Assert.NotNull(payments.OnEvent);
    }

    [Fact]
    public void The_filter_is_read_from_configuration()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.logging.json")
            .Build();

        Assert.Equal("Warning", configuration["Logging:LogLevel:NResilience"]);
        Assert.Equal("Debug", configuration["Logging:LogLevel:NResilience.payments"]);
    }

    [Fact]
    public void A_section_chooses_the_profile_per_policy()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.logging-verbose.json")
            .Build();

        var provider = new FakeLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Trace));
        services.AddResilience(configuration.GetSection("Resilience"));

        var policies = services.BuildServiceProvider().GetRequiredService<IResiliencePolicies>();

        Assert.NotNull(policies["payments"].OnEvent);

        // "Off" attaches no listener at all, so it costs nothing rather than costing a suppressed
        // call. Telemetry is unaffected, which is why OnEvent is still not null.
        Assert.Contains("logging Verbose", provider.Collector.GetSnapshot().First(r => r.Id.Id == 1020).Message, StringComparison.Ordinal);
        Assert.NotNull(policies["reports"].OnEvent);
    }

    [Fact]
    public async Task A_hand_built_policy_opts_in()
    {
        ILogger logger = new FakeLogger();
        var cancellationToken = TestContext.Current.CancellationToken;

        // <snippet:logging-hand-built>
        // A policy registered in a container logs for you. A policy in a static field does not -
        // this says it, and the logger's category is what a filter matches.
        var payments = (Resilience.Http with { Name = "payments" }).WithLogging(logger);
        // </snippet:logging-hand-built>

        Assert.Equal(1, await payments.RunAsync(_ => Task.FromResult(1), cancellationToken));
    }

    [Fact]
    public void A_console_logger_is_one_line()
    {
        // <snippet:logging-console>
        using var factory = LoggerFactory.Create(b => b
            .AddConsole()
            .SetMinimumLevel(LogLevel.Debug));

        var payments = (Resilience.Http with { Name = "payments" })
            .WithLogging(factory.CreateLogger(ResilienceLogging.CategoryFor("payments")));
        // </snippet:logging-console>

        Assert.NotNull(payments.OnEvent);
    }

    [Fact]
    public void A_level_delegate_retunes_one_record()
    {
        ILogger logger = new FakeLogger();

        // <snippet:logging-level>
        // Event 1013 is "the circuit breaker opened". Everything else keeps the profile's level:
        // return null to say nothing, or LogLevel.None to drop the record.
        var payments = (Resilience.Http with { Name = "payments" }).WithLogging(
            logger,
            new ResilienceLoggingOptions
            {
                Level = (id, _) => id.Id == 1013 ? LogLevel.Critical : null,
            });
        // </snippet:logging-level>

        Assert.NotNull(payments.OnEvent);
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

        services.AddResilience("payments", Resilience.Http);

        Assert.NotNull(services.BuildServiceProvider().GetRequiredService<IResiliencePolicies>()["payments"].OnEvent);
    }

    [Fact]
    public async Task Records_are_asserted_on_with_a_fake_logger()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var calls = Sequence.For<int>().Throws(new IOException()).Returns(1);

        // <snippet:logging-assert>
        var logger = new FakeLogger();
        var payments = (Resilience.Http with { Name = "payments", Backoff = Backoff.None })
            .WithLogging(logger);

        await payments.RunAsync(attempt => calls.NextAsync(attempt), cancellationToken);

        // 1005 is "succeeded on attempt N". Every ID is tabled in docs/reference/events.md.
        Assert.Contains(1005, logger.Collector.GetSnapshot().Select(record => record.Id.Id));
        // </snippet:logging-assert>
    }
}
