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
    /// The same budgets the JIT gate enforces. The numbers are duplicated here rather than shared,
    /// because this project must not reference the test project, and because an AOT-specific
    /// divergence is exactly what this gate exists to surface.
    /// </summary>
    private static async Task<int> BudgetsAsync()
    {
        Console.WriteLine();
        int failures = 0;

        double rawSync = await MeasureAsync("raw callback (sync)", Scenarios.RawSync, AllocationCounter.ThreadLocal).ConfigureAwait(false);
        double noneSync = await MeasureAsync("None (sync)", Scenarios.NoneSync, AllocationCounter.ThreadLocal).ConfigureAwait(false);
        double stateSync = await MeasureAsync("no timeout, static+state (sync)", Scenarios.FusedNoTimeoutSyncState, AllocationCounter.ThreadLocal).ConfigureAwait(false);

        failures += Check("no AOT cliff: passthrough is free on the synchronous path", noneSync - rawSync <= 0);
        failures += Check("no AOT cliff: static lambda + state is free on the synchronous path", stateSync - rawSync <= 0);

        double rawSuspending = await MeasureAsync("raw callback (suspending)", Scenarios.RawSuspending, AllocationCounter.ProcessWide).ConfigureAwait(false);
        double noneSuspending = await MeasureAsync("None (suspending)", Scenarios.NoneSuspending, AllocationCounter.ProcessWide).ConfigureAwait(false);
        double defaultSuspending = await MeasureAsync("Default (suspending)", Scenarios.FusedDefaultSuspending, AllocationCounter.ProcessWide).ConfigureAwait(false);

        const double NoiseFloor = 8;
        const double DefaultSuspendingBudget = 465;

        failures += Check("no AOT cliff: passthrough is free on the suspending path", noneSuspending - rawSuspending <= NoiseFloor);
        failures += Check(
            "no AOT cliff: the real loop stays within its suspending budget",
            defaultSuspending - rawSuspending <= DefaultSuspendingBudget + NoiseFloor);

        return failures;
    }

    private static async Task<double> MeasureAsync(string name, Func<ValueTask<int>> body, AllocationCounter counter)
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
