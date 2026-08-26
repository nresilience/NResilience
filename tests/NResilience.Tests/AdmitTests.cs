using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     Tests for <see cref="Resilience.Admit" />: the first-class, value-returning admission hook that
///     selects the executor's second execution path.
///     <para>
///         A refusal <see cref="Resilience.Admit" /> returns gets exactly the treatment a classified
///         <see cref="RateLimitedException" /> gets - see <see cref="RateLimitTests" /> for the same
///         suite run against the exception-based recipe. These tests exist to confirm the two paths
///         agree, not to re-derive the policy from scratch.
///     </para>
/// </summary>
public sealed class AdmitTests
{
    /// <summary>Mirrors <c>RateLimitTests.RunAsync</c>: a guarded rejection is not instant.</summary>
    private static async Task<CallResult<int>> RunAsync(Resilience policy, Func<CancellationToken, Task<int>> work, FakeTimeProvider time)
    {
        var call = policy.TryRunAsync(work).AsTask();

        while (!call.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(1);
        }

        return await call;
    }

    [Fact]
    public async Task An_admitted_attempt_runs_the_callback()
    {
        var time = new FakeTimeProvider();
        var policy = TestPolicy.On(time) with { Admit = _ => Task.FromResult(Verdict.Ok) };

        var result = await RunAsync(policy, _ => Task.FromResult(42), time);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public async Task A_refused_attempt_never_runs_the_callback()
    {
        var time = new FakeTimeProvider();
        var calls = 0;

        var policy = TestPolicy.On(time) with { Attempts = 3, Admit = _ => Task.FromResult(Verdict.Refused()) };

        var result = await RunAsync(
            policy,
            _ =>
            {
                calls++;
                return Task.FromResult(1);
            },
            time);

        Assert.Equal(0, calls);
        Assert.Equal(3, result.Attempts.Count);
        Assert.All(result.Attempts, attempt =>
        {
            Assert.Equal(VerdictKind.Throttled, attempt.Verdict.Kind);
            Assert.True(attempt.Verdict.SelfImposed);
        });
    }

    [Fact]
    public async Task Admit_is_asked_once_per_attempt_and_told_the_previous_verdict()
    {
        var time = new FakeTimeProvider();
        var seen = new List<(int Number, VerdictKind Previous)>();
        var attempt = 0;

        var policy = TestPolicy.On(time) with
        {
            Attempts = 3,
            Admit = next =>
            {
                seen.Add((next.Number, next.PreviousVerdict.Kind));
                return Task.FromResult(attempt++ == 0 ? Verdict.Ok : Verdict.Refused());
            },
        };

        await RunAsync(policy, _ => Task.FromException<int>(new IOException("down")), time);

        Assert.Equal([(1, VerdictKind.Ok), (2, VerdictKind.Transient), (3, VerdictKind.Throttled)], seen);
    }

    [Fact]
    public async Task A_refusal_from_Admit_does_not_spend_retry_budget()
    {
        var time = new FakeTimeProvider();
        var budget = RetryBudget.Of(minimumPerSecond: 1, time: time);

        var policy = TestPolicy.On(time) with
        {
            Attempts = 4,
            Budget = budget,
            Admit = _ => Task.FromResult(Verdict.Refused()),
        };

        var result = await RunAsync(policy, _ => Task.FromResult(1), time);

        Assert.False(result.IsSuccess);
        Assert.Equal(4, result.Attempts.Count);
        Assert.Equal(0, budget.Utilization);
    }

    [Fact]
    public async Task A_refusal_from_Admit_does_not_open_the_breaker()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(new BreakerSettings { ConsecutiveFailures = 2, Time = time });

        var policy = TestPolicy.On(time) with
        {
            Attempts = 6,
            Breaker = breaker,
            Admit = _ => Task.FromResult(Verdict.Refused()),
        };

        var result = await RunAsync(policy, _ => Task.FromResult(1), time);

        Assert.Equal(6, result.Attempts.Count);
        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    [Fact]
    public async Task An_exception_Admit_throws_is_classified_like_any_other()
    {
        var time = new FakeTimeProvider();
        var seen = new List<Type>();

        var policy = TestPolicy.On(time) with
        {
            Attempts = 2,
            Classify = Classifier.RetryEverything.On<InvalidOperationException>(ex =>
            {
                seen.Add(ex.GetType());
                return Verdict.Transient;
            }),
            Admit = _ => throw new InvalidOperationException("guard failed"),
        };

        var result = await RunAsync(policy, _ => Task.FromResult(1), time);

        Assert.Equal([typeof(InvalidOperationException), typeof(InvalidOperationException)], seen);
        Assert.False(result.Attempts[0].Verdict.SelfImposed);
    }

    [Fact]
    public async Task Admit_receives_the_attempt_bound_token_not_the_caller_token()
    {
        var time = new FakeTimeProvider();
        CancellationToken? seen = null;

        var policy = TestPolicy.On(time) with
        {
            Attempts = 1,
            AttemptTimeout = TimeSpan.FromSeconds(5),
            Admit = next =>
            {
                seen = next.CancellationToken;
                return Task.FromResult(Verdict.Ok);
            },
        };

        using var caller = new CancellationTokenSource();
        await policy.TryRunAsync(_ => Task.FromResult(1), caller.Token);

        Assert.NotNull(seen);
        Assert.NotEqual(caller.Token, seen!.Value);
    }

    [Fact]
    public async Task A_policy_with_Admit_configured_is_never_passthrough()
    {
        // Every bound off, which would ordinarily hand back the callback's own task - except Admit
        // takes the policy out of passthrough exactly as OnEvent and Breaker already do.
        var ran = false;

        var policy = Resilience.None with
        {
            Admit = _ =>
            {
                ran = true;
                return Task.FromResult(Verdict.Ok);
            },
        };

        await policy.RunAsync(_ => Task.FromResult(1));

        Assert.True(ran);
    }

    [Fact]
    public async Task TryRunAsync_also_honors_Admit()
    {
        var time = new FakeTimeProvider();

        var policy = TestPolicy.On(time) with { Attempts = 2, Admit = _ => Task.FromResult(Verdict.Refused()) };

        var result = await RunAsync(policy, _ => Task.FromResult(1), time);

        Assert.False(result.IsSuccess);
        Assert.Equal(StopReason.AttemptsExhausted, result.StopReason);
    }
}
