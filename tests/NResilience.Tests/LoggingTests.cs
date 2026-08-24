using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using NResilience.Extensions;
using NResilience.Extensions.Internal;

namespace NResilience.Tests;

/// <summary>
/// The log vocabulary and the promises made about it.
/// <para>
/// Two of these tests are the whole answer to the verbosity objection - a healthy call writes
/// nothing above <c>Trace</c>, and a retried-then-successful one nothing above <c>Debug</c> - and
/// one of them, <c>Event_ids_are_stable</c>, is the contract somebody's alert depends on.
/// </para>
/// </summary>
public sealed class LoggingTests
{
    /// <summary>The checked-in table. Renumbering an ID breaks an alert, so the literals live here.</summary>
    private static readonly (int Id, string Name)[] Vocabulary =
    [
        (1000, "AttemptSucceeded"),
        (1001, "AttemptFailed"),
        (1002, "AttemptLimited"),
        (1003, "Retrying"),
        (1004, "CallSucceeded"),
        (1005, "CallSucceededAfterRetries"),
        (1006, "NotRetried"),
        (1007, "NotRetriedFirstSighting"),
        (1008, "Exhausted"),
        (1009, "DeadlineExceeded"),
        (1010, "RejectedDependencyUnavailable"),
        (1011, "RejectedBudgetExhausted"),
        (1012, "RejectedRepeat"),
        (1013, "BreakerOpened"),
        (1014, "BreakerHalfOpened"),
        (1015, "BreakerClosed"),
        (1016, "OrphanedWork"),
        (1017, "OrphanedWorkRepeat"),
        (1018, "NestedRetry"),
        (1019, "NestedRetryRepeat"),
        (1020, "PolicyResolved"),
        (1021, "PolicyClassifier"),
    ];

    // ---- The verbosity promise ----

    [Fact]
    public async Task A_healthy_call_writes_nothing_above_trace()
    {
        var logger = new FakeLogger();
        var api = Logged(logger, Resilience.Default with { Name = "api" });

        await api.RunAsync(_ => Task.FromResult(1), TestContext.Current.CancellationToken);

        Assert.NotEmpty(logger.Collector.GetSnapshot());
        Assert.All(logger.Collector.GetSnapshot(), record => Assert.Equal(LogLevel.Trace, record.Level));
    }

    [Fact]
    public async Task A_retried_then_successful_call_writes_nothing_above_debug()
    {
        var logger = new FakeLogger();
        var api = Logged(logger, Resilience.Default with { Name = "api", Backoff = Backoff.None });

        var calls = 0;
        await api.RunAsync(
            _ => ++calls == 1 ? Task.FromException<int>(new IOException()) : Task.FromResult(1),
            TestContext.Current.CancellationToken);

        Assert.All(logger.Collector.GetSnapshot(), record => Assert.True(record.Level <= LogLevel.Debug, $"{record.Id} at {record.Level}"));
        Assert.Contains(1005, Ids(logger));
    }

    // ---- The vocabulary ----

