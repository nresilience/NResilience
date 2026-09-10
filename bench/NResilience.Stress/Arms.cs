using System.Diagnostics;
using Lib = NResilience;
using NResilience.Probes;
using NResilience.Probes.Polly;
using Polly;
using Polly.Retry;

namespace NResilience.Stress;

/// <summary>
///     The arm definitions. Each factory takes the counters and histogram and returns a per-worker
///     <see cref="OpDelegate" /> that records its own latency and outcome, so the driver stays
///     arm-agnostic. The fairness rules are the gate's, carried over verbatim: cached static
///     delegates where a closure would otherwise be the difference, Polly's native
///     <c>ValueTask</c> callback shape, matched attempt counts and delays, no telemetry on either
///     side.
/// </summary>
/// <remarks>
///     <para>
///         <b>Latency is measured from the timestamp the op was due to start</b>, which the driver
///         hands in. In closed loop that is the moment the worker began; in open loop it is the
///         arrival's scheduled time, so the queueing an overloaded arm imposes counts against it
///         instead of quietly reducing the load offered to it.
///     </para>
///     <para>
///         <b>Counting attempts without telemetry.</b> Every retry arm increments the shared
///         attempt counter at the top of each callback invocation, before the attempt can fail
///         and before any guard can refuse the next one. The attempts column therefore counts
///         invocations that actually ran, which is the amplification a dependency feels; retries
///         per op is that figure less the one attempt every op makes, and it is the comparable
///         number because it does not scale with how fast the client happens to run.
///     </para>
///     <para>
///         <b>Matched pairs.</b> Every NResilience arm has a Polly arm that arms the nearest
///         equivalent shape, including at the floor: <c>lib-passthrough</c> against
///         <c>polly-empty</c> is bound-for-bound (neither can retry, neither has a time bound),
///         and <c>lib-trivial</c> against <c>polly-retry-only</c> pairs two 3-attempt retry
///         shapes with no time bounds. Pairing a retry-capable policy against
///         <c>ResiliencePipeline.Empty</c> would compare a frame that can retry with one that
///         cannot, which is not a floor measurement of either.
///     </para>
/// </remarks>
internal static class Arms
{
    // ---- Executor level: matched A/B arms. ----

    /// <summary>The raw callback, no library: the floor every arm's overhead is read against.</summary>
    public static OpDelegate BuildRaw(OpCounters counters, LatencyHistogram histogram) =>
        new RawOp(counters, histogram).RunAsync;

    /// <summary>
    ///     Passthrough: every bound off, so the executor cannot retry and hands back the
    ///     callback's own task. The bound-for-bound match for <see cref="BuildPollyEmpty" />.
    /// </summary>
    public static OpDelegate BuildPassthrough(OpCounters counters, LatencyHistogram histogram) =>
        new SuspendingOp(counters, histogram, ShippingScenarios.DerivedPassthrough).RunAsync;

    /// <summary>The lightest policy that can still retry: three attempts, no time bounds.</summary>
    public static OpDelegate BuildTrivial(OpCounters counters, LatencyHistogram histogram) =>
        new SuspendingOp(counters, histogram, ShippingScenarios.Trivial).RunAsync;

    /// <summary>The default-shaped policy: deadline, attempt timeout, classifier and retry budget.</summary>
    public static OpDelegate BuildDefault(OpCounters counters, LatencyHistogram histogram) =>
        new SuspendingOp(counters, histogram, Lib.Resilience.Default).RunAsync;

    /// <summary>The same call with a caller token that can be cancelled and never is - the production shape.</summary>
    public static OpDelegate BuildDefaultCancellable(OpCounters counters, LatencyHistogram histogram) =>
        new SuspendingOp(counters, histogram, Lib.Resilience.Default, Scenarios.CallerSource.Token).RunAsync;

