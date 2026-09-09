using System.Runtime.CompilerServices;

namespace NResilience.Probes;

/// <summary>
///     A consecutive-failure breaker reduced to the state the executor frame actually touches:
///     one admission check per attempt and one counter update per outcome. The full version
///     is implemented in the shipping library; this stand-in ensures the fused loop incurs
///     the same cost as the shipping version.
/// </summary>
public sealed class ProbeBreaker(int consecutiveFailures = 5, TimeSpan? breakDuration = null)
{
    private readonly long _breakDurationTicks = (breakDuration ?? TimeSpan.FromSeconds(15)).Ticks;
    private int _failures;
    private long _openedAtTicks;

    public bool IsOpen => Volatile.Read(ref _openedAtTicks) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnter(TimeProvider time)
    {
        var openedAt = Volatile.Read(ref _openedAtTicks);

        if (openedAt == 0)
            return true;

        if (time.GetUtcNow().UtcTicks - openedAt < _breakDurationTicks)
            return false;

        // Half-open: let one probe through.
        Interlocked.CompareExchange(ref _openedAtTicks, 0, openedAt);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordSuccess() => Volatile.Write(ref _failures, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordFailure(TimeProvider time)
    {
        if (Interlocked.Increment(ref _failures) >= consecutiveFailures)
            Volatile.Write(ref _openedAtTicks, time.GetUtcNow().UtcTicks);
    }

    public void Reset()
    {
        Volatile.Write(ref _failures, 0);
        Volatile.Write(ref _openedAtTicks, 0);
    }
}

/// <summary>
///     A client-side retry token bucket. It implements one <see cref="TrySpend" /> per retry
///     decision and one <see cref="Refund" /> per success, which is the total state the
///     executor frame accesses.
/// </summary>
public sealed class ProbeBudget(int capacity = 100, int refundPerSuccess = 1)
{
    private readonly int _capacity = capacity;
    private int _tokens = capacity;

    public int Tokens => Volatile.Read(ref _tokens);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TrySpend()
    {
        var current = Volatile.Read(ref _tokens);

        while (current > 0)
        {
            var observed = Interlocked.CompareExchange(ref _tokens, current - 1, current);

            if (observed == current)
                return true;

            current = observed;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Refund()
    {
        var current = Volatile.Read(ref _tokens);

        if (current >= _capacity)
            return;

        Interlocked.Add(ref _tokens, refundPerSuccess);
    }

    public void Reset() => Volatile.Write(ref _tokens, _capacity);
}

/// <summary>Thrown when the breaker refuses admission. This is a stand-in for the shipping exception.</summary>
public sealed class ProbeBreakerOpenException() : Exception("The circuit breaker is open.");

/// <summary>Thrown when every attempt has been used. This is a stand-in for the shipping exception.</summary>
public sealed class ProbeExhaustedException(int attempts) : Exception($"All {attempts} attempt(s) failed.")
{
    public int Attempts { get; } = attempts;
}

/// <summary>Thrown when the operation-wide deadline expires. This is a stand-in for the shipping exception.</summary>
public sealed class ProbeDeadlineException() : Exception("The operation deadline expired.");