    [Fact]
    public void Event_ids_are_stable()
    {
        (int, string)[] actual =
        [
            (Log.Ids.AttemptSucceeded.Id, Log.Ids.AttemptSucceeded.Name!),
            (Log.Ids.AttemptFailed.Id, Log.Ids.AttemptFailed.Name!),
            (Log.Ids.AttemptLimited.Id, Log.Ids.AttemptLimited.Name!),
            (Log.Ids.Retrying.Id, Log.Ids.Retrying.Name!),
            (Log.Ids.CallSucceeded.Id, Log.Ids.CallSucceeded.Name!),
            (Log.Ids.CallSucceededAfterRetries.Id, Log.Ids.CallSucceededAfterRetries.Name!),
            (Log.Ids.NotRetried.Id, Log.Ids.NotRetried.Name!),
            (Log.Ids.NotRetriedFirstSighting.Id, Log.Ids.NotRetriedFirstSighting.Name!),
            (Log.Ids.Exhausted.Id, Log.Ids.Exhausted.Name!),
            (Log.Ids.DeadlineExceeded.Id, Log.Ids.DeadlineExceeded.Name!),
            (Log.Ids.RejectedDependencyUnavailable.Id, Log.Ids.RejectedDependencyUnavailable.Name!),
            (Log.Ids.RejectedBudgetExhausted.Id, Log.Ids.RejectedBudgetExhausted.Name!),
            (Log.Ids.RejectedRepeat.Id, Log.Ids.RejectedRepeat.Name!),
            (Log.Ids.BreakerOpened.Id, Log.Ids.BreakerOpened.Name!),
            (Log.Ids.BreakerHalfOpened.Id, Log.Ids.BreakerHalfOpened.Name!),
            (Log.Ids.BreakerClosed.Id, Log.Ids.BreakerClosed.Name!),
            (Log.Ids.OrphanedWork.Id, Log.Ids.OrphanedWork.Name!),
            (Log.Ids.OrphanedWorkRepeat.Id, Log.Ids.OrphanedWorkRepeat.Name!),
            (Log.Ids.NestedRetry.Id, Log.Ids.NestedRetry.Name!),
            (Log.Ids.NestedRetryRepeat.Id, Log.Ids.NestedRetryRepeat.Name!),
            (Log.Ids.PolicyResolved.Id, Log.Ids.PolicyResolved.Name!),
            (Log.Ids.PolicyClassifier.Id, Log.Ids.PolicyClassifier.Name!),
        ];

        Assert.Equal(Vocabulary.Select(row => (row.Id, row.Name)), actual);
    }

    [Fact]
    public async Task Every_event_kind_maps_to_exactly_one_event_id()
    {
        var logger = new FakeLogger();
        var listener = new LogListener(logger, new ResilienceLoggingOptions());

        foreach (var kind in Enum.GetValues<CallEventKind>())
        {
            logger.Collector.Clear();
            listener.Record(Event(kind));

            Assert.Single(logger.Collector.GetSnapshot());
            Assert.Contains(logger.Collector.LatestRecord.Id.Id, Vocabulary.Select(row => row.Id));
        }

        await Task.CompletedTask;
    }

    // ---- Flood control ----

    [Fact]
    public void A_breaker_opening_logs_once_and_its_rejections_are_suppressed()
    {
        var logger = new FakeLogger();
        var listener = new LogListener(logger, new ResilienceLoggingOptions(), new FakeTimeProvider());

        for (var i = 0; i < 100; i++)
        {
            listener.Record(Event(CallEventKind.Rejected, reason: StopReason.DependencyUnavailable));
        }

        Assert.Single(logger.Collector.GetSnapshot(), r => r.Id.Id == 1010);
        Assert.Equal(LogLevel.Warning, logger.Collector.GetSnapshot().First(r => r.Id.Id == 1010).Level);
        Assert.Equal(99, logger.Collector.GetSnapshot().Count(r => r.Id.Id == 1012 && r.Level == LogLevel.Debug));
    }

    [Fact]
    public void A_suppressed_rejection_count_reaches_the_next_warning()
    {
        var logger = new FakeLogger();
        var time = new FakeTimeProvider();
        var listener = new LogListener(logger, new ResilienceLoggingOptions(), time);

        for (var i = 0; i < 100; i++)
        {
            listener.Record(Event(CallEventKind.Rejected, reason: StopReason.DependencyUnavailable));
        }

        time.Advance(TimeSpan.FromSeconds(31));
        listener.Record(Event(CallEventKind.Rejected, reason: StopReason.DependencyUnavailable));

        FakeLogRecord[] warnings = [.. logger.Collector.GetSnapshot().Where(r => r.Id.Id == 1010)];

        Assert.Equal(2, warnings.Length);
        Assert.Equal("0", Field(warnings[0], "Suppressed"));
        Assert.Equal("99", Field(warnings[1], "Suppressed"));
    }