    /// <summary>
    ///     Retry x2 then success, budget off - the gate's fairness rule: an arm that retries
    ///     twice per op thousands of times per second would measure budget rejections rather
    ///     than retry machinery, and Polly has no budget to turn off.
    /// </summary>
    public static OpDelegate BuildRetryTwice(OpCounters counters, LatencyHistogram histogram)
    {
        var policy = ShippingScenarios.Trivial with
        {
            Attempts = 3,
            Backoff = Lib.Backoff.None,
            Budget = Lib.RetryBudget.None,
        };

        return new CountingRetryOp(counters, histogram, policy, failuresPerOp: 2).RunAsync;
    }

    private sealed class SuspendingOp(OpCounters counters, LatencyHistogram histogram, Lib.Resilience policy, CancellationToken token = default)
    {
        public async ValueTask RunAsync(long scheduledTicks)
        {
            try
            {
                await policy.RunAsync(Gate.SuspendAsync, token).ConfigureAwait(false);
                counters.Op();
                counters.Succeeded();
            }
            finally
            {
                histogram.Record(scheduledTicks);
            }
        }
    }

    private sealed class RawOp(OpCounters counters, LatencyHistogram histogram)
    {
        public async ValueTask RunAsync(long scheduledTicks)
        {
            try
            {
                await Gate.SuspendAsync(CancellationToken.None).ConfigureAwait(false);
                counters.Op();
                counters.Succeeded();
            }
            finally
            {
                histogram.Record(scheduledTicks);
            }
        }
    }

    /// <summary>
    ///     The lib retry arm. The per-op failure sequence lives in the per-worker counter: the
    ///     first two attempts throw, the third succeeds, then the counter resets for the next op.
    /// </summary>
    private sealed class CountingRetryOp(OpCounters counters, LatencyHistogram histogram, Lib.Resilience policy, int failuresPerOp)
    {
        private readonly Gate.FailCounter _counter = new(failuresPerOp);

        public async ValueTask RunAsync(long scheduledTicks)
        {
            try
            {
                await policy.RunAsync(AttemptAsync, CancellationToken.None).ConfigureAwait(false);
                counters.Op();
                counters.Succeeded();
            }
            finally
            {
                _counter.Reset();
                histogram.Record(scheduledTicks);
            }
        }

        private async Task<int> AttemptAsync(CancellationToken cancellationToken)
        {
            // Counted at the top of every invocation: this is an attempt actually made, and
            // the only in-client point where delivered amplification can be measured without
            // knowing how the policy will judge the outcome.
            counters.Attempted();

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            if (_counter.Next())
                throw _counter.Fault;

            return Gate.Value;
        }
    }

    // ---- Polly: the matched pipelines. ----

    public static OpDelegate BuildPollyEmpty(OpCounters counters, LatencyHistogram histogram) =>
        new PollySuspendingOp(counters, histogram, PollyScenarios.Empty).RunAsync;

    /// <summary>
    ///     Retry x2, zero delay, no time bound: the match for <see cref="BuildTrivial" />. Polly
    ///     has no retry budget, so this is as close as the shape gets - which is the finding, not
    ///     a defect in the pairing.
    /// </summary>
    public static OpDelegate BuildPollyRetryOnly(OpCounters counters, LatencyHistogram histogram) =>
        new PollySuspendingOp(counters, histogram, RetryOnly).RunAsync;

    public static OpDelegate BuildPollyRetryTimeout(OpCounters counters, LatencyHistogram histogram) =>
        new PollySuspendingOp(counters, histogram, PollyScenarios.RetryTimeout).RunAsync;

    /// <summary>
    ///     The same pipeline with a caller token that can be cancelled and never is - the pair for
    ///     <see cref="BuildDefaultCancellable" />.
    /// </summary>
    public static OpDelegate BuildPollyRetryTimeoutCancellable(OpCounters counters, LatencyHistogram histogram) =>
        new PollySuspendingOp(counters, histogram, PollyScenarios.RetryTimeout, Scenarios.CallerSource.Token).RunAsync;

