namespace NResilience.Http;

/// <summary>
/// The things the HTTP integration decides that a <see cref="Resilience"/> policy cannot, because
/// they are properties of HTTP rather than of resilience.
/// </summary>
/// <remarks>
/// A mutable options class rather than a record, because this is the type an options callback
/// configures - <c>o =&gt; o.RetryUnsafeMethods = true</c> - and that is the shape
/// <c>Microsoft.Extensions.Options</c> binds to.
/// </remarks>
public sealed class HttpResilienceOptions
{
    /// <summary>
    /// Whether POST and PATCH are retried. Off by default.
    /// <para>
    /// GET, HEAD, PUT, DELETE, OPTIONS and TRACE are idempotent by definition and are retried.
    /// POST and PATCH are not, and a retried POST is a duplicate order, a duplicate message or a
    /// duplicate charge. Microsoft's standard handler retries POST by default; the report that it
    /// creates duplicates was declined after 33 comments, and an opt-out shipped instead.
    /// </para>
    /// <para>
    /// Turning this on is a statement about a whole client. Per request,
    /// <see cref="ResilienceHttp.Repeatable"/> is the finer instrument and it wins.
    /// </para>
    /// </summary>
    public bool RetryUnsafeMethods { get; set; }

    /// <summary>
    /// Whether the integration sets <c>HttpClient.Timeout</c> to
    /// <see cref="Timeout.InfiniteTimeSpan"/>. On by default.
    /// <para>
    /// The transport timeout defaults to 100 seconds and covers the entire retry sequence rather
    /// than one attempt, so it silently caps any policy whose deadline exceeds it. The bound
    /// belongs on <see cref="Resilience.Deadline"/>, where it is visible and where the attempt
    /// timeout is expressed in the same vocabulary.
    /// </para>
    /// <para>
    /// It is honored by whoever builds the client - <see cref="ResilienceHttp.CreateClient"/>, or
    /// the DI registration - because a <c>DelegatingHandler</c> cannot reach the client in front
    /// of it. Setting it false on a handler you hand to your own <see cref="HttpClient"/> does
    /// nothing at all.
    /// </para>
    /// </summary>
    public bool OwnTransportTimeout { get; set; } = true;

    /// <summary>
    /// Whether each host gets its own circuit breaker. On by default.
    /// <para>
    /// One breaker across every host means a dead host trips calls to the healthy ones, which is
    /// the blast-radius inversion a breaker exists to prevent - and it is the single most
    /// confusing thing in the .NET resilience ecosystem, because scope is otherwise an emergent
    /// property of where a pipeline happened to be registered.
    /// </para>
    /// <para>
    /// A policy that already carries a <see cref="Resilience.Breaker"/> keeps it: an explicit
    /// breaker is a deliberate scope decision and this switch does not overrule it.
    /// </para>
    /// </summary>
    public bool BreakerPerHost { get; set; } = true;

    /// <summary>
    /// The settings the per-host breakers are created with. Null means
    /// <see cref="BreakerSettings"/>'s own defaults.
    /// </summary>
    public BreakerSettings? BreakerSettings { get; set; }

    /// <summary>
    /// Whether each host gets its own retry budget. On by default, and for the same reason as
    /// <see cref="BreakerPerHost"/>: a storm against one host must not throttle retries to
    /// another.
    /// <para>
    /// A policy carrying an explicit <see cref="Resilience.Budget"/> keeps it, including
    /// <see cref="RetryBudget.None"/>.
    /// </para>
    /// </summary>
    public bool BudgetPerHost { get; set; } = true;

    /// <summary>
    /// Whether the handler stamps <see cref="ResilienceHttp.NestedRetryHeader"/> on outbound
    /// requests and reports nesting it detects. On by default; it costs one header on a request
    /// that can be retried.
    /// </summary>
    public bool DetectNestedRetries { get; set; } = true;
}
