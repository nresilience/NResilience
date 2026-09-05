using System.Net;
using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     Deadline propagation: the inbound half, where a call is clamped by the deadline it inherited,
///     and the outbound half, where each attempt tells the peer how long this side is waiting.
///     <para>
///         The claim being tested is that the deadline stops being a per-process bound and becomes a
///         property of the call graph, without any new concept entering the model: the effective
///         deadline is <c>min(configured, inherited)</c>, and everything downstream of it - the attempt
///         ceiling, the backoff that will not fit, the reported <c>Deadline</c> on the exception - reads
///         the clamped value.
///     </para>
/// </summary>
public sealed class DeadlinePropagationTests
{
    // ---- The ambient deadline itself ----

    [Fact]
    public void An_inbound_deadline_decays_with_the_clock()
    {
        var time = new FakeTimeProvider();

        Assert.Null(ResilienceDeadline.Remaining);

        using var scope = ResilienceDeadline.Begin(TimeSpan.FromSeconds(10), time);
        Assert.Equal(TimeSpan.FromSeconds(10), ResilienceDeadline.Remaining);

        time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(TimeSpan.FromSeconds(6), ResilienceDeadline.Remaining);

        // Expired is zero, never negative: "no time left" is the fact, and how far past it we are is
        // not something any caller should have to subtract.
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.Zero, ResilienceDeadline.Remaining);
    }

    [Fact]
    public void A_scope_restores_the_one_it_replaced()
    {
        var time = new FakeTimeProvider();

        using (ResilienceDeadline.Begin(TimeSpan.FromSeconds(10), time))
        {
            using (ResilienceDeadline.Begin(TimeSpan.FromSeconds(2), time))
            {
                Assert.Equal(TimeSpan.FromSeconds(2), ResilienceDeadline.Remaining);
            }

            Assert.Equal(TimeSpan.FromSeconds(10), ResilienceDeadline.Remaining);
        }

        Assert.Null(ResilienceDeadline.Remaining);
    }

    [Fact]
    public void An_infinite_deadline_is_no_deadline_rather_than_an_unbounded_one()
    {
        var time = new FakeTimeProvider();

        using var outer = ResilienceDeadline.Begin(TimeSpan.FromSeconds(10), time);
        using var inner = ResilienceDeadline.Begin(Timeout.InfiniteTimeSpan, time);

        Assert.Null(ResilienceDeadline.Remaining);
    }

    [Theory]
    [InlineData("200", 200)]
    [InlineData("1", 1)]
    public void A_header_of_whole_milliseconds_reads_as_a_deadline(string value, int milliseconds)
    {
        Assert.True(ResilienceDeadline.TryParse(value, out var remaining));
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), remaining);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" 200")]
    [InlineData("200ms")]
    [InlineData("100m")] // gRPC's format, which this is deliberately not.
    [InlineData("2.5")]
    [InlineData("-5")]
    [InlineData("0")]
    [InlineData("99999999999")]
    public void Anything_else_is_no_deadline_at_all(string? value)
    {
        Assert.False(ResilienceDeadline.TryParse(value, out var remaining));
        Assert.Equal(default, remaining);
    }

    [Fact]
    public void What_goes_out_is_whole_milliseconds_rounded_down_but_never_to_zero()
    {
        Assert.Equal("1500", ResilienceDeadline.Format(TimeSpan.FromMilliseconds(1500)));
        Assert.Equal("1", ResilienceDeadline.Format(TimeSpan.FromMilliseconds(1.9)));
        Assert.Equal("1", ResilienceDeadline.Format(TimeSpan.FromTicks(1)));
        Assert.Null(ResilienceDeadline.Format(TimeSpan.Zero));
        Assert.Null(ResilienceDeadline.Format(Timeout.InfiniteTimeSpan));
    }

    // ---- The executor's clamp ----

    [Fact]
    public async Task The_tighter_of_the_two_deadlines_is_the_one_that_stops_the_call()
    {
        var time = new FakeTimeProvider();
        var attempts = 0;

        var policy = Resilience.Default with
        {
            Attempts = 3,
            Deadline = TimeSpan.FromSeconds(30),
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Backoff = Backoff.None,
            Budget = RetryBudget.None,
            UseAmbientDeadline = true,
            Classifier = Classifier.Http,
            Time = time,
        };

        using var scope = ResilienceDeadline.Begin(TimeSpan.FromMilliseconds(200), time);

        var caught = await Assert.ThrowsAsync<DeadlineExceededException>(async () =>
            await policy.RunAsync(_ =>
            {
                attempts++;
                time.Advance(TimeSpan.FromMilliseconds(500));
                throw new HttpRequestException("transient");
            }));

        // One attempt, and the deadline it is reported against is the inherited one - not the 30 s the
        // policy was configured with, which would tell an operator to look in the wrong place.
        Assert.Equal(1, attempts);
        Assert.Equal(TimeSpan.FromMilliseconds(200), caught.Deadline);
    }

    [Fact]
    public async Task A_policy_that_did_not_ask_does_not_inherit()
    {
        var time = new FakeTimeProvider();
        var attempts = 0;

        var policy = TestPolicy.WithClock(time) with { Deadline = TimeSpan.FromSeconds(30), Classifier = Classifier.Http };

        using var scope = ResilienceDeadline.Begin(TimeSpan.FromMilliseconds(200), time);

        var result = await policy.TryRunAsync(_ =>
        {
            attempts++;
            time.Advance(TimeSpan.FromMilliseconds(500));
            throw new HttpRequestException("transient");
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(StopReason.AttemptsExhausted, result.Reason);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task A_looser_inbound_deadline_leaves_the_policy_where_it_was()
    {
        var time = new FakeTimeProvider();

        var policy = TestPolicy.WithClock(time) with
        {
            Deadline = TimeSpan.FromSeconds(1),
            UseAmbientDeadline = true,
            Classifier = Classifier.Http,
        };

        using var scope = ResilienceDeadline.Begin(TimeSpan.FromHours(1), time);

        var caught = await Assert.ThrowsAsync<DeadlineExceededException>(async () =>
            await policy.RunAsync(_ =>
            {
                time.Advance(TimeSpan.FromSeconds(2));
                throw new HttpRequestException("transient");
            }));

        Assert.Equal(TimeSpan.FromSeconds(1), caught.Deadline);
    }

    [Fact]
    public async Task An_expired_inbound_deadline_stops_the_call_before_it_starts()
    {
        var time = new FakeTimeProvider();
        var ran = false;

        var policy = TestPolicy.WithClock(time) with { UseAmbientDeadline = true };

        using var scope = ResilienceDeadline.Begin(TimeSpan.FromMilliseconds(50), time);
        time.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<DeadlineExceededException>(async () =>
            await policy.RunAsync(_ =>
            {
                ran = true;
                return Task.FromResult(1);
            }));

        // The dependency is never asked for an answer nobody is waiting for. This is the whole point of
        // the feature: without it the call would have run three attempts against a caller who has gone.
        Assert.False(ran);
    }

    [Fact]
    public async Task Passthrough_is_off_the_table_for_a_policy_that_inherits()
    {
        var time = new FakeTimeProvider();
        var ran = false;

        // Every bound off - this is Resilience.None, which normally hands back the callback's own task
        // without an executor frame at all.
        var policy = Resilience.None with { UseAmbientDeadline = true, Time = time };

        using var scope = ResilienceDeadline.Begin(TimeSpan.FromMilliseconds(50), time);
        time.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<DeadlineExceededException>(async () =>
            await policy.RunAsync(_ =>
            {
                ran = true;
                return Task.FromResult(1);
            }));

        Assert.False(ran);
    }

    [Fact]
    public async Task The_admission_path_inherits_too()
    {
        var time = new FakeTimeProvider();
        var ran = false;

        var policy = TestPolicy.WithClock(time) with
        {
            UseAmbientDeadline = true,
            Admit = _ => Task.FromResult(Verdict.Ok),
        };

        using var scope = ResilienceDeadline.Begin(TimeSpan.FromMilliseconds(50), time);
        time.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<DeadlineExceededException>(async () =>
            await policy.RunAsync(_ =>
            {
                ran = true;
                return Task.FromResult(1);
            }));

        Assert.False(ran);
    }

    [Fact]
    public async Task The_hedged_path_inherits_too()
    {
        var time = new FakeTimeProvider();
        var ran = false;

        var policy = TestPolicy.WithClock(time) with
        {
            UseAmbientDeadline = true,
            Hedge = Hedge.At(),
        };

        using var scope = ResilienceDeadline.Begin(TimeSpan.FromMilliseconds(50), time);
        time.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<DeadlineExceededException>(async () =>
            await policy.RunAsync(_ =>
            {
                ran = true;
                return Task.FromResult(1);
            }));

        Assert.False(ran);
    }

    // ---- The outbound header ----

    [Fact]
    public async Task Nothing_goes_on_the_wire_unless_asked()
    {
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);

        using var client = new HttpClient(new ResilienceHandler(transport, TestPolicy.InstantHttp));
        using var response = await client.GetAsync(new Uri("https://api.test/thing"));

        Assert.DoesNotContain(ResilienceDeadline.Header, transport.Requests[0].Headers.Select(h => h.Key));
    }

    [Fact]
    public async Task What_the_peer_is_told_is_this_attempts_own_ceiling()
    {
        var time = new FakeTimeProvider();
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);

        var policy = TestPolicy.WithClock(time) with
        {
            Deadline = TimeSpan.FromSeconds(30),
            AttemptTimeout = TimeSpan.FromSeconds(3),
            Classifier = Classifier.Http,
        };

        using var client = new HttpClient(new ResilienceHandler(transport, policy, new HttpResilienceOptions { PropagateDeadline = true }));
        using var response = await client.GetAsync(new Uri("https://api.test/thing"));

        // Three seconds, not thirty: this side abandons the attempt at its own ceiling, so telling the
        // peer about the whole deadline would invite it to work for 27 s nobody waits through.
        Assert.Equal("3000", Header(transport, 0));
    }

    [Fact]
    public async Task Every_attempt_is_told_what_is_left_rather_than_what_there_was()
    {
        var time = new FakeTimeProvider();

        // Each attempt burns two seconds of the ten the deadline allows.
        var transport = new AdvancingTransport(time, TimeSpan.FromSeconds(2), HttpStatusCode.ServiceUnavailable);

        var policy = TestPolicy.WithClock(time) with
        {
            Attempts = 3,
            Deadline = TimeSpan.FromSeconds(10),
            Classifier = Classifier.Http,
        };

        using var client = new HttpClient(new ResilienceHandler(transport, policy, new HttpResilienceOptions { PropagateDeadline = true }));
        using var response = await client.GetAsync(new Uri("https://api.test/thing"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(["10000", "8000", "6000"], transport.Deadlines);
    }

    [Fact]
    public async Task The_header_carries_the_inherited_deadline_when_that_is_the_tighter_one()
    {
        var time = new FakeTimeProvider();
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);

        var policy = TestPolicy.WithClock(time) with
        {
            Deadline = TimeSpan.FromSeconds(30),
            Classifier = Classifier.Http,
            UseAmbientDeadline = true,
        };

        using var scope = ResilienceDeadline.Begin(TimeSpan.FromMilliseconds(500), time);

        using var client = new HttpClient(new ResilienceHandler(transport, policy, new HttpResilienceOptions { PropagateDeadline = true }));
        using var response = await client.GetAsync(new Uri("https://api.test/thing"));

        // The number on the wire is what this side is actually waiting for, which is what makes the
        // deadline true across a hop rather than only within one.
        Assert.Equal("500", Header(transport, 0));
    }

    [Fact]
    public async Task The_header_name_is_the_callers_to_choose()
    {
        var time = new FakeTimeProvider();
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);

        var policy = TestPolicy.WithClock(time) with { Deadline = TimeSpan.FromSeconds(4), Classifier = Classifier.Http };
        var options = new HttpResilienceOptions { PropagateDeadline = true, DeadlineHeader = "X-Budget" };

        using var client = new HttpClient(new ResilienceHandler(transport, policy, options));
        using var response = await client.GetAsync(new Uri("https://api.test/thing"));

        Assert.Equal("4000", Header(transport, 0, "X-Budget"));
        Assert.DoesNotContain(ResilienceDeadline.Header, transport.Requests[0].Headers.Select(h => h.Key));
    }

    [Fact]
    public async Task An_unbounded_call_has_nothing_to_say()
    {
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);

        // TestPolicy.InstantHttp has neither a deadline nor an attempt ceiling.
        using var client = new HttpClient(
            new ResilienceHandler(transport, TestPolicy.InstantHttp, new HttpResilienceOptions { PropagateDeadline = true }));

        using var response = await client.GetAsync(new Uri("https://api.test/thing"));

        Assert.DoesNotContain(ResilienceDeadline.Header, transport.Requests[0].Headers.Select(h => h.Key));
    }

    [Fact]
    public async Task Ours_replaces_whatever_the_caller_wrote()
    {
        var time = new FakeTimeProvider();
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);

        var policy = TestPolicy.WithClock(time) with { Deadline = TimeSpan.FromSeconds(4), Classifier = Classifier.Http };

        using var client = new HttpClient(new ResilienceHandler(transport, policy, new HttpResilienceOptions { PropagateDeadline = true }));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.test/thing"));
        request.Headers.TryAddWithoutValidation(ResilienceDeadline.Header, "999999");

        using var response = await client.SendAsync(request);

        Assert.Equal("4000", Header(transport, 0));
    }

    private static string? Header(ScriptedHttpHandler transport, int attempt, string name = ResilienceDeadline.Header) =>
        transport.Requests[attempt].Headers.TryGetValues(name, out var values) ? values.Single() : null;

    /// <summary>
    ///     A transport that spends real deadline on every attempt, which a scripted one cannot: the
    ///     number this feature puts on the wire is only interesting once time has passed.
    /// </summary>
    private sealed class AdvancingTransport(FakeTimeProvider time, TimeSpan spend, HttpStatusCode status) : HttpMessageHandler
    {
        private readonly List<string?> _deadlines = [];

        internal IReadOnlyList<string?> Deadlines => _deadlines;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _deadlines.Add(request.Headers.TryGetValues(ResilienceDeadline.Header, out var values) ? values.Single() : null);
            time.Advance(spend);

            return Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request });
        }
    }
}
