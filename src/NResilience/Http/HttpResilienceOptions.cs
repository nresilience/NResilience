namespace NResilience.Http;

/// <summary>
///     The things the HTTP integration decides that a <see cref="Resilience" /> policy cannot, because
///     they are properties of HTTP rather than of resilience.
/// </summary>
/// <remarks>
///     A mutable options class rather than a record, because this is the type an options callback
///     configures - <c>o =&gt; o.RetryUnsafeMethods = true</c> - and that is the shape
///     <c>Microsoft.Extensions.Options</c> binds to.
/// </remarks>
public sealed class HttpResilienceOptions
{
    /// <summary>
    ///     Whether POST and PATCH are retried. Off by default.
    ///     <para>
    ///         GET, HEAD, PUT, DELETE, OPTIONS and TRACE are idempotent by definition and are retried.
    ///         POST and PATCH are not, and a retried POST is a duplicate order, a duplicate message or a
    ///         duplicate charge. Microsoft's standard handler retries POST by default; the report that it
    ///         creates duplicates was declined after 33 comments, and an opt-out shipped instead.
    ///     </para>
    ///     <para>
    ///         Turning this on is a statement about a whole client. Per request,
    ///         <see cref="ResilienceHttp.Repeatable" /> is the finer instrument and it wins.
    ///     </para>
    /// </summary>
    public bool RetryUnsafeMethods { get; set; }

    /// <summary>
    ///     Whether the integration sets <c>HttpClient.Timeout</c> to
    ///     <see cref="Timeout.InfiniteTimeSpan" />. On by default.
    ///     <para>
    ///         The transport timeout defaults to 100 seconds and covers the entire retry sequence rather
    ///         than one attempt, so it silently caps any policy whose deadline exceeds it. The bound
    ///         belongs on <see cref="Resilience.Deadline" />, where it is visible and where the attempt
    ///         timeout is expressed in the same vocabulary.
    ///     </para>
    ///     <para>
    ///         It is honored by whoever builds the client - <see cref="ResilienceHttp.CreateClient" />, or
    ///         the DI registration - because a <c>DelegatingHandler</c> cannot reach the client in front
    ///         of it. Setting it false on a handler you hand to your own <see cref="HttpClient" /> does
    ///         nothing at all.
    ///     </para>
    /// </summary>
    public bool OwnTransportTimeout { get; set; } = true;

    /// <summary>
    ///     Whether each host gets its own circuit breaker. On by default.
    ///     <para>
    ///         One breaker across every host means a dead host trips calls to the healthy ones, which is
    ///         the blast-radius inversion a breaker exists to prevent - and it is the single most
    ///         confusing thing in the .NET resilience ecosystem, because scope is otherwise an emergent
    ///         property of where a pipeline happened to be registered.
    ///     </para>
    ///     <para>
    ///         A policy that already carries a <see cref="Resilience.Breaker" /> keeps it: an explicit
    ///         breaker is a deliberate scope decision and this switch does not overrule it.
    ///     </para>
    /// </summary>
    public bool BreakerPerHost { get; set; } = true;

    /// <summary>
    ///     The settings the per-host breakers are created with. Null means
    ///     <see cref="BreakerSettings" />'s own defaults.
    /// </summary>
    public BreakerSettings? BreakerSettings { get; set; }

    /// <summary>
    ///     Whether each host gets its own retry budget. On by default, and for the same reason as
    ///     <see cref="BreakerPerHost" />: a storm against one host must not throttle retries to
    ///     another.
    ///     <para>
    ///         A policy carrying an explicit <see cref="Resilience.Budget" /> keeps it, including
    ///         <see cref="RetryBudget.None" />.
    ///     </para>
    /// </summary>
    public bool BudgetPerHost { get; set; } = true;

    /// <summary>
    ///     The number of hosts the handler keeps a breaker and a budget for. 1024 by default; null is
    ///     unbounded.
    ///     <para>
    ///         The set of hosts one client talks to is normally a property of the application, and the
    ///         cap is invisible to it. A proxy, a crawler or a webhook dispatcher reaches the cap, and
    ///         the least-recently-seen hosts are dropped - a host that returns after being dropped
    ///         starts again with a closed breaker and a full budget, which is the right reading for a
    ///         host nobody has spoken to in a while.
    ///     </para>
    ///     <para>
    ///         Eviction is approximate, so the registry can sit a little over the cap while a sweep
    ///         catches up. A value of zero or less is read as unbounded.
    ///     </para>
    /// </summary>
    public int? MaxHosts { get; set; } = 1024;

    /// <summary>
    ///     Whether each outbound attempt carries how long this side is going to wait for it. Off by
    ///     default.
    ///     <para>
    ///         The value is the attempt's own ceiling - <c>min(<see cref="Resilience.AttemptTimeout" />,
    ///         time left on the deadline)</c> - in whole milliseconds, written to
    ///         <see cref="DeadlineHeader" />, and recomputed for every attempt and every hedged leg,
    ///         because each one has less of the deadline left than the last. A peer that reads it can
    ///         stop work nobody is waiting for; a peer that ignores it is unaffected.
    ///     </para>
    ///     <para>
    ///         Off by default because a header is only useful when the other side reads it, and the
    ///         library cannot know that. Turning it on costs one header on the request and no allocation
    ///         the request was not already making.
    ///     </para>
    /// </summary>
    public bool PropagateDeadline { get; set; }

    /// <summary>
    ///     The header <see cref="PropagateDeadline" /> writes. Defaults to
    ///     <see cref="ResilienceDeadline.Header" />, which is what the inbound half reads.
    /// </summary>
    /// <remarks>
    ///     There is no standard for this on plain HTTP, so the name is yours to change - one service
    ///     mesh's convention is another's. <c>grpc-timeout</c> is not a drop-in value for it: gRPC's
    ///     format carries a unit suffix rather than a bare count of milliseconds, and the gRPC client
    ///     stack already propagates its own deadlines from <c>CallOptions.Deadline</c>.
    /// </remarks>
    public string DeadlineHeader { get; set; } = ResilienceDeadline.Header;

    /// <summary>
    ///     Whether the handler stamps <see cref="ResilienceHttp.NestedRetryHeader" /> on outbound
    ///     requests and reports nesting it detects. On by default; it costs one header on a request
    ///     that can be retried.
    /// </summary>
    public bool DetectNestedRetries { get; set; } = true;
}