    [Fact]
    public void A_zero_repeat_window_warns_every_time()
    {
        var logger = new FakeLogger();
        var listener = new LogListener(
            logger,
            new ResilienceLoggingOptions { RepeatWindow = TimeSpan.Zero },
            new FakeTimeProvider());

        for (var i = 0; i < 5; i++)
        {
            listener.Record(Event(CallEventKind.Rejected, reason: StopReason.DependencyUnavailable));
        }

        Assert.Equal(5, logger.Collector.GetSnapshot().Count(r => r.Id.Id == 1010));
    }

    [Fact]
    public void A_budget_rejection_and_a_breaker_rejection_use_different_event_ids()
    {
        var logger = new FakeLogger();
        var listener = new LogListener(logger, new ResilienceLoggingOptions(), new FakeTimeProvider());

        listener.Record(Event(CallEventKind.Rejected, reason: StopReason.DependencyUnavailable));
        listener.Record(Event(CallEventKind.Rejected, reason: StopReason.BudgetExhausted));

        Assert.Equal([1010, 1011], Ids(logger));
    }

    [Fact]
    public void The_first_unretried_exception_type_warns_and_the_rest_do_not()
    {
        var logger = new FakeLogger();
        var listener = new LogListener(logger, new ResilienceLoggingOptions());

        listener.Record(Event(CallEventKind.NotRetried, error: new InvalidOperationException()));
        listener.Record(Event(CallEventKind.NotRetried, error: new InvalidOperationException()));

        Assert.Equal([1007, 1006], Ids(logger));
        Assert.Equal(LogLevel.Warning, logger.Collector.GetSnapshot()[0].Level);
        Assert.Equal(LogLevel.Debug, logger.Collector.GetSnapshot()[1].Level);
    }

    [Fact]
    public void A_second_unretried_exception_type_warns_again()
    {
        var logger = new FakeLogger();
        var listener = new LogListener(logger, new ResilienceLoggingOptions());

        listener.Record(Event(CallEventKind.NotRetried, error: new InvalidOperationException()));
        listener.Record(Event(CallEventKind.NotRetried, error: new FormatException()));

        Assert.Equal([1007, 1007], Ids(logger));
    }

    [Fact]
    public void A_permanent_result_with_no_exception_does_not_warn()
    {
        var logger = new FakeLogger();
        var listener = new LogListener(logger, new ResilienceLoggingOptions());

        // The HTTP path classifies a 404 from a response, so the event arrives with no exception at
        // all. The warning is for genuine unrecognized exception types.
        listener.Record(Event(CallEventKind.NotRetried, verdict: Verdict.Permanent));

        Assert.Equal([1006], Ids(logger));
        Assert.Equal(LogLevel.Debug, logger.Collector.LatestRecord.Level);
    }

    [Fact]
    public void Distinct_diagnostics_are_capped_at_sixty_four()
    {
        var logger = new FakeLogger();
        var listener = new LogListener(logger, new ResilienceLoggingOptions());

        for (var i = 0; i < 200; i++)
        {
            listener.Record(Event(CallEventKind.NotRetried, error: new InvalidOperationException($"#{i}"), policy: $"p{i}"));
        }

        Assert.Equal(64, logger.Collector.GetSnapshot().Count(r => r.Id.Id == 1007));
        Assert.Equal(136, logger.Collector.GetSnapshot().Count(r => r.Id.Id == 1006));
    }

    [Fact]
    public void A_nested_retry_warns_once_per_policy()
    {
        var logger = new FakeLogger();
        var listener = new LogListener(logger, new ResilienceLoggingOptions());

        listener.Record(Event(CallEventKind.NestedRetry));
        listener.Record(Event(CallEventKind.NestedRetry));
        listener.Record(Event(CallEventKind.NestedRetry, policy: "other"));

        Assert.Equal([1018, 1019, 1018], Ids(logger));
    }

