using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.Logging.Testing;
using NResilience.Extensions;
using NResilience.Extensions.Internal;
using NResilience.Http;
using NResilience.Testing;

namespace NResilience.IntegrationTests;

/// <summary>
///     All telemetry surfaces in one call, over a real socket.
///     <para>
///         The behavioural suite exercises the meter, the logger, the activity source, and the event
///         recorder each in isolation. No single test runs a retried HTTP call with all four wired and
///         asserts they agree. This is that test, over a real loopback socket.
///     </para>
/// </summary>
public sealed class TelemetryCompositionTests
{
    /// <summary>
    ///     One retried call produces consistent meter, logger, activity, and event data. The server
    ///     serves 503 then 200. All four telemetry surfaces are wired and assert on the same call.
    /// </summary>
    [Fact]
    public async Task One_retried_call_produces_consistent_telemetry_across_all_surfaces()
    {
        await using var server = await LoopbackHttp.StartAsync(
            new LoopbackResponse(HttpStatusCode.ServiceUnavailable),
            new LoopbackResponse(HttpStatusCode.OK, "ok"u8.ToArray()));

        var events = new EventRecorder();
        var spans = new List<Activity>();
        var logger = new FakeLogger();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ResilienceTelemetry.ActivitySourceName,
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };

        ActivitySource.AddActivityListener(activityListener);

        using var recording = new MeterRecording();

        // Wire all four surfaces onto one policy: events, logging, meter, and the activity source
        // (the latter two via WithTelemetry, which chains the meter listener onto OnEvent and which
        // the telemetry handler picks up through the activity source).
        var policy = (Resilience.Http with
        {
            Backoff = Backoff.None,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Deadline = Timeout.InfiniteTimeSpan,
            OnEvent = events.Record,
        }).WithLogging(logger).WithTelemetry();

        // The telemetry handler starts the span. Attach it as an outer delegating handler around
        // the resilience handler.
        var telemetryHandler = new ResilienceTelemetryHandler("test");
        telemetryHandler.InnerHandler = new ResilienceHandler(new SocketsHttpHandler(), policy);
        using var client = new HttpClient(telemetryHandler, true);

        using var response = await client.GetAsync(server.BaseUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, server.RequestCount);

        // The event recorder saw the canonical sequence.
        Assert.Equal(
            [CallEventKind.Attempt, CallEventKind.Retrying, CallEventKind.Attempt, CallEventKind.Succeeded],
            events.Kinds);

        // The span covers the whole retry sequence, with two attempt events. Filter by the
        // client name to exclude spans from other tests running in parallel.
        var span = Assert.Single(spans, s => s.DisplayName == "resilience test");
        Assert.Equal("succeeded", span.GetTagItem("nresilience.outcome"));
        Assert.Equal(2, span.Events.Count(e => e.Name == "nresilience.attempt"));

        // The meter counted one call and two attempts. Scoped to this test's own server: the
        // listener is attached to the process-wide meter, and "http:" alone would also sum every
        // host-scoped policy that another test running in parallel happens to drive.
        var policyName = HostScopedName(server);
        Assert.Equal(1, recording.CallsForPrefix(policyName));
        Assert.Equal(2, recording.AttemptsForPrefix(policyName));

