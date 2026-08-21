using NResilience.Probes;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace NResilience.Probes.Polly;

/// <summary>
/// Competitive arms that use the same harness, gate, and suspension count as every other arm.
///
/// Fairness rules ensure the baseline is not rigged:
///
/// <list type="bullet">
///   <item>Polly uses its native callback shape. Its pipeline takes 
///   <c>Func&lt;CancellationToken, ValueTask&lt;T&gt;&gt;</c>, so the callback wraps the shared 
///   gate's <c>Task&lt;int&gt;</c> in a <c>ValueTask&lt;int&gt;</c> struct instead of an extra 
///   <c>async</c> frame. Using <c>async ct =&gt; await Gate.SuspendAsync(ct)</c> would have 
///   charged Polly for a state-machine box its design does not require.</item>
///
///   <item>Delegates are cached statics to ensure no arm incurs a closure cost that another avoids.</item>
///
///   <item>The retry+timeout pipeline matches the fused policy: three total attempts, 
///   constant zero delay, no jitter, and one 10-second timeout.</item>
///
///   <item>No telemetry listener is registered, as this is Polly's cheapest configuration. 
///   Polly's own benchmarks report telemetry at 6.9x; measuring it here would flatter this design.</item>
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