    [Fact]
    public void Orphaned_work_names_the_cancellation_token_as_the_cause()
    {
        var logger = new FakeLogger();
        var listener = new LogListener(logger, new ResilienceLoggingOptions());

        listener.Record(Event(CallEventKind.OrphanedWork));

        Assert.Equal(1016, logger.Collector.LatestRecord.Id.Id);
        Assert.Contains("CancellationToken", logger.Collector.LatestRecord.Message, StringComparison.Ordinal);
        Assert.Contains("NRES001", logger.Collector.LatestRecord.Message, StringComparison.Ordinal);
    }

    // ---- Categories and attachment ----

    [Fact]
    public void The_category_is_the_policy_name_without_the_host_suffix()
    {
        var factory = new FakeLoggerProvider();
        var api = (Resilience.Http with { Name = "payments" }).WithLogging(Factory(factory));

        // What HostRegistry does: `with` preserves OnEvent, so the listener created for "payments"
        // is the one a per-host policy raises events to.
        var scoped = api with { Name = "payments:api.example.com" };

        scoped.OnEvent!(Event(CallEventKind.Rejected, policy: "payments:api.example.com", reason: StopReason.DependencyUnavailable));

        Assert.Equal("NResilience.payments", factory.Collector.LatestRecord.Category);
    }

    [Fact]
    public void The_record_carries_the_host_scoped_name()
    {
        var factory = new FakeLoggerProvider();
        var api = (Resilience.Http with { Name = "payments" }).WithLogging(Factory(factory));

        (api with { Name = "payments:api.example.com" }).OnEvent!(
            Event(CallEventKind.Rejected, policy: "payments:api.example.com", reason: StopReason.DependencyUnavailable));

        Assert.Equal("payments:api.example.com", Field(factory.Collector.LatestRecord, "Policy"));
    }

    [Fact]
    public void CategoryFor_names_the_prefix_for_an_unnamed_policy()
    {
        Assert.Equal("NResilience", ResilienceLogging.CategoryFor(null));
        Assert.Equal("NResilience", ResilienceLogging.CategoryFor(string.Empty));
        Assert.Equal("NResilience.api", ResilienceLogging.CategoryFor("api"));
    }

    [Fact]
    public void WithLogging_chains_after_an_existing_listener()
    {
        var seen = new List<CallEventKind>();
        var logger = new FakeLogger();

        var api = (Resilience.Default with { Name = "api", OnEvent = e => seen.Add(e.Kind) }).WithLogging(logger);

        api.OnEvent!(Event(CallEventKind.NestedRetry));

        Assert.Equal([CallEventKind.NestedRetry], seen);
        Assert.NotEmpty(logger.Collector.GetSnapshot());
    }

    [Fact]
    public void WithLogging_twice_attaches_one_listener()
    {
        var logger = new FakeLogger();
        var api = (Resilience.Default with { Name = "api" }).WithLogging(logger).WithLogging(logger);

        api.OnEvent!(Event(CallEventKind.NestedRetry));

        Assert.Single(logger.Collector.GetSnapshot());
    }

    [Fact]
    public void WithLogging_off_attaches_nothing()
    {
        var api = (Resilience.Default with { Name = "api" })
            .WithLogging(new FakeLogger(), new ResilienceLoggingOptions { Profile = ResilienceLogProfile.Off });

        Assert.Null(api.OnEvent);
    }

    // ---- Levels ----

    [Fact]
    public void Verbose_raises_traffic_records_to_information()
    {
        var logger = new FakeLogger();
        var listener = new LogListener(logger, new ResilienceLoggingOptions { Profile = ResilienceLogProfile.Verbose });

        listener.Record(Event(CallEventKind.Attempt, error: new IOException(), verdict: Verdict.Transient));
        listener.Record(Event(CallEventKind.Succeeded, attempt: 1, verdict: Verdict.Ok, reason: StopReason.Succeeded));

        Assert.All(logger.Collector.GetSnapshot(), record => Assert.Equal(LogLevel.Information, record.Level));
    }

