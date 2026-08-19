using System.Runtime.CompilerServices;

namespace NResilience.Probes;

/// <summary>
/// Consecutive-failure breaker, reduced to the state the executor frame actually touches:
/// one admission check per attempt and one counter update per outcome. Phase 2 owns the
/// real thing; what matters here is that the fused loop pays for a breaker the way it will.
/// </summary>
public sealed class ProbeBreaker
{
    private readonly int _consecutiveFailures;
    private readonly long _breakDurationTicks;
    private int _failures;
    private long _openedAtTicks;

    public ProbeBreaker(int consecutiveFailures = 5, TimeSpan? breakDuration = null)
    {
        _consecutiveFailures = consecutiveFailures;
        _breakDurationTicks = (breakDuration ?? TimeSpan.FromSeconds(15)).Ticks;
    }

    public bool IsOpen => Volatile.Read(ref _openedAtTicks) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnter(TimeProvider time)
    {
        long openedAt = Volatile.Read(ref _openedAtTicks);
        if (openedAt == 0)
        {
            return true;
        }

        if (time.GetUtcNow().UtcTicks - openedAt < _breakDurationTicks)
        {
            return false;
        }

        // Half-open: let one probe through.
        Interlocked.CompareExchange(ref _openedAtTicks, 0, openedAt);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordSuccess() => Volatile.Write(ref _failures, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordFailure(TimeProvider time)
    {
        if (Interlocked.Increment(ref _failures) >= _consecutiveFailures)
        {
            Volatile.Write(ref _openedAtTicks, time.GetUtcNow().UtcTicks);
        }
    }

    public void Reset()
    {
        Volatile.Write(ref _failures, 0);
        Volatile.Write(ref _openedAtTicks, 0);
    }
}

/// <summary>
/// Client-side retry token bucket. One <see cref="TrySpend"/> per retry decision and one
/// <see cref="Refund"/> per success, which is the whole of what the executor frame sees.
/// </summary>
public sealed class ProbeBudget
{
    private readonly int _capacity;
    private readonly int _refundPerSuccess;
    private int _tokens;

    public ProbeBudget(int capacity = 100, int refundPerSuccess = 1)
    {
        _capacity = capacity;
        _refundPerSuccess = refundPerSuccess;
        _tokens = capacity;
    }

    public int Tokens => Volatile.Read(ref _tokens);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TrySpend()
    {
        int current = Volatile.Read(ref _tokens);
        while (current > 0)
        {
            int observed = Interlocked.CompareExchange(ref _tokens, current - 1, current);
            if (observed == current)
            {
                return true;
            }

            current = observed;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Refund()
    {
        int current = Volatile.Read(ref _tokens);
        if (current >= _capacity)
        {
            return;
        }

        Interlocked.Add(ref _tokens, _refundPerSuccess);
    }

    public void Reset() => Volatile.Write(ref _tokens, _capacity);
}

/// <summary>Thrown when the breaker refuses admission. Stand-in for the shipping exception.</summary>
public sealed class ProbeBreakerOpenException : Exception
{
    public ProbeBreakerOpenException()
        : base("The circuit breaker is open.")
    {
    }
}

/// <summary>Thrown when every attempt has been used. Stand-in for the shipping exception.</summary>
public sealed class ProbeExhaustedException : Exception
{
    public ProbeExhaustedException(int attempts)
        : base($"All {attempts} attempt(s) failed.")
        => Attempts = attempts;

    public int Attempts { get; }
}

/// <summary>Thrown when the operation-wide deadline expires. Stand-in for the shipping exception.</summary>
public sealed class ProbeDeadlineException : Exception
{
    public ProbeDeadlineException()
        : base("The operation deadline expired.")
    {
    }
}
