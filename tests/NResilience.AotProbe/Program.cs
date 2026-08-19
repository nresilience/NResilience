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
        const double TrivialSuspendingBudget = 368;      // measured 320
        const double DefaultSuspendingBudget = 448;      // measured 384
        const double TryRunSuspendingBudget = 640;       // measured 553

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