    [Fact]
    public void A_level_delegate_overrides_the_profile()
    {
        var logger = new FakeLogger();
        var listener = new LogListener(
            logger,
            new ResilienceLoggingOptions
            {
                Level = (id, _) => id.Id == 1004 ? LogLevel.Error : null,
            });

        listener.Record(Event(CallEventKind.Succeeded, attempt: 1, verdict: Verdict.Ok, reason: StopReason.Succeeded));

        Assert.Equal(LogLevel.Error, logger.Collector.LatestRecord.Level);
    }

    [Fact]
    public void A_level_delegate_returning_none_drops_the_record()
    {
        var logger = new FakeLogger();
        var listener = new LogListener(
            logger,
            new ResilienceLoggingOptions { Level = (_, _) => LogLevel.None });

        foreach (var kind in Enum.GetValues<CallEventKind>())
        {
            listener.Record(Event(kind));
        }

        Assert.Empty(logger.Collector.GetSnapshot());
    }

    [Fact]
    public void Stack_traces_are_attached_to_terminal_records_only()
    {
        var logger = new FakeLogger();
        var listener = new LogListener(logger, new ResilienceLoggingOptions());
        var error = new IOException();

        listener.Record(Event(CallEventKind.Attempt, error: error, verdict: Verdict.Transient));
        listener.Record(Event(CallEventKind.Exhausted, error: error, verdict: Verdict.Transient, reason: StopReason.AttemptsExhausted));

        Assert.Null(logger.Collector.GetSnapshot()[0].Exception);
        Assert.Same(error, logger.Collector.GetSnapshot()[1].Exception);
    }

    [Fact]
    public void IncludeStackTracesOnRetry_attaches_the_exception_to_per_attempt_records()
    {
        var logger = new FakeLogger();
        var listener = new LogListener(logger, new ResilienceLoggingOptions { IncludeStackTracesOnRetry = true });
        var error = new IOException();

        listener.Record(Event(CallEventKind.Attempt, error: error, verdict: Verdict.Transient));

        Assert.Same(error, logger.Collector.LatestRecord.Exception);
    }

    // ---- Behavior through the executor ----

    [Fact]
    public async Task A_cancelled_call_logs_nothing()
    {
        var logger = new FakeLogger();
        var api = Logged(logger, Resilience.Default with { Name = "api" });

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => api.RunAsync(_ => Task.FromResult(1), cts.Token).AsTask());

