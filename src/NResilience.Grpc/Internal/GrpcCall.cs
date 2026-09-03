using Grpc.Core;

namespace NResilience.Grpc.Internal;

/// <summary>
///     The parts a unary call and a server-streaming call do identically: the retry marker, the
///     deadline arithmetic, and the ambient clamp.
///     <para>
///         Shared rather than duplicated because these are the places where the two shapes must not
///         drift. What they do differently - which ceiling reaches the wire, and which exceptions are
///         translated - stays in the two call types, where the difference is the point.
///     </para>
/// </summary>
internal static class GrpcCall
{
    /// <summary>
    ///     The metadata key the retry marker travels under: the HTTP header's name, lowercased,
    ///     because gRPC metadata keys are lowercase ASCII and <see cref="Metadata" /> normalizes them.
    ///     Same fact, same name, two transports.
    /// </summary>
    internal const string NestedRetryKey = "x-nresilience-retrying";

    /// <summary>Whether the caller's own metadata already carries the marker, so it is not stamped twice.</summary>
    internal static bool CarriesRetryMarker(in CallOptions options)
    {
        if (options.Headers is not { } headers)
            return false;

        // A loop rather than LINQ: this runs on every retrying call.
        foreach (var entry in headers)
        {
            if (!entry.IsBinary && string.Equals(entry.Key, NestedRetryKey, StringComparison.Ordinal)
                                && ResilienceNestedRetry.IsMarker(entry.Value))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     The caller's metadata plus the marker, in a fresh <see cref="Metadata" />.
    /// </summary>
    /// <remarks>
    ///     A new <see cref="Metadata" /> per attempt, never the caller's own. The caller's object is
    ///     theirs and may be reused across calls; stamping into it accumulates one duplicate entry per
    ///     attempt per call, and gRPC sends every entry. One small allocation buys a bug that only
    ///     appears under retry - the only condition this code runs under.
    /// </remarks>
    internal static CallOptions Stamp(in CallOptions options)
    {
        var headers = new Metadata();

        if (options.Headers is { } caller)
        {
            foreach (var entry in caller)
            {
                headers.Add(entry);
            }
        }

        headers.Add(NestedRetryKey, ResilienceNestedRetry.Marker);
        return options.WithHeaders(headers);
    }

    /// <summary>How much of a call's deadline is left, or <see cref="Timeout.InfiniteTimeSpan" /> when it has none.</summary>
    internal static TimeSpan Remaining(TimeProvider time, long start, TimeSpan deadline)
    {
        if (deadline == Timeout.InfiniteTimeSpan)
            return Timeout.InfiniteTimeSpan;

        var left = deadline - time.GetElapsedTime(start);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>Whichever of two spans is the tighter bound, reading <see cref="Timeout.InfiniteTimeSpan" /> as no bound at all.</summary>
    internal static TimeSpan Tighter(TimeSpan left, TimeSpan right)
    {
        if (left == Timeout.InfiniteTimeSpan)
            return right;

        if (right == Timeout.InfiniteTimeSpan)
            return left;

        return left < right ? left : right;
    }

    /// <summary>The executor's ambient-deadline clamp, over the public half of <see cref="ResilienceDeadline" />.</summary>
    internal static TimeSpan Clamp(TimeSpan configured)
    {
        if (ResilienceDeadline.Remaining is not { } left)
            return configured;

        if (configured == Timeout.InfiniteTimeSpan)
            return left;

        return left < configured ? left : configured;
    }

    /// <summary>
    ///     The whole-call deadline a policy imposes, ambient clamp included - resolved once, before
    ///     the executor starts, because the wire deadline has to be computed before it does.
    /// </summary>
    internal static TimeSpan DeadlineFor(Resilience policy) =>
        policy.UseAmbientDeadline ? Clamp(policy.Deadline) : policy.Deadline;
}
