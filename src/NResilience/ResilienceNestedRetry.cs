namespace NResilience;

/// <summary>
///     Whether the caller of this logical call is already retrying, and the two halves of carrying
///     that across a process boundary: reading a header into an ambient flag, and recognizing the
///     marker a retrying handler writes.
///     <para>
///         <see cref="ResilienceHandler" /> already knows when it is nested inside another
///         retrying handler in this process, and it stamps
///         <see cref="HttpResilience.NestedRetryHeader" /> so the next hop can know too. What
///         neither half can do is get that fact from an inbound request into the outbound calls the
///         request makes - the middle of a three-hop chain, which is exactly where amplification is
///         happening and the only place it was invisible.
///     </para>
/// </summary>
/// <remarks>
///     A bool rather than a value with state: unlike a deadline, the flag does not decay, so there is
///     no equivalent of <c>ResilienceDeadline.Ambient</c> and no clock to read it against.
/// </remarks>
public static class ResilienceNestedRetry
{
    /// <summary>
    ///     The only value the retry marker header ever carries. The header is a presence marker, and a
    ///     value other than this one is not something this library wrote.
    /// </summary>
    public const string Marker = "1";

    private static readonly AsyncLocal<bool> Current = new();

    /// <summary>
    ///     Whether the caller of this logical call said it was already retrying. False when nobody
    ///     published a flag, which is the case for every call outside a server that reads one.
    /// </summary>
    public static bool IsCallerRetrying => Current.Value;

    /// <summary>Publishes the flag for the current logical call, and everything it awaits.</summary>
    /// <param name="callerRetrying">Whether the caller is already retrying.</param>
    /// <returns>A scope that restores the previous value when disposed.</returns>
    public static NestedRetryScope Begin(bool callerRetrying)
    {
        var previous = Current.Value;
        Current.Value = callerRetrying;
        return new NestedRetryScope(previous);
    }

    /// <summary>
    ///     Whether a header value is the retry marker. The check is exact: a proxy that forwards
    ///     unknown headers can produce an empty one, and a value this library did not write is not
    ///     evidence of anything.
    /// </summary>
    /// <param name="value">The header value.</param>
    /// <returns>True when the value is <see cref="Marker" />.</returns>
    public static bool IsMarker(string? value) => string.Equals(value, Marker, StringComparison.Ordinal);

    /// <summary>
    ///     Restores the flag the scope replaced. A struct, and one field wide, for the same reason
    ///     <see cref="ResilienceDeadline.DeadlineScope" /> is: the middleware that publishes it runs on
    ///     every request.
    /// </summary>
    public readonly struct NestedRetryScope : IDisposable
    {
        private readonly bool _previous;

        internal NestedRetryScope(bool previous)
        {
            _previous = previous;
        }

        /// <summary>Restores the previous flag.</summary>
        public void Dispose() => Current.Value = _previous;
    }
}
