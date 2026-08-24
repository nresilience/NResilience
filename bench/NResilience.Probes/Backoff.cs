using System.Numerics;
using System.Runtime.CompilerServices;

namespace NResilience.Probes;

/// <summary>
///     Implements exponential backoff with full jitter, a hard cap, and separate base delays
///     for transient and throttled verdicts. Jitter draws from a thread-static xoshiro128**
///     to ensure the hot path takes no lock and touches no shared <see cref="Random" />.
/// </summary>
public static class ProbeBackoff
{
    [ThreadStatic] private static uint t_s0, t_s1, t_s2, t_s3;

    [ThreadStatic] private static bool t_seeded;

    public static TimeSpan Compute(in FusedPolicy policy, in Verdict verdict, int attemptsSoFar)
    {
        if (!policy.UseBackoff)
            return TimeSpan.Zero;

        if (verdict.RetryAfter is { } pushback)
        {
            // A server telling you when to come back beats any client-side curve.
            return pushback > policy.BackoffMax ? policy.BackoffMax : pushback;
        }

        var @base = verdict.Kind == VerdictKind.Throttled
            ? policy.ThrottledBase
            : policy.TransientBase;

        var ticks = @base.Ticks * Math.Pow(policy.BackoffFactor, attemptsSoFar - 1);
        var capped = Math.Min(ticks, policy.BackoffMax.Ticks);

        if (!policy.Jitter)
            return TimeSpan.FromTicks((long)capped);

        // Full jitter: random(0, computed). A narrow band around a shared base still leaves a
        // synchronized pulse, which is the thing jitter exists to destroy.
        return TimeSpan.FromTicks((long)(capped * NextDouble()));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double NextDouble() => (NextUInt32() >> 8) * (1.0 / (1u << 24));

    private static uint NextUInt32()
    {
        if (!t_seeded)
            Seed();

        var result = BitOperations.RotateLeft(t_s1 * 5, 7) * 9;
        var t = t_s1 << 9;

        t_s2 ^= t_s0;
        t_s3 ^= t_s1;
        t_s1 ^= t_s2;
        t_s0 ^= t_s3;
        t_s2 ^= t;
        t_s3 = BitOperations.RotateLeft(t_s3, 11);

        return result;
    }

    private static void Seed()
    {
        var x = (ulong)Environment.TickCount64 ^ ((ulong)Environment.CurrentManagedThreadId << 32) ^ 0x9E3779B97F4A7C15UL;
        t_s0 = SplitMix(ref x);
        t_s1 = SplitMix(ref x);
        t_s2 = SplitMix(ref x);
        t_s3 = SplitMix(ref x);

        if ((t_s0 | t_s1 | t_s2 | t_s3) == 0)
            t_s0 = 1;

        t_seeded = true;

        static uint SplitMix(ref ulong state)
        {
            state += 0x9E3779B97F4A7C15UL;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return (uint)((z ^ (z >> 31)) >> 32);
        }
    }
}
