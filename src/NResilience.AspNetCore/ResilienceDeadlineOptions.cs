namespace NResilience.AspNetCore;

/// <summary>
///     What the inbound deadline middleware reads, and what it refuses to believe.
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
    ///     <see cref="ResilienceDeadline.Header" />, which is what
    ///     <see cref="HttpResilienceOptions.PropagateDeadline" /> writes.
    /// </summary>
    public string Header { get; set; } = ResilienceDeadline.Header;

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
}
