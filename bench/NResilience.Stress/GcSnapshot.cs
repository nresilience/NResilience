using System.Runtime.InteropServices;

using GCLatencyMode = System.Runtime.GCLatencyMode;

namespace NResilience.Stress;

/// <summary>
///     A point-in-time reading of everything the sustained window reports: allocation counters,
///     GC collection counts, GC pause duration, and thread-pool state. Two snapshots and the
///     deltas between them are the whole memory story of a cell.
/// </summary>
internal readonly struct GcSnapshot
{
    public readonly long AllocatedBytes;
    public readonly int Gen0;
    public readonly int Gen1;
    public readonly int Gen2;
    public readonly TimeSpan PauseDuration;

    private GcSnapshot(long allocatedBytes, int gen0, int gen1, int gen2, TimeSpan pauseDuration)
    {
        AllocatedBytes = allocatedBytes;
        Gen0 = gen0;
        Gen1 = gen1;
        Gen2 = gen2;
        PauseDuration = pauseDuration;
    }

    public static GcSnapshot Take() => new(
        GC.GetTotalAllocatedBytes(precise: true),
        GC.CollectionCount(0),
        GC.CollectionCount(1),
        GC.CollectionCount(2),
        GC.GetTotalPauseDuration());

    /// <summary>What the window cost, as the difference between two snapshots.</summary>
    public static GcDelta Between(GcSnapshot start, GcSnapshot end) => new(
        end.AllocatedBytes - start.AllocatedBytes,
        end.Gen0 - start.Gen0,
        end.Gen1 - start.Gen1,
        end.Gen2 - start.Gen2,
        end.PauseDuration - start.PauseDuration);
}

/// <summary>The deltas a cell reports: totals over the steady window.</summary>
/// <remarks>
///     <see cref="Gen0" /> counts every collection of generation 0 or higher, which is what
///     <see cref="GC.CollectionCount(int)" /> means at generation 0 - so it is the total
///     collection count, and <see cref="MeanPause" /> divides by it rather than by a sum of the
///     three generations that would double-count every promotion.
/// </remarks>
internal readonly struct GcDelta(long allocatedBytes, int gen0, int gen1, int gen2, TimeSpan pauseDuration)
{
    public readonly long AllocatedBytes = allocatedBytes;
    public readonly int Gen0 = gen0;
    public readonly int Gen1 = gen1;
    public readonly int Gen2 = gen2;
    public readonly TimeSpan PauseDuration = pauseDuration;

    /// <summary>
    ///     Wall-clock cost of one collection. The number that separates "allocates more often"
    ///     from "allocates things that survive": collection frequency tracks the allocation
    ///     rate, but what each collection costs tracks how much of it is still live when the
    ///     collector arrives, and only the second is a property of the design under test.
    /// </summary>
    public TimeSpan MeanPause => Gen0 == 0 ? TimeSpan.Zero : PauseDuration / Gen0;
}

/// <summary>
///     Samples thread-pool state across the measured window instead of reading it once at the
///     end. A single end-of-window reading says nothing about saturation - the queue it would
///     have caught has usually drained by then - so the peak and the mean over the window are
///     what a claim about overload has to rest on.
/// </summary>
internal sealed class PoolSampler : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private long _queueSum;
    private long _queuePeak;
    private int _threadPeak;
    private long _samples;

    public PoolSampler(TimeSpan interval)
    {
        _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                var queue = ThreadPool.PendingWorkItemCount;
                var threads = ThreadPool.ThreadCount;

                Interlocked.Add(ref _queueSum, queue);
                Interlocked.Increment(ref _samples);

                if (queue > Interlocked.Read(ref _queuePeak))
                    Interlocked.Exchange(ref _queuePeak, queue);

                if (threads > Volatile.Read(ref _threadPeak))
                    Interlocked.Exchange(ref _threadPeak, threads);

                try
                {
                    await Task.Delay(interval, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
    }

    public long QueuePeak => Interlocked.Read(ref _queuePeak);

    public int ThreadPeak => Volatile.Read(ref _threadPeak);

    public double QueueMean
    {
        get
        {
            var samples = Interlocked.Read(ref _samples);

            return samples == 0 ? 0 : Interlocked.Read(ref _queueSum) / (double)samples;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();

        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The sampler's own cancellation; nothing a cell's result depends on.
        }

        _stop.Dispose();
    }
}

/// <summary>
///     The runtime configuration a cell ran under, read back in the child so the matrix can
///     verify the env overrides took. Server GC comes from <see cref="System.Runtime.GCSettings.IsServerGC" />;
///     concurrent GC has no such property, so it is read from the latency mode the runtime
///     selects for it - <see cref="GCLatencyMode.Batch" /> when concurrent GC is off, and
///     <see cref="GCLatencyMode.Interactive" /> (or <see cref="GCLatencyMode.SustainedLowLatency" />)
///     when it is on.
/// </summary>
internal readonly record struct RuntimeMode(bool ServerGc, bool ConcurrentGc)
{
    public static RuntimeMode Current() => new(
        System.Runtime.GCSettings.IsServerGC,
        System.Runtime.GCSettings.LatencyMode is GCLatencyMode.Interactive or GCLatencyMode.SustainedLowLatency);

    public override string ToString() => $"{(ServerGc ? "server" : "workstation")}/{(ConcurrentGc ? "concurrent" : "non-concurrent")}";
}

internal static class MachineFacts
{
    public static string Describe() =>
        $"{RuntimeInformation.FrameworkDescription} on {RuntimeInformation.OSDescription}, {Environment.ProcessorCount} logical cores";
}
