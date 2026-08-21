namespace NResilience.Probes;

/// <summary>
/// Measures six cancellation facts that drive the executor's timeout implementation and two 
/// behavioral facts about <see cref="TimeProvider"/>, re-run on the target TFMs.
///
/// These are re-measured rather than trusting Appendix B because the timeout design 
/// - pooling the timer source, linking per attempt, and falling back to per-call construction 
/// for custom <see cref="TimeProvider"/> instances - depends on them. If <c>TryReset</c> 
/// changes behavior with custom or system providers, the arrangement must change.
/// </summary>
public static class CtsFacts
{
    private static int s_sink;

    public static int Sink => s_sink;

    public static ValueTask<int> NewSource()
    {
        using var cts = new CancellationTokenSource();
        s_sink += cts.Token.CanBeCanceled ? 1 : 0;
        return new ValueTask<int>(s_sink);
    }

    public static ValueTask<int> NewSourceWithCancelAfter()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        s_sink += cts.Token.CanBeCanceled ? 1 : 0;
        return new ValueTask<int>(s_sink);
    }

    public static ValueTask<int> LinkedFromCancellable()
    {
        using var outer = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outer.Token);
        s_sink += linked.Token.CanBeCanceled ? 1 : 0;
        return new ValueTask<int>(s_sink);
    }

    public static ValueTask<int> LinkedFromNone()
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        s_sink += linked.Token.CanBeCanceled ? 1 : 0;
        return new ValueTask<int>(s_sink);
    }

    public static ValueTask<int> LinkedFromTwoCancellable()
    {
        using var a = new CancellationTokenSource();
        using var b = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(a.Token, b.Token);
        s_sink += linked.Token.CanBeCanceled ? 1 : 0;
        return new ValueTask<int>(s_sink);
    }

    /// <summary>The arrangement the executor uses: a pooled timer source that is reset and reused.</summary>
    public static ValueTask<int> PooledSourceReused()
    {
        CancellationTokenSource cts = CtsPool.Rent(TimeProvider.System);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        s_sink += cts.Token.CanBeCanceled ? 1 : 0;
        CtsPool.Return(cts, TimeProvider.System);
        return new ValueTask<int>(s_sink);
    }

    /// <summary>Implements timeout by racing a delay - an implementation the executor deliberately avoids.</summary>
    public static ValueTask<int> DelayCreatedThenCancelled()
    {
        using var cts = new CancellationTokenSource();
        Task delay = Task.Delay(TimeSpan.FromSeconds(30), cts.Token);
        cts.Cancel();
        s_sink += delay.IsCompleted ? 1 : 0;
        return new ValueTask<int>(s_sink);
    }

    /// <summary>
    /// Calls <c>TryReset()</c> on a source built with a custom provider. The runtime type-tests 
    /// for <c>TimerQueueTimer</c>, and a custom provider's <c>ITimer</c> does not match. 
    /// Consequently, pooling and injectable <see cref="TimeProvider"/> testability 
    /// are mutually exclusive.
    /// </summary>
    public static bool TryResetWithCustomProvider(TimeProvider provider)
    {
        using var cts = new CancellationTokenSource(Timeout.InfiniteTimeSpan, provider);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        return cts.TryReset();
    }

    /// <summary>Calls the same method on the system provider, which is the case the pool relies on.</summary>
    public static bool TryResetWithSystemProvider()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        return cts.TryReset();
    }

    /// <summary>A source that has already fired is poison; the pool must discard it rather than reuse it.</summary>
    public static bool TryResetAfterCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        return cts.TryReset();
    }

    /// <summary>
    /// <c>CancelAfter()</c> correctly drives an injected provider's timer, so virtual time 
    /// cancels an attempt in tests even though the source cannot be pooled.
    /// </summary>
    public static CancellationTokenSource CancelAfterOnProvider(TimeProvider provider, TimeSpan timeout)
    {
        var cts = new CancellationTokenSource(Timeout.InfiniteTimeSpan, provider);
        cts.CancelAfter(timeout);
        return cts;
    }
}
