namespace NResilience.Http.Internal;

/// <summary>
///     The outbound half of deadline propagation: what to tell the peer, recomputed for every attempt.
///     <para>
///         The number written is the attempt's own ceiling rather than the whole deadline, because that
///         is what this side is actually going to wait for: a call with a 30 s deadline and a 10 s
///         attempt timeout abandons this request after 10 s, and telling the peer 30 s would invite it
///         to keep working for 20 s that nobody is waiting through.
///     </para>
/// </summary>
/// <param name="header">The header to write.</param>
/// <param name="deadline">The effective deadline for the whole call, already clamped by any inbound one.</param>
/// <param name="attemptTimeout">The policy's per-attempt ceiling.</param>
/// <param name="start">Timestamp the handler started the call at, which is at or before the executor's own.</param>
/// <param name="time">The policy's clock.</param>
/// <remarks>
///     <paramref name="start" /> is taken by the handler rather than read from the executor, which has
///     no way to hand it out. It is taken a little earlier - before the request body is buffered - so
///     the remaining time this reports is a slight underestimate, which is the safe direction to be
///     wrong in.
/// </remarks>
internal readonly struct DeadlineStamp(string header, TimeSpan deadline, TimeSpan attemptTimeout, long start, TimeProvider time)
{
    internal string Header => header;

    /// <summary>
    ///     What to write, or null when there is nothing left to say - an unbounded call, or one whose
    ///     deadline has already run out and which is about to stop anyway.
    /// </summary>
    internal string? Value()
    {
        var remaining = Resilience.Remaining(time, start, deadline);
        return ResilienceDeadline.Format(Resilience.Effective(attemptTimeout, remaining));
    }
}
