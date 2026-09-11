using NResilience.Internal;

namespace NResilience.Testing.Internal;

/// <summary>
///     Points policies at what their modeled pool last published, on the interval the shipping probe
///     actually publishes on.
///     <para>
///         One driver serves a whole run, however many pools it has: the quantization is a property of
///         the clock rather than of any one pool, so every reader crosses a boundary at the same
///         instant.
///     </para>
/// </summary>
/// <remarks>
///     Held to four times a second and evaluated at the boundary rather than at the instant asked for.
///     A reading recomputed on every advance of a virtual clock would be an oracle rather than a
///     probe: it would notice a stall the instant it began, where a real reader sees the previous
///     sample until the next one lands. Only the <i>measurement</i> is quantized - the delay an attempt
///     actually waits is read at the instant it waits it.
/// </remarks>
internal sealed class ProbeDriver(ProbeDriver.Reader[] readers)
{
    /// <summary>Which probe interval the substituted readings were last computed for, or -1 for none.</summary>
    private long _probed = -1;

    /// <summary>Whether any policy in the run reads a modeled pool.</summary>
    internal bool IsEmpty => readers.Length == 0;

    /// <summary>
    ///     Makes a policy read a pool that never queues, which is what a policy configuring
    ///     <see cref="Resilience.Saturation" /> gets when the run models no pool for it.
    /// </summary>
    /// <param name="policy">The policy.</param>
    /// <remarks>
    ///     The alternative is reading the real process-wide probe, which is the one input to a run the
    ///     simulator would not own - and would put a machine-dependent term in a report that is
    ///     otherwise reproducible to the last byte.
    /// </remarks>
    internal static void Freeze(Resilience policy) =>
        ExecutionState.OverrideProbe(policy, new PoolProbe.Reading(TimeSpan.Zero, null));

    /// <summary>
    ///     Refreshes every reading that has crossed a probe boundary. Called on every move of the
    ///     virtual clock, so an attempt starting from a retry timer reads the instant that timer fired
    ///     at rather than the last arrival's.
    /// </summary>
    /// <param name="ticks">Ticks since the start of the run.</param>
    internal void Refresh(long ticks)
    {
        var interval = ticks / PoolProbe.Interval.Ticks;

        if (interval == _probed)
            return;

        _probed = interval;

        var at = TimeSpan.FromTicks(interval * PoolProbe.Interval.Ticks);

        foreach (var reader in readers)
            ExecutionState.OverrideProbe(reader.Policy, reader.Pool.At(at, reader.Samples));
    }

    /// <summary>One policy, the pool it measures, and how many probes its baseline needs.</summary>
    internal readonly record struct Reader(Pool Pool, Resilience Policy, int Samples);
}
