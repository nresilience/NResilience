namespace NResilience.AspNetCore;

/// <summary>
///     What the inbound middleware reads, and what it refuses to believe.
///     <para>
///         One pass reads both propagated values: the deadline the caller is waiting on, and how much
///         the work matters. They travel together, they are read from the same headers collection, and
///         a second middleware would be a second pass over the same request for one more lookup.
///     </para>
/// </summary>
/// <remarks>
///     A mutable options class rather than a record, because this is the type a configure callback
///     configures - <c>o =&gt; o.Header = "grpc-timeout"</c> - and that is the shape the ecosystem
///     binds to.
/// </remarks>
public sealed class ResilienceDeadlineOptions
{
    /// <summary>
    ///     The header carrying how long the caller is still waiting, in whole milliseconds. Defaults to
    ///     <see cref="AmbientDeadline.Header" />, which is what
    ///     <see cref="HttpResilienceOptions.PropagateDeadline" /> writes.
    /// </summary>
    public string Header { get; set; } = AmbientDeadline.Header;

    /// <summary>
    ///     The longest inbound deadline this service will believe. Null - the default - believes any of
    ///     them.
    ///     <para>
    ///         The header is caller-controlled input, and a deadline is only ever used to make this
    ///         service's own bounds tighter, so an absurd value is harmless rather than dangerous: a
    ///         caller claiming to wait an hour gets the policy's own deadline. Set this when you would
    ///         rather cap what a caller can ask for than have one long-lived request hold a connection
    ///         through a whole outage.
    ///     </para>
    /// </summary>
    public TimeSpan? Maximum { get; set; }

    /// <summary>
    ///     How much of the inbound deadline this service keeps for itself - serializing a response,
    ///     writing an audit record, whatever has to happen after the last outbound call returns.
    ///     <see cref="TimeSpan.Zero" /> by default.
    ///     <para>
    ///         Subtracted from what the caller sent, so outbound calls see less time than the caller is
    ///         waiting for and the difference is left to finish the request in. A reserve at or above the
    ///         inbound deadline leaves nothing, and every outbound call bounded by it fails immediately
    ///         with <see cref="DeadlineExceededException" /> - which is the correct answer for a request
    ///         that arrived with less time than it takes to answer.
    ///     </para>
    /// </summary>
    public TimeSpan Reserve { get; set; }

    /// <summary>
    ///     Whether the pass also reads how much the caller says the work matters. On by default: the
    ///     middleware is already opt-in, and once it is registered a second header lookup is the whole
    ///     cost.
    ///     <para>
    ///         The level is published for the rest of the request, so a policy with
    ///         <see cref="Resilience.UseAmbientCriticality" /> set will not hedge a backfill or spend a
    ///         depleted retry budget on one, and <see cref="Resilience.Admit" /> can read
    ///         <see cref="AmbientCriticality.Current" /> to decide what the level means for this
    ///         service. Nothing is refused by reading it.
    ///     </para>
    /// </summary>
    public bool ReadCriticality { get; set; } = true;

    /// <summary>
    ///     The header carrying how much the work matters, as one of the four
    ///     <see cref="Criticality" /> names. Defaults to <see cref="AmbientCriticality.Header" />,
    ///     which is what <see cref="HttpResilienceOptions.PropagateCriticality" /> writes.
    /// </summary>
    /// <remarks>
    ///     A value this service will not believe - an unknown name, or <c>CriticalPlus</c>, which
    ///     clamps at <see cref="Criticality.Critical" /> - leaves the request at
    ///     <see cref="Criticality.Critical" />. See <see cref="AmbientCriticality.TryParse" />.
    /// </remarks>
    public string CriticalityHeader { get; set; } = AmbientCriticality.Header;

    /// <summary>
    ///     Whether a request that arrives with no usable time left is refused with <c>504</c> and an
    ///     RFC 9457 problem document, rather than run. Off by default.
    ///     <para>
    ///         "No usable time left" means the inbound deadline is at or below <see cref="Reserve" />,
    ///         so nothing remains for the work after this service's own share. That is the highest
    ///         value overload protection there is when it applies, because the work is provably
    ///         undeliverable - Finagle's <c>DeadlineFilter</c> is the same idea.
    ///     </para>
    ///     <para>
    ///         Off by default, and it stays off, because the request may still be answerable from
    ///         cache or from work that costs nobody anything, and refusing it is a decision about this
    ///         service that the library has no standing to make. Turn it on when you know the handler
    ///         has no such path.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <b>Pair it with <see cref="Reserve" />.</b> A deadline header carries a positive count of
    ///     milliseconds or it carries nothing at all, so with no reserve set there is no request this
    ///     can refuse. <see cref="Reserve" /> is what states how much time answering actually costs,
    ///     and this turns "arrived with less than that" into one refusal instead of a handler that
    ///     runs and fails every outbound call it makes.
    /// </remarks>
    public bool RejectExpired { get; set; }
}
