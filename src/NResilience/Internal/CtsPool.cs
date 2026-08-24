namespace NResilience.Internal;

/// <summary>
/// The timeout-source arrangement.
/// <list type="bullet">
///   <item>
///     A pooled source drives the timer and is <b>never</b> handed to user code, because
///     <c>TryReset</c> preserves token identity: a callback that outlived its attempt would
///     otherwise observe the next operation's cancellation.
///   </item>
///   <item>
///     A source that has actually fired is poison - <c>TryReset</c> returns false once cancelled -
///     so it is disposed rather than returned.
///   </item>
///   <item>
///     The pool is used only when the policy's <see cref="TimeProvider"/> is
///     <see cref="TimeProvider.System"/>. <c>TryReset()</c> always returns false on a source
///     constructed with a custom provider, because the runtime type-tests its timer. Tests get
///     virtual time; production gets the pool.
///   </item>
/// </list>
/// </summary>
internal static class CtsPool
{
    [ThreadStatic]
    private static CancellationTokenSource? t_pooled;

    public static bool IsPoolable(TimeProvider time) => ReferenceEquals(time, TimeProvider.System);

    public static CancellationTokenSource Rent(TimeProvider time)
    {
        if (!IsPoolable(time))
        {
            // CancelAfter() correctly drives an injected provider's timer, so virtual time still
            // cancels the attempt. Only TryReset() is unavailable, which costs pooling, not
            // correctness.
            return new CancellationTokenSource(Timeout.InfiniteTimeSpan, time);
        }

        var cached = t_pooled;
        if (cached is null)
        {
            return new CancellationTokenSource();
        }

        t_pooled = null;
        return cached;
    }

    public static void Return(CancellationTokenSource source, TimeProvider time)
    {
        if (!IsPoolable(time) || !source.TryReset())
        {
            source.Dispose();
            return;
        }

        // Dispose the previous tenant before overwriting the slot, rather than leaving it for
        // the finalizer: each pooled source holds a TimerQueueTimer, and a leaked timer defers
        // its disposal to a GC that may not come. Bounded to one per thread, but a thread that
        // runs many short operations through the pool would otherwise accumulate them.
        t_pooled?.Dispose();
        t_pooled = source;
    }
}
