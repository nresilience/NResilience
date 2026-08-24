using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NResilience.Extensions;
using NResilience.Testing;

namespace NResilience.Docs;

/// <summary>The telemetry surface: one event type, one delegate, and the meter behind them.</summary>
public sealed class TelemetryDocs
{
    private readonly ILogger _logger = NullLogger.Instance;

    [Fact]
    public async Task A_listener_is_a_lambda()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var calls = Sequence.For<int>().Throws(new IOException()).Returns(1);

        // <snippet:telemetry-listener>
        var api = Resilience.Http with
        {
            Name = "payments",
            Backoff = Backoff.None,
            OnEvent = e => _logger.LogInformation(
                "{Policy} {Kind} attempt {Attempt}: {Verdict} in {Ms}ms",
                e.PolicyName, e.Kind, e.AttemptNumber, e.Verdict.Kind, e.Duration.TotalMilliseconds),
        };
        // </snippet:telemetry-listener>

        Assert.Equal(1, await api.RunAsync(attempt => calls.NextAsync(attempt), cancellationToken));
    }

    [Fact]
    public async Task Every_call_ends_with_exactly_one_terminal_event()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var events = new EventRecorder();
        var calls = Sequence.For<int>().Throws(new IOException()).Returns(1);

        // <snippet:telemetry-recorder>
        var api = Resilience.Default with { Backoff = Backoff.None, OnEvent = events.Record };

        await api.RunAsync(attempt => calls.NextAsync(attempt), cancellationToken);

        // Attempt, Retrying, Attempt, Succeeded
        Console.WriteLine(string.Join(", ", events.Kinds));
        // </snippet:telemetry-recorder>

        Assert.Equal(
            [CallEventKind.Attempt, CallEventKind.Retrying, CallEventKind.Attempt, CallEventKind.Succeeded],
            events.Kinds);
    }

    [Fact]
    public void A_hand_built_policy_opts_into_the_meter()
    {
        // <snippet:telemetry-with-telemetry>
        // A policy registered in a container is instrumented for you. A policy in a static field
        // is not - this says it.
        var api = (Resilience.Http with { Name = "payments" }).WithTelemetry();
        // </snippet:telemetry-with-telemetry>

        Assert.NotNull(api.OnEvent);
    }

    [Fact]
    public async Task An_event_prints_itself()
    {
        var events = new EventRecorder();
        var api = Resilience.Default with { Name = "api", Attempts = 1, OnEvent = events.Record };

        await api.RunAsync(attempt => Task.FromResult(1), TestContext.Current.CancellationToken);

        // <snippet:telemetry-tostring>
        // [PolicyName] Kind #N VerdictKind ExceptionType (duration) +delay
        Console.WriteLine(events[0]);   // [api] Attempt #1 Ok (0.1ms)
        // </snippet:telemetry-tostring>

        Assert.StartsWith("[api] Attempt #1 Ok", events[0].ToString(), StringComparison.Ordinal);
    }
}
