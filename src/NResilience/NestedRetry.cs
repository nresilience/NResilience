namespace NResilience;

/// <summary>
///     Whether the caller of this logical call is already retrying, and the two halves of carrying
///     that across a process boundary: reading a header into an ambient flag, and recognizing the
///     marker a retrying handler writes.
///     <para>
///         <see cref="HttpResilienceHandler" /> already knows when it is nested inside another
///         retrying handler in this process, and it stamps
///         <see cref="Header" /> so the next hop can know too. What
///         neither half can do is get that fact from an inbound request into the outbound calls the
///         request makes - the middle of a three-hop chain, which is exactly where amplification is
///         happening and the only place it was invisible.
///     </para>
/// </summary>
/// <remarks>
///     A bool rather than a value with state: unlike a deadline, the flag does not decay, so there is
///     no equivalent of <c>AmbientDeadline.Ambient</c> and no clock to read it against.
/// </remarks>
public static class NestedRetry
{
    /// <summary>
    ///     The header a retrying client stamps on every request it can retry, so the service receiving it
    ///     can see that its caller will retry.
    ///     <para>
    ///         Retries compose multiplicatively - three layers each retrying three times is 27 attempts at
    ///         the bottom - and the amplification is invisible from any single layer. A service that reads
    ///         this header off its inbound request knows it is already being retried, which is the
    ///         information it needs to stop retrying again underneath.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     A <see cref="CallEventKind.NestedRetry" /> event is raised when a request that already
    ///     carries this header is about to be retried again, and when one retrying handler executes
    ///     inside another's attempt in the same process. The library reports it and does nothing else:
    ///     silently dropping the caller's configured retries would be a bigger surprise than the
    ///     amplification.
    /// </remarks>
    public const string Header = "X-NResilience-Retrying";

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
    public static Scope Begin(bool callerRetrying)
    {
        var previous = Current.Value;
        Current.Value = callerRetrying;
        return new Scope(previous);
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
    ///     <see cref="AmbientDeadline.Scope" /> is: the middleware that publishes it runs on
    ///     every request.
    /// </summary>
    public readonly struct Scope : IDisposable
    {
        private readonly bool _previous;

        internal Scope(bool previous)
        {
            _previous = previous;
        }

        /// <summary>Restores the previous flag.</summary>
        public void Dispose() => Current.Value = _previous;
    }
}
