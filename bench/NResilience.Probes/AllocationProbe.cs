namespace NResilience.Probes;

/// <summary>How a measurement counts bytes.</summary>
public enum AllocationCounter
{
    /// <summary>
    /// <c>GC.GetAllocatedBytesForCurrentThread()</c>. Exact, and the instrument named by the CI
    /// gate table — but valid <b>only</b> where the measured body never suspends, because a
    /// continuation resumed on a thread-pool thread allocates against that thread's counter.
    /// </summary>
    ThreadLocal,

    /// <summary>
    /// <c>GC.GetTotalAllocatedBytes(precise: true)</c>. Process-wide, and therefore immune to
    /// the thread hop that every suspending await performs. Requires a quiesced process, which
    /// is why the gate assembly disables test parallelisation.
    /// </summary>
    ProcessWide,
}

/// <summary>One arm's measured allocation.</summary>
public sealed record AllocationMeasurement(string Name, double BytesPerOperation, AllocationCounter Counter, int Iterations, int Repeats)
{
    public override string ToString() => $"{Name,-34} {BytesPerOperation,9:0.0} B/op  ({Counter}, {Repeats}x{Iterations})";
}

/// <summary>
/// The allocation instrument behind the hard gate.
///
/// Three things it does that a naive loop does not, each of which changes the answer:
///
/// <list type="number">
///   <item><b>It picks the counter from the shape of the body.</b> The thread-local counter is
///   exact but wrong the moment an await suspends; the process-wide counter survives thread
///   hops but needs a quiet process. Sync-completing scenarios use the first, suspending
///   scenarios the second. Using the thread-local counter for a suspending body silently
///   under-reports, which would make every number in Phase 0a flattering and false.</item>
///
///   <item><b>It warms to tier 1 and waits for it.</b> Tiered compilation promotes on call count
///   <i>and</i> a background delay (100 ms by default), and tier-1 escape analysis is what
///   removes allocations. Measuring a tier-0 body reports allocations that production would
///   never make.</item>
///
///   <item><b>It reports the minimum across repeats, not the mean.</b> Allocation noise is
///   one-sided: a stray timer or finalizer can only add bytes. The minimum is the estimate
///   closest to the noise-free truth.</item>
/// </list>
///
/// It is generic in the arm's result type, and that is load-bearing rather than tidy: an arm that
/// returns something other than the shared gate's <c>int</c> — <c>TryRunAsync</c> returns a
/// <c>CallResult&lt;T&gt;</c> — would otherwise need a conversion wrapper, and a wrapper that
/// suspends allocates a state-machine box the other arms do not pay. Every arm is awaited directly
/// by the loop in its own natural shape.
/// </summary>
public static class AllocationProbe
{
    public const int DefaultWarmup = 4_000;
    public const int DefaultIterations = 2_000;
    public const int DefaultRepeats = 5;

    /// <summary>Milliseconds to let tiered compilation promote warmed methods before measuring.</summary>
    public const int TierUpSettleMs = 300;

    public static async Task<AllocationMeasurement> MeasureAsync<TResult>(
        string name,
        Func<ValueTask<TResult>> body,
        AllocationCounter counter,
        int warmup = DefaultWarmup,
        int iterations = DefaultIterations,
        int repeats = DefaultRepeats,
        Action? betweenOperations = null)
    {
        ArgumentNullException.ThrowIfNull(body);

        await WarmAsync(body, warmup, betweenOperations).ConfigureAwait(false);

        double best = double.MaxValue;
        for (int r = 0; r < repeats; r++)
        {
            Quiesce();

            long before = Read(counter);
            for (int i = 0; i < iterations; i++)
            {
                betweenOperations?.Invoke();
                await body().ConfigureAwait(false);
            }

            long after = Read(counter);

            double perOp = (after - before) / (double)iterations;
            if (perOp < best)
            {
                best = perOp;
            }
        }

        return new AllocationMeasurement(name, best, counter, iterations, repeats);
    }

    /// <summary>
    /// Warms in two passes separated by a settle delay, so the second pass runs against tier-1
    /// code and the measurement that follows sees the same code production would.
    /// </summary>
    public static async Task WarmAsync<TResult>(Func<ValueTask<TResult>> body, int warmup, Action? betweenOperations = null)
    {
        for (int i = 0; i < warmup; i++)
        {
            betweenOperations?.Invoke();
            await body().ConfigureAwait(false);
        }

        await Task.Delay(TierUpSettleMs).ConfigureAwait(false);

        for (int i = 0; i < warmup; i++)
        {
            betweenOperations?.Invoke();
            await body().ConfigureAwait(false);
        }
    }

    private static long Read(AllocationCounter counter) => counter switch
    {
        AllocationCounter.ThreadLocal => GC.GetAllocatedBytesForCurrentThread(),
        AllocationCounter.ProcessWide => GC.GetTotalAllocatedBytes(precise: true),
        _ => throw new ArgumentOutOfRangeException(nameof(counter)),
    };

    private static void Quiesce()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    }
}
