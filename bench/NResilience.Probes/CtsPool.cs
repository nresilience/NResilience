namespace NResilience.Probes;

/// <summary>
///     The timeout-source arrangement: a pooled source drives the timer, and is never handed to
///     user code, because <c>TryReset</c> preserves token identity.
///     <list type="bullet">
///         <item>
///             a pooled source drives the timer, and is never handed to user code, because
///             <c>TryReset</c> preserves token identity;
///         </item>
///         <item>
///             each attempt links that source with the caller's token to produce the token the
///             callback actually receives, and disposes the link at the end of the attempt;
///         </item>
///         <item>
///             the pool is used only when the policy's <see cref="TimeProvider" /> is
///             <see cref="TimeProvider.System" />, because <c>TryReset()</c> always returns false
///             on a source constructed with a custom provider. That is re-confirmed on both
///             target TFMs; see <c>CtsFacts</c>.
///         </item>
///     </list>
/// </summary>
internal static class CtsPool
{
    [ThreadStatic] private static CancellationTokenSource? t_cached;

    public static bool IsPoolable(TimeProvider time) => ReferenceEquals(time, TimeProvider.System);

    public static CancellationTokenSource Rent(TimeProvider time)
    {
        if (!IsPoolable(time))
            return new CancellationTokenSource(TimeSpan.FromMilliseconds(-1), time);

        var cached = t_cached;

        if (cached is null)
            return new CancellationTokenSource();

        t_cached = null;
        return cached;
    }

    public static void Return(CancellationTokenSource source, TimeProvider time)
    {
        // A source that actually fired is poison: TryReset returns false once cancelled.
        if (!IsPoolable(time) || !source.TryReset())
        {
            source.Dispose();
            return;
        }

        t_cached = source;
    }
}
