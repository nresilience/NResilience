using System.Net;
using Microsoft.Extensions.Time.Testing;
using NResilience.Http;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     Fault injection. The claim under test throughout is that an injected outcome is
///     indistinguishable from a real one to everything downstream of the callback.
/// </summary>
public sealed class ChaosTests
{
    [Fact]
    public void A_disabled_profile_hands_the_callback_straight_back()
    {
        Func<CancellationToken, Task<int>> work = static _ => Task.FromResult(1);

        Assert.Same(work, Chaos.None.Inject(work));
        Assert.Same(work, new Chaos { FaultRate = 1, Enabled = false }.Inject(work));
    }

    [Fact]
    public async Task An_injected_fault_is_retried_like_a_real_one()
    {
        // The gate counts its own invocations rather than the callback's: an injected fault means the
        // callback never ran, so counting there would leave the gate open forever.
        var rolls = 0;
        var calls = 0;
        var chaos = new Chaos { Enabled = true, FaultRate = 1, Gate = () => rolls++ == 0 };

        var work = chaos.Inject(ct =>
        {
            calls++;
            return Task.FromResult(42);
        });

        var result = await (TestPolicy.Instant with { Attempts = 3 }).TryRunAsync(work);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.Equal(1, calls);

        // Two entries: the injected failure, then the real success. The injected one went through
        // the classifier and the log exactly as a transport failure would have.
        Assert.Equal(2, result.Attempts.Count);
        Assert.Equal(VerdictKind.Transient, result.Attempts[0].Verdict.Kind);
        Assert.IsType<IOException>(result.Attempts[0].Exception);
    }

    /// <summary>
    ///     The reason the default fault is an <see cref="IOException" />: an unrecognized type is
    ///     <see cref="VerdictKind.Permanent" />, so a chaos run using one would test none of the retry
    ///     machinery it exists to test.
    /// </summary>
    [Fact]
    public void The_default_fault_is_one_the_shipped_classifiers_retry()
    {
        var chaos = new Chaos { Enabled = true, FaultRate = 1 };
        var thrown = Assert.Throws<IOException>(() => chaos.Inject(static _ => Task.FromResult(1))(default).GetAwaiter().GetResult());

        Assert.Equal(VerdictKind.Transient, Classifier.Default.ClassifyException(thrown).Kind);
        Assert.Equal(VerdictKind.Transient, Classifier.Http.ClassifyException(thrown).Kind);
    }

    [Fact]
    public async Task A_fault_of_your_own_is_thrown_verbatim()
    {
        var chaos = new Chaos { Enabled = true, FaultRate = 1, Fault = static () => new InvalidOperationException("mine") };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await (TestPolicy.Instant with { Attempts = 1 }).RunAsync(chaos.Inject(static _ => Task.FromResult(1))));

