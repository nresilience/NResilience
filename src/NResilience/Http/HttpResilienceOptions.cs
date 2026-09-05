namespace NResilience;

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
    ///         <see cref="HttpResilience.Repeatable" /> is the finer instrument and it wins.
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
    ///         It is honored by whoever builds the client - <see cref="HttpResilience.CreateClient" />, or
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
    ///     The number of hosts the handler keeps a breaker and a budget for. 1024 by default, and at
    ///     least 1.
    ///     <para>
    ///         The set of hosts one client talks to is normally a property of the application, and the
    ///         cap is invisible to it. A proxy, a crawler or a webhook dispatcher reaches the cap, and
    ///         the least-recently-seen hosts are dropped - a host that returns after being dropped
    ///         starts again with a closed breaker and a full budget, which is the right reading for a
    ///         host nobody has spoken to in a while.
    ///     </para>
    ///     <para>
    ///         Eviction is approximate, so the registry can sit a little over the cap while a sweep
    ///         catches up. There is no unbounded mode, for the reason <see cref="PolicyScope{TKey}" />
    ///         has none - unbounded keying is a memory leak with a breaker and a budget on every
    ///         entry - and <see cref="int.MaxValue" /> is how you say "effectively unbounded" if you
    ///         want it anyway.
    ///     </para>
    /// </summary>
    public int MaximumHosts { get; set; } = 1024;

    /// <summary>
    ///     Whether each outbound attempt carries how long this side is going to wait for it. Off by
    ///     default, which is the one place this differs from the gRPC integration's switch of the same
    ///     name: <c>grpc-timeout</c> is a protocol field every gRPC peer already honors, and this
    ///     header is a convention the library invented.
    ///     <para>
    ///         The value is the attempt's own ceiling -
    ///         <c>
    ///             min(<see cref="Resilience.AttemptTimeout" />,
    ///             time left on the deadline)
    ///         </c>
    ///         - in whole milliseconds, written to
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
    ///     <see cref="AmbientDeadline.Header" />, which is what the inbound half reads.
    /// </summary>
    /// <remarks>
    ///     There is no standard for this on plain HTTP, so the name is yours to change - one service
    ///     mesh's convention is another's. <c>grpc-timeout</c> is not a drop-in value for it: gRPC's
    ///     format carries a unit suffix rather than a bare count of milliseconds, and the gRPC client
    ///     stack already propagates its own deadlines from <c>CallOptions.Deadline</c>.
    /// </remarks>
    public string DeadlineHeader { get; set; } = AmbientDeadline.Header;

    /// <summary>
    ///     Whether the handler stamps <see cref="NestedRetry.Header" /> on outbound
    ///     requests and reports nesting it detects. On by default; it costs one header on a request
    ///     that can be retried.
    /// </summary>
    public bool DetectNestedRetries { get; set; } = true;

    /// <summary>
    ///     Runs <see cref="Validate" /> and returns these options, so a bad configuration throws where
    ///     it is written rather than when the handler is built.
    /// </summary>
    /// <returns>These options.</returns>
    /// <exception cref="ResilienceConfigurationException">The options cannot be used.</exception>
    public HttpResilienceOptions Validated()
    {
        Validate();
        return this;
    }

    /// <summary>
    ///     Checks the options and throws <see cref="ResilienceConfigurationException" /> listing every
    ///     problem at once. Called for you by <see cref="HttpResilienceHandler" />'s constructor, beside the
    ///     policy's own <see cref="Resilience.Validate" />.
    /// </summary>
    /// <exception cref="ResilienceConfigurationException">The options cannot be used.</exception>
    public void Validate()
    {
        var problems = new List<string>();

        if (MaximumHosts < 1)
            problems.Add($"MaximumHosts must be at least 1; it is {MaximumHosts}. Use int.MaxValue for an effectively unbounded registry.");

        if (string.IsNullOrWhiteSpace(DeadlineHeader))
        {
            problems.Add(
                "DeadlineHeader must not be empty; it is the name of a header. " +
                $"Leave it alone for the default of \"{AmbientDeadline.Header}\", or set PropagateDeadline to false to send none.");
        }

        // Eagerly, rather than on the first request to the first host: the per-host breakers are
        // built lazily as hosts are seen, so a bad setting here would otherwise surface as a
        // configuration error thrown from the middle of a call.
        BreakerSettings?.Validate();

        if (problems.Count > 0)
            throw new ResilienceConfigurationException(problems);
    }
}
