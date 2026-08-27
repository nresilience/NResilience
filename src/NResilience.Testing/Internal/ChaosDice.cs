namespace NResilience.Testing.Internal;

/// <summary>
///     One chaos profile's random stream, and the roll it produces.
///     <para>
///         A splitmix64 step over an interlocked counter: lock-free, allocation-free per roll, and
///         repeatable from a seed for as long as the calls are sequential. Concurrent callers all get
///         distinct draws from the stream; which caller gets which draw is a property of the scheduler
///         rather than of the seed, and a test that needs an exact count should drive the callback
///         sequentially.
///     </para>
/// </summary>
internal sealed class ChaosDice(int? seed)
{
    private long _state = seed ?? Environment.TickCount64;

    /// <summary>A uniform double in [0, 1).</summary>
    internal double Next()
    {
        var x = (ulong)Interlocked.Add(ref _state, unchecked((long)0x9E3779B97F4A7C15UL));

        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        x ^= x >> 31;

        return (x >> 11) * (1.0 / (1UL << 53));
    }
}

/// <summary>What one roll decided. A struct, so rolling costs nothing to return.</summary>
internal readonly struct ChaosRoll(TimeSpan latency, bool faults)
{
    /// <summary>How long to slow this call by. Zero when it was not selected.</summary>
    internal TimeSpan Latency { get; } = latency;

    /// <summary>Whether this call fails instead of reaching the dependency.</summary>
    internal bool Faults { get; } = faults;

    /// <summary>True when this roll leaves the call completely alone.</summary>
    internal bool IsInert => Latency <= TimeSpan.Zero && !Faults;
}

/// <summary>
///     The decision, shared by the callback wrapper and the HTTP handler so the two cannot disagree
///     about what a rate means.
/// </summary>
internal static class ChaosCore
{
    /// <summary>
    ///     Rolls for this call. Two independent draws rather than one partitioned range: a call that is
    ///     both slowed and failed is a real shape, and a single draw would make the two mutually
    ///     exclusive.
    /// </summary>
    internal static ChaosRoll Roll(Chaos chaos, ChaosDice dice)
    {
        // The gate is asked before anything is drawn, so a gated-out call does not consume the stream
        // and a seeded test stays repeatable when the gate changes.
        if (chaos.Gate is { } gate && !gate())
            return default;

        var latency = chaos.LatencyRate > 0 && dice.Next() < chaos.LatencyRate ? chaos.Latency : TimeSpan.Zero;
        var faults = chaos.FaultRate > 0 && dice.Next() < chaos.FaultRate;

        return new ChaosRoll(latency, faults);
    }

    /// <summary>What a failing call throws. See <see cref="Chaos.Fault" /> for why the default is this one.</summary>
    internal static Exception FaultFor(Chaos chaos) =>
        chaos.Fault?.Invoke() ?? new IOException("Injected by NResilience.Testing.Chaos.");
}
