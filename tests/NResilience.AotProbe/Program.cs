using System.Globalization;
using System.Runtime.CompilerServices;
using NResilience.Probes;

namespace NResilience.AotProbe;

/// <summary>
/// The Native AOT gate: a published binary that actually executes a policy and asserts the
/// result, then re-runs the allocation budgets under AOT.
///
/// Publishing without warnings proves the code is AOT-clean. It does not prove there is no AOT
/// allocation cliff, and that is the claim worth defending — Polly boxes state per layer per
/// execution under Native AOT, so its zero-allocation claim is false there. A gate that only
/// checked for warnings would never have caught that.
///
/// Exit code 0 means every budget held. Anything else fails the build.
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        Log($"framework    : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Log($"architecture : {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        Log($"server GC    : {System.Runtime.GCSettings.IsServerGC}");
        Console.WriteLine();

        int failures = 0;

        failures += await CorrectnessAsync().ConfigureAwait(false);
        failures += await ShippingLibraryAsync().ConfigureAwait(false);
        failures += await BudgetsAsync().ConfigureAwait(false);

        Console.WriteLine();
        Log(failures == 0 ? "AOT gate: PASS" : $"AOT gate: FAIL ({failures} check(s))");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>The published binary has to do the thing, not merely start.</summary>
    private static async Task<int> CorrectnessAsync()
    {
        int failures = 0;

        var executor = new FusedExecutor(FusedPolicy.Default);

        int value = await executor.RunAsync(Gate.SuspendAsync).ConfigureAwait(false);
        failures += Check("suspending call returns the callback's value", value == Gate.Value);

        value = await executor.RunAsync(static (int _, CancellationToken ct) => Gate.CompleteAsync(ct), 0).ConfigureAwait(false);
        failures += Check("stateful overload returns the callback's value", value == Gate.Value);

        var counter = new Gate.FailCounter(failures: 2);
        value = await executor.RunAsync(Gate.SuspendThenFailAsync, counter).ConfigureAwait(false);
        failures += Check("two transient failures are retried to success", value == Gate.Value);

        var permanent = new FusedExecutor(FusedPolicy.NoTimeout with { Attempts = 3 });
        bool threw = false;
        try
        {
            await permanent.RunAsync(static _ => Task.FromException<int>(new InvalidOperationException("permanent"))).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        failures += Check("an unrecognised exception is not retried and propagates", threw);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().ConfigureAwait(false);
        bool cancelledCorrectly = false;
        try
        {
            await executor.RunAsync(Gate.SuspendAsync, cancelled.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelledCorrectly = true;
        }

        failures += Check("caller cancellation propagates untouched", cancelledCorrectly);

        return failures;
    }

    /// <summary>
    /// The shipping library, published Native AOT.
    ///
    /// "No reflection anywhere in core" is a claim, and the only thing that can check it is a
    /// trimmed, AOT-compiled binary running the real executor. Publishing with the trim and AOT
    /// analysers on and warnings as errors proves the code is clean; running it proves the
    /// per-result-type judge cache and the generic-struct invoker survive whole-program
    /// compilation, which is where an implementation that reached for reflection would break.
    /// </summary>
    private static async Task<int> ShippingLibraryAsync()
    {
        Console.WriteLine();
        int failures = 0;

        int value = await Resilience.Default.RunAsync(Gate.SuspendAsync).ConfigureAwait(false);
        failures += Check("library: a suspending call returns the callback's value", value == Gate.Value);

        value = await Resilience.Default.RunAsync(static (int _, CancellationToken ct) => Gate.CompleteAsync(ct), 0).ConfigureAwait(false);
        failures += Check("library: the stateful overload returns the callback's value", value == Gate.Value);

        Resilience instant = Resilience.Default with { Backoff = Backoff.None, Attempts = 3 };
        var counter = new Gate.FailCounter(failures: 2);
        value = await instant.RunAsync(Gate.SuspendThenFailAsync, counter).ConfigureAwait(false);
        failures += Check("library: two transient failures are retried to success", value == Gate.Value);

        bool threw = false;
        try
        {
            await instant.RunAsync(static _ => Task.FromException<int>(new InvalidOperationException("permanent"))).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        failures += Check("library: an unrecognised exception is not retried and propagates", threw);

        // The result-classification cache is the one place a naive implementation would reach for
        // reflection, so it is exercised over two distinct result types in one process.
        Resilience classified = instant with
        {
            Classify = Classifier.Default.OnResult<int>(static v => v == 503 ? Verdict.Transient : Verdict.Ok),
        };

        CallResult<int> failing = await classified.TryRunAsync(static ct => Task.FromResult(503)).ConfigureAwait(false);
        failures += Check("library: a result rule fires under AOT", !failing.IsSuccess && failing.Attempts.Count == 3);

        CallResult<string> unjudged = await classified.TryRunAsync(static ct => Task.FromResult("fine")).ConfigureAwait(false);
        failures += Check("library: an unjudged result type is a success under AOT", unjudged.IsSuccess);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().ConfigureAwait(false);
        bool cancelledCorrectly = false;
        try
        {
            await Resilience.Default.RunAsync(Gate.SuspendAsync, cancelled.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelledCorrectly = true;
        }

        failures += Check("library: caller cancellation propagates untouched", cancelledCorrectly);

        failures += await GuardsAsync().ConfigureAwait(false);
        failures += await TelemetryAsync().ConfigureAwait(false);

        return failures;
    }

    /// <summary>
    /// Phase 3 under AOT. The one thing here that whole-program compilation could plausibly break
    /// is the boxed result on <see cref="CallEvent.Result"/>: it is the only place the executor
    /// converts a generic <c>T</c> to <see cref="object"/>, and the <c>typeof(T)</c> test that
    /// keeps the void entry points from handing out a box of an internal type is folded by the
    /// compiler rather than evaluated.
    /// </summary>
    private static async Task<int> TelemetryAsync()
    {
        int failures = 0;

        var kinds = new List<CallEventKind>();
        var results = new List<object?>();

        Resilience watched = Resilience.Default with
        {
            Attempts = 2,
            Backoff = Backoff.None,
            Deadline = Timeout.InfiniteTimeSpan,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Name = "aot",
            OnEvent = e =>
            {
                kinds.Add(e.Kind);
                results.Add(e.Result);
            },
        };

        int value = await watched.RunAsync(static _ => Task.FromResult(41)).ConfigureAwait(false);

        failures += Check("library: a successful call raises Attempt then Succeeded under AOT",
            value == 41 && kinds is [CallEventKind.Attempt, CallEventKind.Succeeded]);
        failures += Check("library: the boxed result survives AOT", results is [41, 41]);

        kinds.Clear();
        await watched.TryRunAsync(static _ => Task.FromException<int>(new IOException("aot"))).ConfigureAwait(false);
        failures += Check("library: a retried call raises Retrying under AOT", kinds.Contains(CallEventKind.Retrying));

        kinds.Clear();
        results.Clear();
        await watched.TryRunAsync(static _ => Task.CompletedTask).ConfigureAwait(false);
        failures += Check("library: a void call reports no result under AOT", results.TrueForAll(static r => r is null));

        return failures;
    }

    /// <summary>
    /// The Phase 2 guards under AOT. Both hold mutable state behind a lock and both feed the
    /// executor's rejection path, so what this checks is that the state machine and the guarded
    /// rejection survive whole-program compilation — including <c>Task.Delay</c> on a
    /// <see cref="TimeProvider"/>, which the guard uses and which nothing else in the probe does.
    /// </summary>
    private static async Task<int> GuardsAsync()
    {
        int failures = 0;

        Resilience instant = Resilience.Default with
        {
            Backoff = Backoff.None,
            Deadline = Timeout.InfiniteTimeSpan,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
        };

        var breaker = new Breaker(new BreakerSettings { ConsecutiveFailures = 2 }) { Name = "aot" };
        Resilience guarded = instant with { Breaker = breaker, Attempts = 2, Budget = RetryBudget.None };

        bool ran = false;
        CallResult<int> tripped = await guarded.TryRunAsync(
            static _ => Task.FromException<int>(new IOException("aot")))
            .ConfigureAwait(false);

        failures += Check("library: two transient attempts open the breaker", breaker.State == BreakerState.Open);
        failures += Check("library: the breaker records an opening time", breaker.OpenedAt is not null);
        failures += Check("library: the tripped operation still reports its own failure", !tripped.IsSuccess);

        CallResult<int> refused = await guarded.TryRunAsync(_ =>
        {
            ran = true;
            return Task.FromResult(1);
        }).ConfigureAwait(false);

        failures += Check("library: an open breaker refuses the call without running it", !ran);
        failures += Check(
            "library: a refusal reports DependencyUnavailable",
            refused.StopReason == StopReason.DependencyUnavailable && refused.Exception is CallRejectedException);

        breaker.Reset();
        failures += Check("library: Reset closes the breaker", breaker.State == BreakerState.Closed);

        // A quarter of a token per success and no floor, so the bucket funds exactly one retry and
        // the operation after it is refused at the throttle step rather than at admission.
        Resilience metered = instant with { Attempts = 3, Budget = RetryBudget.Of(fraction: 0.25, minimumPerSecond: 0) };

        await metered.TryRunAsync(static _ => Task.FromException<int>(new IOException("aot"))).ConfigureAwait(false);
        CallResult<int> throttled = await metered
            .TryRunAsync(static _ => Task.FromException<int>(new IOException("aot")))
            .ConfigureAwait(false);

        failures += Check(
            "library: an exhausted budget refuses the retry",
            throttled.StopReason == StopReason.BudgetExhausted && throttled.Attempts.Count == 1);

        return failures;
    }

    /// <summary>
    /// The same budgets the JIT gate enforces, against the same shipping executor. Phase 0b
    /// re-pointed these from the Phase 0a stand-in loop; the stand-in arms stay in the correctness
    /// section above, where what they prove is that the harness itself survives AOT.
    ///
    /// The numbers are duplicated here rather than shared, because this project must not reference
    /// the test project, and because an AOT-specific divergence is exactly what this gate exists to
    /// surface.
    /// </summary>
    private static async Task<int> BudgetsAsync()
    {
        Console.WriteLine();
        int failures = 0;

        // Phase 0b, .NET 10 / .NET 8, arm64: bytes above an identical un-wrapped callback.
        const double NoiseFloor = 8;
        const double TrivialSuspendingBudget = 368;      // measured 328 (320 before Phase 2)
        const double DefaultSuspendingBudget = 448;      // measured 393 (384 before Phase 2)
        const double TryRunSuspendingBudget = 640;       // measured 561 (553 before Phase 2)
        const double ListenerAllowance = 72;             // measured 48: two boxed int results

        double rawSync = await MeasureAsync("raw callback (sync)", Scenarios.RawSync, AllocationCounter.ThreadLocal).ConfigureAwait(false);
        double noneSync = await MeasureAsync("None (sync)", ShippingScenarios.NoneSync, AllocationCounter.ThreadLocal).ConfigureAwait(false);
        double trivialSync = await MeasureAsync("trivial, static+state (sync)", ShippingScenarios.TrivialSyncState, AllocationCounter.ThreadLocal).ConfigureAwait(false);
        double defaultSync = await MeasureAsync("Default, static+state (sync)", ShippingScenarios.DefaultSyncState, AllocationCounter.ThreadLocal).ConfigureAwait(false);

        failures += Check("no AOT cliff: passthrough is free on the synchronous path", noneSync - rawSync <= 0);
        failures += Check("no AOT cliff: static lambda + state is free on the synchronous path", trivialSync - rawSync <= 0);

        // 64 B, and a floor rather than an implementation failure: one linked source per attempt,
        // because the callback needs a token the attempt timeout can cancel and the pooled source's
        // own token must never be handed to user code. See plans/phase-0a-results.md.
        failures += Check("no AOT cliff: an attempt timeout still costs exactly one linked source", defaultSync - rawSync <= 72);

        double rawSuspending = await MeasureAsync("raw callback (suspending)", Scenarios.RawSuspending, AllocationCounter.ProcessWide).ConfigureAwait(false);
        double noneSuspending = await MeasureAsync("None (suspending)", ShippingScenarios.NoneSuspending, AllocationCounter.ProcessWide).ConfigureAwait(false);
        double trivialSuspending = await MeasureAsync("trivial (suspending)", ShippingScenarios.TrivialSuspending, AllocationCounter.ProcessWide).ConfigureAwait(false);
        double defaultSuspending = await MeasureAsync("Default (suspending)", ShippingScenarios.DefaultSuspending, AllocationCounter.ProcessWide).ConfigureAwait(false);
        double tryRunSuspending = await MeasureAsync("TryRunAsync, Default (suspending)", ShippingScenarios.TryRunDefaultSuspending, AllocationCounter.ProcessWide).ConfigureAwait(false);
        double listenerSuspending = await MeasureAsync("Default + listener (suspending)", ShippingScenarios.DefaultListenerSuspending, AllocationCounter.ProcessWide).ConfigureAwait(false);

        failures += Check("no AOT cliff: passthrough is free on the suspending path", noneSuspending - rawSuspending <= NoiseFloor);
        failures += Check(
            "no AOT cliff: the trivial policy stays within its suspending budget",
            trivialSuspending - rawSuspending <= TrivialSuspendingBudget + NoiseFloor);
        failures += Check(
            "no AOT cliff: the real loop stays within its suspending budget",
            defaultSuspending - rawSuspending <= DefaultSuspendingBudget + NoiseFloor);

        // TryRunAsync always materialises the attempt log, and the log is where an AOT-specific
        // divergence would surface: it is the one part of the frame that reaches the heap.
        failures += Check(
            "no AOT cliff: reporting the outcome stays within its suspending budget",
            tryRunSuspending - rawSuspending <= TryRunSuspendingBudget + NoiseFloor);

        // Pay-for-play, under AOT. A listener may cost the results it asked to have boxed and
        // nothing else - 48 B on both target frameworks under the JIT, for the two events on a
        // successful call that carry one.
        failures += Check(
            "no AOT cliff: a listener costs only the results it asked to be boxed",
            listenerSuspending - defaultSuspending <= ListenerAllowance);

        return failures;
    }

    private static async Task<double> MeasureAsync<T>(string name, Func<ValueTask<T>> body, AllocationCounter counter)
    {
        AllocationMeasurement measurement = await AllocationProbe.MeasureAsync(name, body, counter).ConfigureAwait(false);
        Log($"  {name,-36} {measurement.BytesPerOperation,9:0.0} B/op");
        return measurement.BytesPerOperation;
    }

    private static int Check(string what, bool ok)
    {
        Log($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        return ok ? 0 : 1;
    }

    private static void Log(ref DefaultInterpolatedStringHandler message)
        => Console.WriteLine(string.Create(CultureInfo.InvariantCulture, ref message));

    private static void Log(string message) => Console.WriteLine(message);
}
