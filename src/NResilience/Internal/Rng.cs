using System.Numerics;
using System.Runtime.CompilerServices;

namespace NResilience.Internal;

/// <summary>
/// A thread-static xoshiro128** for jitter. No locking, no shared <see cref="Random"/>, and no
/// contention on <c>Random.Shared</c> — jitter is drawn on a path whose whole purpose is to be
/// taken by many threads at once during an incident.
/// </summary>
internal static class Rng
{
    [ThreadStatic]
    private static uint t_s0, t_s1, t_s2, t_s3;

    [ThreadStatic]
    private static bool t_seeded;

    /// <summary>A uniform double in [0, 1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double NextDouble() => (NextUInt32() >> 8) * (1.0 / (1u << 24));

    private static uint NextUInt32()
    {
        if (!t_seeded)
        {
            Seed();
        }

        uint result = BitOperations.RotateLeft(t_s1 * 5, 7) * 9;
        uint t = t_s1 << 9;

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
        ulong x = (ulong)Environment.TickCount64
                  ^ ((ulong)Environment.CurrentManagedThreadId << 32)
                  ^ 0x9E3779B97F4A7C15UL;

        t_s0 = SplitMix(ref x);
        t_s1 = SplitMix(ref x);
        t_s2 = SplitMix(ref x);
        t_s3 = SplitMix(ref x);
        if ((t_s0 | t_s1 | t_s2 | t_s3) == 0)
        {
            t_s0 = 1;
        }

        t_seeded = true;

        static uint SplitMix(ref ulong state)
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return (uint)((z ^ (z >> 31)) >> 32);
        }
    }
}
