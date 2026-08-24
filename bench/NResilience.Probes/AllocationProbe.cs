namespace NResilience.Probes;

/// <summary>Defines how a measurement counts bytes.</summary>
public enum AllocationCounter
{
    /// <summary>
    ///     Uses <c>GC.GetAllocatedBytesForCurrentThread()</c>. This is exact and is the
    ///     instrument named by the CI gate table. It is valid only where the measured
    ///     body never suspends, because a continuation resumed on a thread-pool thread
    ///     allocates against that thread's counter.
    /// </summary>
    ThreadLocal,

    /// <summary>
    ///     Uses <c>GC.GetTotalAllocatedBytes(precise: true)</c>. This is process-wide and
    ///     therefore immune to the thread hop that every suspending await performs.
    ///     It requires a quiesced process, which is why the gate assembly disables
    ///     test parallelization.
    /// </summary>
    ProcessWide,
}

/// <summary>A single arm's measured allocation.</summary>
public sealed record AllocationMeasurement(string Name, double BytesPerOperation, AllocationCounter Counter, int Iterations, int Repeats)
{
    public override string ToString() => $"{Name,-34} {BytesPerOperation,9:0.0} B/op  ({Counter}, {Repeats}x{Iterations})";
}

/// <summary>
///     The allocation instrument behind the hard gate.
///     This instrument differs from a naive loop in three ways, each of which changes the result:
///     <list type="number">
///         <item>
///             It selects the counter based on the shape of the body. The thread-local counter
///             is exact but incorrect the moment an await suspends; the process-wide counter survives
///             thread hops but needs a quiet process. Sync-completing scenarios use the first,
///             and suspending scenarios use the second. Using the thread-local counter for a
///             suspending body silently under-reports results.
///         </item>
///         <item>
///             It warms to tier 1 and waits for promotion. Tiered compilation promotes based on
///             call count and a background delay (100 ms by default). Tier-1 escape analysis removes
///             allocations, so measuring a tier-0 body reports allocations that production would
///             never make.
///         </item>
///         <item>
///             It reports the minimum across repeats rather than the mean. Allocation noise
///             is one-sided; a stray timer or finalizer can only add bytes. The minimum provides
///             the estimate closest to the noise-free truth.
///         </item>
///     </list>
///     The instrument is generic in the arm's result type. This is load-bearing: an arm that
///     returns a type other than the shared gate's <c>int</c> - such as <c>TryRunAsync</c>,
///     which returns a <c>CallResult&lt;T&gt;</c> - would otherwise need a conversion wrapper.
///     A wrapper that suspends allocates a state-machine box that other arms do not incur.
///     Every arm is awaited directly by the loop in its natural shape.
/// </summary>
public static class AllocationProbe
{
    public const int DefaultWarmup = 4_000;
    public const int DefaultIterations = 2_000;
    public const int DefaultRepeats = 5;

    /// <summary>The delay in milliseconds to allow tiered compilation to promote warmed methods before measuring.</summary>
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

        var best = double.MaxValue;

        for (var r = 0; r < repeats; r++)
        {
            Quiesce();

            var before = Read(counter);

            for (var i = 0; i < iterations; i++)
            {
                betweenOperations?.Invoke();
                await body().ConfigureAwait(false);
            }

            var after = Read(counter);

            var perOp = (after - before) / (double)iterations;

            if (perOp < best)
                best = perOp;
        }

        return new AllocationMeasurement(name, best, counter, iterations, repeats);
    }

    /// <summary>
    ///     Warms in two passes separated by a settle delay. This ensures the second pass runs
    ///     against tier-1 code and the subsequent measurement sees the same code production would.
    /// </summary>
    public static async Task WarmAsync<TResult>(Func<ValueTask<TResult>> body, int warmup, Action? betweenOperations = null)
    {
        for (var i = 0; i < warmup; i++)
        {
            betweenOperations?.Invoke();
            await body().ConfigureAwait(false);
        }

        await Task.Delay(TierUpSettleMs).ConfigureAwait(false);

        for (var i = 0; i < warmup; i++)
        {
            betweenOperations?.Invoke();
            await body().ConfigureAwait(false);
        }
    }

    private static long Read(AllocationCounter counter) => counter switch
    {
        AllocationCounter.ThreadLocal => GC.GetAllocatedBytesForCurrentThread(),
        AllocationCounter.ProcessWide => GC.GetTotalAllocatedBytes(true),
        _ => throw new ArgumentOutOfRangeException(nameof(counter)),
    };

    private static void Quiesce()
    {
        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true);
    }
}
