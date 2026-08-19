using NResilience.Probes;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace NResilience.Probes.Polly;

/// <summary>
/// The competitive arms, on the same harness, over the same gate, with the same suspension
/// count as every other arm.
///
/// Fairness rules, stated because a rigged baseline would make the whole exercise worthless:
///
/// <list type="bullet">
///   <item>Polly is given its <b>native</b> callback shape. Its pipeline takes
///   <c>Func&lt;CancellationToken, ValueTask&lt;T&gt;&gt;</c>, so the callback wraps the shared gate's
///   <c>Task&lt;int&gt;</c> in a <c>ValueTask&lt;int&gt;</c> struct rather than in an extra
///   <c>async</c> frame. Writing <c>async ct =&gt; await Gate.SuspendAsync(ct)</c> would have
///   charged Polly a whole state-machine box that its design does not require.</item>
///
///   <item>The delegates are cached statics, so no arm pays for a closure another avoids.</item>
///
///   <item>The retry+timeout pipeline is configured to match the fused policy it is compared
///   against: three total attempts, constant zero delay, no jitter, one 10-second timeout.</item>
///
///   <item>No telemetry listener is registered, which is Polly's cheapest configuration. Its
///   own benchmarks put telemetry at 6.9x, and measuring it here would flatter this design.</item>
/// </list>
/// </summary>
public static class PollyScenarios
{
    private static readonly Func<CancellationToken, ValueTask<int>> SuspendCallback =
        static ct => new ValueTask<int>(Gate.SuspendAsync(ct));

    private static readonly Func<CancellationToken, ValueTask<int>> CompleteCallback =
        static ct => new ValueTask<int>(Gate.CompleteAsync(ct));

    public static readonly ResiliencePipeline Empty = ResiliencePipeline.Empty;

    public static readonly ResiliencePipeline RetryTimeout = BuildRetryTimeout();

    public static ResiliencePipeline BuildRetryTimeout() =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.Zero,
                UseJitter = false,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(),
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(10),
            })
            .Build();

    // ---- Suspending path. ----

    public static ValueTask<int> EmptySuspending() => Empty.ExecuteAsync(SuspendCallback, CancellationToken.None);

    public static ValueTask<int> RetryTimeoutSuspending() => RetryTimeout.ExecuteAsync(SuspendCallback, CancellationToken.None);

    public static ValueTask<int> RetryTimeoutSuspendingCancellable() => RetryTimeout.ExecuteAsync(SuspendCallback, Scenarios.CallerSource.Token);

    // ---- Synchronous fast path. ----

    public static ValueTask<int> EmptySync() => Empty.ExecuteAsync(CompleteCallback, CancellationToken.None);

    public static ValueTask<int> RetryTimeoutSync() => RetryTimeout.ExecuteAsync(CompleteCallback, CancellationToken.None);

    // ---- Retry, matched to the fused retry arm. ----

    public static PollyRetryArm BuildRetryArm(int failures = 2) => new(failures);

    public sealed class PollyRetryArm
    {
        private readonly Gate.FailCounter _counter;
        private readonly ResiliencePipeline _pipeline;
        private readonly Func<Gate.FailCounter, CancellationToken, ValueTask<int>> _callback =
            static (counter, ct) => new ValueTask<int>(Gate.SuspendThenFailAsync(counter, ct));

        public PollyRetryArm(int failures)
        {
            _counter = new Gate.FailCounter(failures);
            _pipeline = new ResiliencePipelineBuilder()
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = failures,
                    BackoffType = DelayBackoffType.Constant,
                    Delay = TimeSpan.Zero,
                    UseJitter = false,
                    ShouldHandle = new PredicateBuilder().Handle<IOException>(),
                })
                .Build();
        }

        public void Reset() => _counter.Reset();

        public ValueTask<int> RunAsync() => _pipeline.ExecuteAsync(_callback, _counter, CancellationToken.None);
    }
}
