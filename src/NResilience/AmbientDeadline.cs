using System.Globalization;

namespace NResilience;

/// <summary>
///     The deadline this logical call inherited from whoever called it, and the two halves of putting
///     one on the wire: reading a header into an ambient deadline, and writing one back out.
///     <para>
///         A deadline is the honest bound on a call, and it stops at the process edge unless somebody
///         carries it across. A service with 200 ms left that sends a request the peer will happily
///         work on for 10 seconds has already produced garbage, and neither side can tell. gRPC has
///         propagated deadlines since forever; .NET HTTP stacks generally do not.
///     </para>
///     <para>
///         The value here is ambient rather than a parameter because the code that reads it - the
///         executor, several frames below the request handler - cannot be given one. Reading an
///         <see cref="AsyncLocal{T}" /> is not free, which is why the executor only reads it for a
///         policy whose <see cref="Resilience.UseAmbientDeadline" /> is set, and why that property
///         defaults to false: everyone else pays one branch.
///     </para>
/// </summary>
/// <example>
///     <code>
/// // Inbound: the deadline a caller sent us, for the length of this request.
/// using var scope = AmbientDeadline.Begin(TimeSpan.FromMilliseconds(200));
/// 
/// // Anything running inside the scope with UseAmbientDeadline set is bounded by whichever of the
/// // two deadlines is tighter.
/// var policy = Resilience.Default with { UseAmbientDeadline = true };
/// </code>
/// </example>
public static class AmbientDeadline
{
    /// <summary>
    ///     The header the HTTP integration reads and writes by default: whole milliseconds left, as a
    ///     positive integer.
    /// </summary>
    /// <remarks>
    ///     There is no standard for this on plain HTTP - <c>grpc-timeout</c> is gRPC's, and its value
    ///     carries a unit suffix rather than being a bare count of milliseconds. This name is the one
    ///     both halves of the library agree on, and both halves let you change it.
    /// </remarks>
    public const string Header = "X-Deadline-Ms";

    private static readonly AsyncLocal<Ambient?> Current = new();

    /// <summary>
    ///     How long the inbound deadline has left, or null when this call did not inherit one.
    ///     <see cref="TimeSpan.Zero" /> when it inherited one that has since expired.
    /// </summary>
    public static TimeSpan? Remaining => Current.Value?.Left();

    /// <summary>
    ///     Publishes an inbound deadline for the current logical call, and everything it awaits.
    /// </summary>
    /// <param name="remaining">How long is left. Non-positive values are read as expired, not as unbounded.</param>
    /// <param name="time">The clock the remaining time is measured against. Defaults to <see cref="TimeProvider.System" />.</param>
    /// <returns>A scope that restores the previous ambient deadline when disposed.</returns>
    /// <remarks>
    ///     <see cref="Timeout.InfiniteTimeSpan" /> clears the deadline for the scope rather than
    ///     publishing an unbounded one: "no bound" and "no deadline" are the same statement, and a
    ///     nested call should not be told a caller is waiting forever.
    /// </remarks>
    public static Scope Begin(TimeSpan remaining, TimeProvider? time = null)
    {
        var previous = Current.Value;

        Current.Value = remaining == Timeout.InfiniteTimeSpan
            ? null
            : new Ambient(remaining, time ?? TimeProvider.System);

        return new Scope(previous);
    }

    /// <summary>
    ///     Reads a deadline header. The format is whole milliseconds as a positive integer, and
    ///     anything else - empty, negative, zero, a duration with a unit on it - is no deadline at all.
    /// </summary>
    /// <param name="value">The header value.</param>
    /// <param name="remaining">How long the caller says is left.</param>
    /// <returns>True when <paramref name="value" /> carried a usable deadline.</returns>
    /// <remarks>
    ///     A header this process did not write is caller-controlled input, so the parse is strict and
    ///     failure is silent: an unreadable deadline leaves the call bounded by its own policy, which is
    ///     what it would have been without the header. Values above <see cref="int.MaxValue" />
    ///     milliseconds are read as no deadline for the same reason - a caller claiming to wait 25 days
    ///     is not carrying a deadline worth propagating.
    /// </remarks>
    public static bool TryParse(string? value, out TimeSpan remaining)
    {
        remaining = default;

        if (string.IsNullOrEmpty(value))
            return false;

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) || milliseconds <= 0)
            return false;

        remaining = TimeSpan.FromMilliseconds(milliseconds);
        return true;
    }

    /// <summary>
    ///     Writes a deadline header value: whole milliseconds, rounded down, and never below 1.
    /// </summary>
    /// <param name="remaining">How long is left.</param>
    /// <returns>The header value, or null when there is nothing to say.</returns>
    /// <remarks>
    ///     Rounding down is the conservative direction - the peer is told slightly less time than we
    ///     will actually wait - and the floor of 1 keeps a sub-millisecond remainder from being written
    ///     as a zero this library's own parser would then read as "no deadline".
    /// </remarks>
    public static string? Format(TimeSpan remaining)
    {
        if (remaining == Timeout.InfiniteTimeSpan || remaining <= TimeSpan.Zero)
            return null;

        var milliseconds = (long)remaining.TotalMilliseconds;
        return (milliseconds < 1 ? 1 : milliseconds).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     Whichever of the configured deadline and the inbound one is tighter. The executor's one read
    ///     of the <see cref="AsyncLocal{T}" />, taken once per call.
    /// </summary>
    internal static TimeSpan Clamp(TimeSpan configured)
    {
        if (Current.Value is not { } ambient)
            return configured;

        var left = ambient.Left();

        if (configured == Timeout.InfiniteTimeSpan)
            return left;

        return left < configured ? left : configured;
    }

    /// <summary>
    ///     Restores the ambient deadline the scope replaced.
    ///     <para>
    ///         A struct, and one field wide: the middleware that publishes a deadline does so on every
    ///         request, and a disposable that allocates to undo an assignment would be the only thing on
    ///         that path that did.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     An <see cref="AsyncLocal{T}" /> assignment made inside a child execution context does not
    ///     flow back to the parent, so this is belt and braces in the common case and load-bearing when
    ///     scopes nest on one context - a request handler that hands part of its own budget to a
    ///     sub-operation.
    /// </remarks>
    public readonly struct Scope : IDisposable
    {
        private readonly Ambient? _previous;

        internal Scope(Ambient? previous)
        {
            _previous = previous;
        }

        /// <summary>Restores the previous ambient deadline.</summary>
        public void Dispose() => Current.Value = _previous;
    }

    /// <summary>
    ///     An inbound deadline as the pair that survives being awaited: when it was published, and how
    ///     long it had left then. Storing the remaining time on its own would make it stop decaying.
    /// </summary>
    internal sealed class Ambient(TimeSpan remaining, TimeProvider time)
    {
        private readonly long _start = time.GetTimestamp();

        internal TimeSpan Left()
        {
            var left = remaining - time.GetElapsedTime(_start);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }
}