    private static readonly ResiliencePipeline RetryOnly = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 2,
            BackoffType = DelayBackoffType.Constant,
            Delay = TimeSpan.Zero,
            UseJitter = false,
            ShouldHandle = new PredicateBuilder().Handle<Exception>(),
        })
        .Build();

    private sealed class PollySuspendingOp(OpCounters counters, LatencyHistogram histogram, ResiliencePipeline pipeline, CancellationToken token = default)
    {
        public async ValueTask RunAsync(long scheduledTicks)
        {
            try
            {
                await pipeline.ExecuteAsync(
                    static ct => new ValueTask<int>(Gate.SuspendAsync(ct)),
                    token).ConfigureAwait(false);
                counters.Op();
                counters.Succeeded();
            }
            finally
            {
                histogram.Record(scheduledTicks);
            }
        }
    }

    /// <summary>The Polly retry arm, matched to <see cref="BuildRetryTwice" />.</summary>
    public static OpDelegate BuildPollyRetryTwice(OpCounters counters, LatencyHistogram histogram) =>
        new PollyCountingRetryOp(counters, histogram, BuildTransientRetry(), failuresPerOp: 2).RunAsync;

    private static ResiliencePipeline BuildTransientRetry() =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.Zero,
                UseJitter = false,
                ShouldHandle = new PredicateBuilder().Handle<IOException>(),
            })
            .Build();

    private sealed class PollyCountingRetryOp(OpCounters counters, LatencyHistogram histogram, ResiliencePipeline pipeline, int failuresPerOp)
    {
        private readonly Gate.FailCounter _counter = new(failuresPerOp);

        public async ValueTask RunAsync(long scheduledTicks)
        {
            try
            {
                await pipeline.ExecuteAsync(AttemptAsync, CancellationToken.None).ConfigureAwait(false);
                counters.Op();
                counters.Succeeded();
            }
            finally
            {
                _counter.Reset();
                histogram.Record(scheduledTicks);
            }
        }

        private async ValueTask<int> AttemptAsync(CancellationToken cancellationToken)
        {
            counters.Attempted();

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            if (_counter.Next())
                throw _counter.Fault;

            return Gate.Value;
        }
    }

    // ---- Behavioral: the retry budget under sustained failure. ----

    /// <summary>
    ///     NResilience retry x3 against a dependency failing one attempt in
    ///     <paramref name="failurePeriod" />, with <see cref="Lib.RetryBudget.Automatic" /> on.
    ///     <para>
    ///         Driven open-loop, and it has to be. The refused call pauses before it reports -
    ///         a deliberate floor under the rate of a rejection loop - so under a closed loop the
    ///         arm's own pause reduces the load it offers, and every column then measures the
    ///         pause rather than the budget. At a fixed arrival rate the offered load is the
    ///         same for both arms whatever either does internally, which is what makes retries
    ///         per op a comparison of the policies.
    ///     </para>
    /// </summary>
    public static OpDelegate BuildBudgetedRetry(OpCounters counters, LatencyHistogram histogram, int failurePeriod)
    {
        var policy = ShippingScenarios.Trivial with
        {
            Attempts = 3,
            Backoff = Lib.Backoff.None,
            Budget = Lib.RetryBudget.Automatic,
        };

        return new BudgetedRetryOp(counters, histogram, policy, failurePeriod).RunAsync;
    }

    /// <summary>
    ///     Polly retry x3 under the same failure pattern. No budget exists to disable: every
    ///     failing op retries the full count, which is what the section exists to contrast.
    /// </summary>
    public static OpDelegate BuildPollyUnbudgetedRetry(OpCounters counters, LatencyHistogram histogram, int failurePeriod) =>
        new PollyUnbudgetedRetryOp(counters, histogram, BuildTransientRetry(), failurePeriod).RunAsync;

    /// <summary>
    ///     The shared failure pattern for the budget arms: one attempt in <c>period</c> fails,
    ///     counted across every worker of the cell rather than per worker.
    ///     <para>
    ///         Shared on purpose. A per-worker counter makes the pattern deterministic per op -
    ///         with period 2 every op fails its first attempt and succeeds its second, which is
    ///         a client-side alternation rather than a dependency failing a fraction of what it
    ///         is sent. One counter for the cell is the dependency, and it is what both arms face.
    ///     </para>
    /// </summary>
    internal sealed class SharedFailurePattern(int period)
    {
        private long _seen;

        public IOException Fault { get; } = new("stress dependency fault");

        public bool Next() => Interlocked.Increment(ref _seen) % period == 0;
    }

    private sealed class BudgetedRetryOp(OpCounters counters, LatencyHistogram histogram, Lib.Resilience policy, int failurePeriod)
    {
        private readonly SharedFailurePattern _pattern = new(failurePeriod);

        public async ValueTask RunAsync(long scheduledTicks)
        {
            try
            {
                await policy.RunAsync(AttemptAsync, CancellationToken.None).ConfigureAwait(false);
                counters.Op();
                counters.Succeeded();
            }
            catch (Lib.CallRejectedException ex) when (ex.Reason == Lib.StopReason.BudgetExhausted)
            {
                counters.Op();
                counters.BudgetRejected();
                counters.Failed();
            }
            catch (IOException)
            {
                counters.Op();
                counters.Failed();
            }
            finally
            {
                histogram.Record(scheduledTicks);
            }
        }

        private async Task<int> AttemptAsync(CancellationToken cancellationToken)
        {
            counters.Attempted();

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            if (_pattern.Next())
                throw _pattern.Fault;

            return Gate.Value;
        }
    }

    private sealed class PollyUnbudgetedRetryOp(OpCounters counters, LatencyHistogram histogram, ResiliencePipeline pipeline, int failurePeriod)
    {
        private readonly SharedFailurePattern _pattern = new(failurePeriod);

        public async ValueTask RunAsync(long scheduledTicks)
        {
            try
            {
                await pipeline.ExecuteAsync(AttemptAsync, CancellationToken.None).ConfigureAwait(false);
                counters.Op();
                counters.Succeeded();
            }
            catch (IOException)
            {
                counters.Op();
                counters.Failed();
            }
            finally
            {
                histogram.Record(scheduledTicks);
            }
        }

        private async ValueTask<int> AttemptAsync(CancellationToken cancellationToken)
        {
            counters.Attempted();

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            if (_pattern.Next())
                throw _pattern.Fault;

            return Gate.Value;
        }
    }

    // ---- HTTP level. ----

    /// <summary>
    ///     The HTTP arms' shared op: one GET against the loopback server, response status
    ///     deciding success. The response is disposed by the using: a retried 503's response is
    ///     the pipeline's business, not this loop's.
    /// </summary>
    private sealed class HttpOp(OpCounters counters, LatencyHistogram histogram, HttpClient client, string url)
    {
        public async ValueTask RunAsync(long scheduledTicks)
        {
            try
            {
                using var response = await client.GetAsync(url).ConfigureAwait(false);

                counters.Op();

                if (response.IsSuccessStatusCode)
                    counters.Succeeded();
                else
                    counters.Failed();
            }
            catch (Exception)
            {
                counters.Op();
                counters.Failed();
            }
            finally
            {
                histogram.Record(scheduledTicks);
            }
        }
    }

    public static OpDelegate BuildHttpOp(OpCounters counters, LatencyHistogram histogram, HttpClient client, string url) =>
        new HttpOp(counters, histogram, client, url).RunAsync;

    /// <summary>
    ///     The transport every HTTP arm runs over, built identically for all of them.
    ///     <para>
    ///         Identical is the point. A bare <c>new HttpClient()</c> keeps the default 100-second
    ///         timeout, which installs a cancellation source and a timer on every request; an arm
    ///         built that way is charged for machinery the policy arms have turned off, and the
    ///         "cost over raw transport" it anchors is then wrong by that much. Same handler,
    ///         same connection limits, same infinite client timeout on every arm - the resilience
    ///         handler is the only difference left.
    ///     </para>
    /// </summary>
    public static SocketsHttpHandler BuildTransport() => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 256,
    };

    public static HttpClient BuildPlainClient() => new(BuildTransport(), disposeHandler: true)
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
}