        Assert.Equal("mine", thrown.Message);
    }

    [Fact]
    public async Task An_injected_outcome_is_judged_by_the_result_rules_rather_than_thrown()
    {
        var chaos = new Chaos { Enabled = true, FaultRate = 1 };
        var work = chaos.Inject(
            static _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
            static () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var result = await (TestPolicy.InstantHttp with { Attempts = 2 }).TryRunAsync(work);

        Assert.False(result.IsSuccess);
        Assert.Equal(StopReason.AttemptsExhausted, result.StopReason);
        Assert.Null(result.Exception);

        // Classified from the status code, by Classifier.Http's own result rule.
        Assert.Equal(VerdictKind.Transient, result.Attempts[0].Verdict.Kind);
        result.Value?.Dispose();
    }

    [Fact]
    public async Task A_gate_that_says_no_leaves_the_call_alone()
    {
        var chaos = new Chaos { Enabled = true, FaultRate = 1, Gate = static () => false };

        var value = await (TestPolicy.Instant with { Attempts = 1 }).RunAsync(chaos.Inject(static _ => Task.FromResult(7)));

        Assert.Equal(7, value);
    }

    [Fact]
    public async Task Injected_latency_is_served_on_the_attempt_token_so_the_attempt_timeout_cuts_it()
    {
        var time = new FakeTimeProvider();
        var chaos = new Chaos { Enabled = true, LatencyRate = 1, Latency = TimeSpan.FromMinutes(5), Time = time };

        var policy = (TestPolicy.On(time) with { Attempts = 1, AttemptTimeout = TimeSpan.FromSeconds(1) })
            .UseClock(time);

        var call = policy.TryRunAsync(chaos.Inject(static _ => Task.FromResult(1)));

        // The injected delay is five minutes and the attempt is allowed one second. Advancing past
        // the ceiling has to end the attempt, which is only true if the delay took the token.
        while (!call.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        var result = await call;

        Assert.False(result.IsSuccess);
        Assert.IsType<AttemptTimeoutException>(result.Exception);
    }

    [Fact]
    public async Task A_seeded_profile_injects_the_same_calls_every_run()
    {
        var first = await CountInjections(seed: 1234);
        var second = await CountInjections(seed: 1234);
        var other = await CountInjections(seed: 9876);

        Assert.Equal(first, second);
        Assert.NotEmpty(first);

        // Not a property of the design, but a broken stream would make the two identical.
        Assert.NotEqual(first, other);

        static async Task<List<int>> CountInjections(int seed)
        {
            var chaos = new Chaos { Enabled = true, FaultRate = 0.5, Seed = seed };
            var injected = new List<int>();
            var work = chaos.Inject<int>(_ => Task.FromResult(0));

            for (var i = 0; i < 40; i++)
            {
                try
                {
                    await work(CancellationToken.None);
                }
                catch (IOException)
                {
                    injected.Add(i);
                }
            }

            return injected;
        }
    }

    [Fact]
    public async Task A_rate_of_one_half_injects_roughly_half()
    {
        var chaos = new Chaos { Enabled = true, FaultRate = 0.5, Seed = 42 };
        var work = chaos.Inject<int>(static _ => Task.FromResult(0));
        var injected = 0;

        for (var i = 0; i < 2_000; i++)
        {
            try
            {
                await work(CancellationToken.None);
            }
            catch (IOException)
            {
                injected++;
            }
        }

        Assert.InRange(injected, 900, 1_100);
    }

    [Theory]
    [InlineData(1.5, 0, 0)]
    [InlineData(-0.1, 0, 0)]
    [InlineData(0, 2, 1)]
    [InlineData(0, 0.5, 0)]
    public void A_profile_that_cannot_work_is_refused(double faultRate, double latencyRate, int latencySeconds)
    {
        var chaos = new Chaos
        {
            FaultRate = faultRate,
            LatencyRate = latencyRate,
            Latency = TimeSpan.FromSeconds(latencySeconds),
        };

        Assert.Throws<ResilienceConfigurationException>(() => chaos.Validate());
    }

    [Fact]
    public void Validated_returns_the_profile_so_a_bad_one_throws_where_it_is_written()
    {
        var chaos = new Chaos { Enabled = true, FaultRate = 0.1 };
        Assert.Same(chaos, chaos.Validated());
    }

    [Fact]
    public async Task The_ValueTask_shape_is_wrapped_too()
    {
        var chaos = new Chaos { Enabled = true, FaultRate = 1 };
        var work = chaos.Inject<int>(static _ => new ValueTask<int>(1));

        await Assert.ThrowsAsync<IOException>(async () => await work(CancellationToken.None));
    }

    [Fact]
    public async Task The_void_shape_is_wrapped_too()
    {
        var chaos = new Chaos { Enabled = true, FaultRate = 1 };

        await Assert.ThrowsAsync<IOException>(async () => await chaos.Inject(static _ => Task.CompletedTask)(CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(async () => await chaos.Inject(static _ => default(ValueTask))(CancellationToken.None));
    }

    [Fact]
    public async Task The_handler_injects_inside_the_policy_so_the_fault_is_retried()
    {
        var rolls = 0;
        var chaos = new Chaos { Enabled = true, FaultRate = 1, Gate = () => rolls++ == 0 };
        var transport = new ScriptedHttpHandler().Respond(HttpStatusCode.OK);
        var handler = new ChaosHandler(chaos) { InnerHandler = transport };

        using var client = new HttpClient(new ResilienceHandler(handler, TestPolicy.InstantHttp));
        using var response = await client.GetAsync(new Uri("https://api.test/thing"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.Injected);

        // The injected failure never reached the transport, and the retry did.
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task The_handler_can_inject_a_status_code_instead_of_an_exception()
    {
        var rolls = 0;
        var chaos = new Chaos { Enabled = true, FaultRate = 1, Gate = () => rolls++ == 0 };
        var transport = new ScriptedHttpHandler().Respond(HttpStatusCode.OK);

        var handler = new ChaosHandler(chaos, static () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
        {
            InnerHandler = transport,
        };

        using var client = new HttpClient(new ResilienceHandler(handler, TestPolicy.InstantHttp));
        using var response = await client.GetAsync(new Uri("https://api.test/thing"));

        // The 503 was classified from its status code and retried, and the second attempt reached
        // the transport - which is the whole point of the handler being inner to the policy.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.Injected);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task A_disabled_handler_forwards_every_request_untouched()
    {
        var transport = new ScriptedHttpHandler().Respond(HttpStatusCode.OK);
        var handler = new ChaosHandler(Chaos.None with { FaultRate = 1 }) { InnerHandler = transport };

        using var client = new HttpClient(new ResilienceHandler(handler, TestPolicy.InstantHttp));
        using var response = await client.GetAsync(new Uri("https://api.test/thing"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, handler.Injected);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task The_handler_counts_what_it_slowed()
    {
        var time = new FakeTimeProvider();
        var chaos = new Chaos { Enabled = true, LatencyRate = 1, Latency = TimeSpan.FromSeconds(2), Time = time };
        var transport = new ScriptedHttpHandler().Respond(HttpStatusCode.OK);
        var handler = new ChaosHandler(chaos) { InnerHandler = transport };

        using var client = new HttpClient(new ResilienceHandler(handler, TestPolicy.On(time) with { Classify = Classifier.Http }));
        var call = client.GetAsync(new Uri("https://api.test/thing"));

        while (!call.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        using var response = await call;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.Slowed);
        Assert.Equal(0, handler.Injected);
    }

    [Fact]
    public void A_handler_refuses_a_profile_that_cannot_work()
    {
        Assert.Throws<ResilienceConfigurationException>(() => new ChaosHandler(new Chaos { FaultRate = 2 }));
    }
}