        // Caller cancellation rethrows before any event is raised, so a cancelled call is silent by
        // construction. Worth pinning, because "my cancelled request vanished from the logs" is
        // otherwise read as a bug.
        Assert.Empty(logger.Collector.GetSnapshot());
    }

    [Fact]
    public async Task A_listener_that_throws_does_not_fail_the_call()
    {
        var api = Resilience.Default with
        {
            Name = "api",
            OnEvent = _ => throw new InvalidOperationException("the provider fell over"),
        };

        Assert.Equal(1, await api.RunAsync(_ => Task.FromResult(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_limited_attempt_reports_its_own_event_id()
    {
        var logger = new FakeLogger();
        var api = Logged(logger, Resilience.Default with { Name = "api", Attempts = 1 });

        await Assert.ThrowsAnyAsync<Exception>(() => api.RunAsync(
            _ => throw new RateLimitedException("quota", TimeSpan.FromSeconds(1)),
            TestContext.Current.CancellationToken).AsTask());

        Assert.Contains(1002, Ids(logger));
    }

    // ---- DI ----

    [Fact]
    public void A_registered_policy_logs_without_any_extra_call()
    {
        var provider = new FakeLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Trace));
        services.AddResilience("api", Resilience.Http);

        var api = services.BuildServiceProvider().GetRequiredService<IResiliencePolicies>()["api"];

        api.OnEvent!(Event(CallEventKind.Rejected, policy: "api", reason: StopReason.DependencyUnavailable));

        Assert.Contains(1010, provider.Collector.GetSnapshot().Select(r => r.Id.Id));
        Assert.Equal("NResilience.api", provider.Collector.GetSnapshot().First(r => r.Id.Id == 1010).Category);
    }

    [Fact]
    public void A_container_without_logging_registers_and_runs()
    {
        var services = new ServiceCollection();
        services.AddResilience("api", Resilience.Http);

        var api = services.BuildServiceProvider().GetRequiredService<IResiliencePolicies>()["api"];

        // Telemetry is attached, logging is not, and nothing threw.
        Assert.NotNull(api.OnEvent);
    }

    [Fact]
    public void An_explicit_WithLogging_beats_the_automatic_one()
    {
        var mine = new FakeLogger();
        var theirs = new FakeLoggerProvider();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(theirs).SetMinimumLevel(LogLevel.Trace));
        services.AddResilience("api", Resilience.Http, configure: p => p.WithLogging(mine));

        var api = services.BuildServiceProvider().GetRequiredService<IResiliencePolicies>()["api"];
        api.OnEvent!(Event(CallEventKind.NestedRetry, policy: "api"));

        Assert.Single(mine.Collector.GetSnapshot(), r => r.Id.Id == 1018);
        Assert.DoesNotContain(1018, theirs.Collector.GetSnapshot().Select(r => r.Id.Id));
    }

    [Fact]
    public void Logging_off_in_a_section_attaches_no_listener()
    {
        var provider = new FakeLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Trace));
        services.AddResilience("api", o =>
        {
            o.Logging = "Off";
            o.Telemetry = false;
        });

        var api = services.BuildServiceProvider().GetRequiredService<IResiliencePolicies>()["api"];

        Assert.Null(api.OnEvent);
    }

    [Fact]
    public void An_unknown_logging_value_names_the_valid_ones()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddResilience("api", o => o.Logging = "Verbse");

        var thrown = Assert.Throws<ResilienceConfigurationException>(
            () => services.BuildServiceProvider().GetRequiredService<IResiliencePolicies>()["api"]);

        Assert.Contains("Off, Default or Verbose", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("Verbse", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_process_wide_profile_reaches_every_policy()
    {
        var provider = new FakeLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Trace));
        services.AddResilienceLogging(o => o.Profile = ResilienceLogProfile.Verbose);
        services.AddResilience("api", Resilience.Http);

        var api = services.BuildServiceProvider().GetRequiredService<IResiliencePolicies>()["api"];
        api.OnEvent!(Event(CallEventKind.Succeeded, policy: "api", attempt: 1, verdict: Verdict.Ok, reason: StopReason.Succeeded));

        Assert.Equal(
            LogLevel.Information,
            provider.Collector.GetSnapshot().First(r => r.Id.Id == 1004).Level);
    }

    // ---- Provenance ----

    [Fact]
    public void A_resolved_policy_reports_its_effective_shape()
    {
        var provider = new FakeLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Debug));
        services.AddResilience("api", o =>
        {
            o.Preset = "Http";
            o.Attempts = 4;
            o.Deadline = TimeSpan.FromSeconds(20);
        });

        _ = services.BuildServiceProvider().GetRequiredService<IResiliencePolicies>()["api"];

        var resolved = provider.Collector.GetSnapshot().First(r => r.Id.Id == 1020);

        Assert.Equal(LogLevel.Debug, resolved.Level);
        Assert.Contains("4 attempts", resolved.Message, StringComparison.Ordinal);
        Assert.Contains("deadline 20s", resolved.Message, StringComparison.Ordinal);
        Assert.Contains("telemetry on", resolved.Message, StringComparison.Ordinal);
        Assert.Contains("logging Default", resolved.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_classifier_dump_is_not_built_when_trace_is_off()
    {
        var provider = new FakeLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Debug));
        services.AddResilience("api", Resilience.Http);

        _ = services.BuildServiceProvider().GetRequiredService<IResiliencePolicies>()["api"];

        Assert.DoesNotContain(1021, provider.Collector.GetSnapshot().Select(r => r.Id.Id));
    }

    [Fact]
    public void The_classifier_dump_is_written_at_trace()
    {
        var provider = new FakeLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Trace));
        services.AddResilience("api", Resilience.Http);

        _ = services.BuildServiceProvider().GetRequiredService<IResiliencePolicies>()["api"];

        var dump = provider.Collector.GetSnapshot().First(r => r.Id.Id == 1021);

        Assert.Equal(LogLevel.Trace, dump.Level);
        Assert.Contains("exception", dump.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reload_reports_the_new_shape_and_keeps_the_breaker()
    {
        var provider = new FakeLoggerProvider();
        var manager = new ConfigurationManager();
        manager.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Resilience:api:Attempts"] = "3",
            ["Resilience:api:Breaker:ConsecutiveFailures"] = "2",
        });

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Debug));
        services.AddResilience(manager.GetSection("Resilience"));

        var policies = services.BuildServiceProvider().GetRequiredService<IResiliencePolicies>();
        var first = policies["api"].Breaker!;

        manager.Sources.Clear();
        manager.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Resilience:api:Attempts"] = "5",
            ["Resilience:api:Breaker:ConsecutiveFailures"] = "2",
        });

        var reloaded = policies["api"];

        Assert.Same(first, reloaded.Breaker);
        Assert.Equal(
            ["3 attempts", "5 attempts"],
            provider.Collector.GetSnapshot()
                .Where(r => r.Id.Id == 1020)
                .Select(r => r.Message[(r.Message.IndexOf("resolved: ", StringComparison.Ordinal) + 10)..].Split(',')[0]));
    }

    // ---- Helpers ----

    private static Resilience Logged(FakeLogger logger, Resilience policy) =>
        policy.WithLogging(logger);

    private static ILoggerFactory Factory(FakeLoggerProvider provider) =>
        LoggerFactory.Create(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Trace));

    private static int[] Ids(FakeLogger logger) =>
        [.. logger.Collector.GetSnapshot().Select(record => record.Id.Id)];

    private static string? Field(FakeLogRecord record, string name) =>
        record.StructuredState?.FirstOrDefault(pair => pair.Key == name).Value;

    /// <summary>
    /// A <see cref="CallEvent"/> of a given kind, shaped enough for the listener to route it. The
    /// executor's own constructor is internal, so the recorder is how a test gets one.
    /// </summary>
    private static CallEvent Event(
        CallEventKind kind,
        string? policy = "api",
        int attempt = 1,
        Verdict? verdict = null,
        Exception? error = null,
        StopReason? reason = null)
    {
        var effective = verdict ?? (kind switch
        {
            CallEventKind.Rejected => Verdict.Transient,
            CallEventKind.NotRetried => Verdict.Permanent,
            CallEventKind.Exhausted => Verdict.Transient,
            CallEventKind.DeadlineExceeded => Verdict.Transient,
            _ => Verdict.Ok,
        });

        var effectiveReason = reason ?? (kind switch
        {
            CallEventKind.Rejected => StopReason.DependencyUnavailable,
            CallEventKind.NotRetried => StopReason.Permanent,
            CallEventKind.Succeeded => StopReason.Succeeded,
            CallEventKind.DeadlineExceeded => StopReason.DeadlineExceeded,
            CallEventKind.Exhausted => StopReason.AttemptsExhausted,
            _ => null,
        });

        // The constructor is internal, and NResilience grants this assembly access - so a test can
        // hand the listener one event of each kind without driving the executor into every state.
        return new CallEvent(
            kind,
            policy,
            attempt,
            effective,
            TimeSpan.FromMilliseconds(12),
            kind is CallEventKind.Retrying or CallEventKind.Rejected ? TimeSpan.FromMilliseconds(5) : null,
            error,
            null,
            effectiveReason);
    }
}