        // The logger wrote something - the log is not empty.
        Assert.NotEmpty(logger.Collector.GetSnapshot());
    }

    /// <summary>
    ///     Telemetry is a switch, and turning it off leaves no span, no meter, no log. The same call
    ///     without <c>WithTelemetry</c> or <c>WithLogging</c> - all three surfaces are empty.
    /// </summary>
    [Fact]
    public async Task Telemetry_off_leaves_no_span_no_meter_no_log()
    {
        await using var server = await LoopbackHttp.StartAsync(
            new LoopbackResponse(HttpStatusCode.OK, "ok"u8.ToArray()));

        var spans = new List<Activity>();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ResilienceTelemetry.ActivitySourceName,
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };

        ActivitySource.AddActivityListener(activityListener);

        using var recording = new MeterRecording();

        // No WithLogging, no WithTelemetry - the surfaces stay empty.
        using var client = ResilienceHttp.CreateClient(Resilience.Http with
        {
            Backoff = Backoff.None,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Deadline = Timeout.InfiniteTimeSpan,
        });

        using var response = await client.GetAsync(server.BaseUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(spans);
        Assert.Equal(0, recording.CallsForPrefix(HostScopedName(server)));
    }

    /// <summary>
    ///     A failed call's terminal event is consistent across all surfaces. The server serves 503
    ///     forever. The meter, the span, and the event recorder all agree the call was exhausted.
    /// </summary>
    [Fact]
    public async Task A_failed_calls_terminal_event_is_consistent_across_all_surfaces()
    {
        await using var server = await LoopbackHttp.StartAsync(
            new LoopbackResponse(HttpStatusCode.ServiceUnavailable));

        var events = new EventRecorder();
        var spans = new List<Activity>();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ResilienceTelemetry.ActivitySourceName,
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };

        ActivitySource.AddActivityListener(activityListener);

        using var recording = new MeterRecording();

        var policy = (Resilience.Http with
        {
            Backoff = Backoff.None,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Deadline = Timeout.InfiniteTimeSpan,
            OnEvent = events.Record,
        }).WithTelemetry();

        var telemetryHandler = new ResilienceTelemetryHandler("test");
        telemetryHandler.InnerHandler = new ResilienceHandler(new SocketsHttpHandler(), policy);
        using var client = new HttpClient(telemetryHandler, true);

        using var response = await client.GetAsync(server.BaseUri);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, server.RequestCount);

        // The event recorder's terminal event is Exhausted.
        Assert.True(events.Contains(CallEventKind.Exhausted), $"Expected Exhausted, got: {events}.");

        // The span's outcome is attempts_exhausted. Filter by the client name to exclude spans
        // from other tests running in parallel.
        var span = Assert.Single(spans, s => s.DisplayName == "resilience test");
        Assert.Equal("attempts_exhausted", span.GetTagItem("nresilience.outcome"));

        // The meter counted one call and three attempts, scoped to this test's own server.
        var policyName = HostScopedName(server);
        Assert.Equal(1, recording.CallsForPrefix(policyName));
        Assert.Equal(3, recording.AttemptsForPrefix(policyName));
    }

    /// <summary>
    ///     Collects what the NResilience meter recorded, filtered by policy name. A trimmed version
    ///     of <c>MetricsTests.Recording</c> - enough to assert on the call and attempt counters for
    ///     one policy, excluding other tests running in parallel.
    /// </summary>
    /// <summary>
    ///     The name <see cref="ResilienceHandler" /> scopes its policy to for one loopback server:
    ///     the policy's own name and the authority, which includes the ephemeral port and is
    ///     therefore unique to the test that started the server.
    /// </summary>
    private static string HostScopedName(LoopbackHttp server) => $"http:{server.BaseUri.Authority}";

    private sealed class MeterRecording : IDisposable
    {
        private readonly Dictionary<string, long> _attemptsByPolicy = [];
        private readonly Dictionary<string, long> _callsByPolicy = [];
        private readonly object _gate = new();
        private readonly MeterListener _listener = new();

        public MeterRecording()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, ResilienceTelemetry.Meter))
                    listener.EnableMeasurementEvents(instrument);
            };

            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                var policy = "(unnamed)";

                foreach (var tag in tags)
                {
                    if (tag.Key == "nresilience.policy")
                    {
                        policy = (string)tag.Value!;
                        break;
                    }
                }

                lock (_gate)
                {
                    if (instrument.Name == "nresilience.calls")
                        _callsByPolicy[policy] = _callsByPolicy.GetValueOrDefault(policy) + value;
                    else if (instrument.Name == "nresilience.attempts")
                        _attemptsByPolicy[policy] = _attemptsByPolicy.GetValueOrDefault(policy) + value;
                }
            });

            _listener.Start();
        }

        public long Calls => CallsFor("http");
        public long Attempts => AttemptsFor("http");

        public void Dispose() => _listener.Dispose();

        public long CallsFor(string policy)
        {
            lock (_gate)
            {
                return _callsByPolicy.GetValueOrDefault(policy);
            }
        }

        public long AttemptsFor(string policy)
        {
            lock (_gate)
            {
                return _attemptsByPolicy.GetValueOrDefault(policy);
            }
        }

        public long CallsForPrefix(string prefix)
        {
            lock (_gate)
            {
                return _callsByPolicy.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).Sum(kv => kv.Value);
            }
        }

        public long AttemptsForPrefix(string prefix)
        {
            lock (_gate)
            {
                return _attemptsByPolicy.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).Sum(kv => kv.Value);
            }
        }
    }
}
