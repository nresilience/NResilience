using Grpc.Core;

namespace NResilience.Grpc;

/// <summary>
///     The things the gRPC integration decides that a <see cref="Resilience" /> policy cannot,
///     because they are properties of gRPC rather than of resilience.
/// </summary>
/// <remarks>
///     A mutable options class rather than a record, because this is the type an options callback
///     configures - <c>o =&gt; o.ScopeBy = null</c> - and that is the shape
///     <c>Microsoft.Extensions.Options</c> binds to. It is the gRPC counterpart of
///     <see cref="HttpResilienceOptions" />, and the two share property names wherever they
///     mean the same thing.
/// </remarks>
public sealed class GrpcResilienceOptions
{
    /// <summary>
    ///     Whether the interceptor writes each attempt's ceiling into <see cref="CallOptions.Deadline" />.
    ///     On by default.
    ///     <para>
    ///         This is deadline propagation for gRPC, and it costs nothing: grpc-dotnet converts a
    ///         <see cref="CallOptions.Deadline" /> into the standard <c>grpc-timeout</c> header, so the
    ///         peer learns the ceiling without a new header, a new format, or anything to parse. The
    ///         value written is <c>min(AttemptTimeout, time left on the deadline)</c> plus
    ///         <see cref="DeadlineSlack" />.
    ///     </para>
    ///     <para>
    ///         A deadline the caller set on the call is never overwritten: the effective one is
    ///         whichever of the two is tighter.
    ///     </para>
    ///     <para>
    ///         The same switch as <see cref="HttpResilienceOptions.PropagateDeadline" />, and the one
    ///         asymmetry between them is deliberate: this one is on by default and the HTTP one is
    ///         off. <c>grpc-timeout</c> is a protocol field every gRPC peer already honors, so sending
    ///         it costs nothing and is never misread; the HTTP header is a convention this library
    ///         invented, and a header the other side does not read is not worth sending by default.
    ///     </para>
    /// </summary>
    public bool PropagateDeadline { get; set; } = true;

    /// <summary>
    ///     How much longer than the attempt ceiling the wire deadline is set. 50 ms by default, and
    ///     deliberately not zero.
    ///     <para>
    ///         Unlike the HTTP integration's deadline header, which is advisory and read only by the
    ///         peer, <see cref="CallOptions.Deadline" /> is enforced locally by grpc-dotnet's own
    ///         timer. Writing the attempt ceiling into it arms two timers for the same instant - ours
    ///         and gRPC's - and whichever the runtime notices first decides what the call looks like.
    ///         The slack makes the executor's timer win in the ordinary case, so a timed-out attempt
    ///         keeps producing <see cref="AttemptTimeoutException" />, keeps charging the deadline,
    ///         and keeps raising <see cref="CallEventKind.OrphanedWork" />.
    ///     </para>
    ///     <para>
    ///         The case the slack cannot cover - clock granularity, a scheduling stall - is covered by
    ///         the interceptor instead: a <see cref="StatusCode.DeadlineExceeded" /> on a deadline the
    ///         interceptor wrote is translated into the cancellation shape the executor already knows
    ///         how to judge, so the outcome is identical either way.
    ///     </para>
    /// </summary>
    public TimeSpan DeadlineSlack { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    ///     Whether the registration sets <c>HttpClient.Timeout</c> to
    ///     <see cref="Timeout.InfiniteTimeSpan" /> for the channel. On by default, and the same
    ///     decision <see cref="HttpResilienceOptions.OwnTransportTimeout" /> makes for HTTP.
    ///     <para>
    ///         The transport timeout covers the whole retry sequence rather than one attempt, so it
    ///         silently caps any policy whose <see cref="Resilience.Deadline" /> exceeds it. The bound
    ///         belongs on the policy, where it is visible and expressed in the same vocabulary as the
    ///         attempt timeout.
    ///     </para>
    ///     <para>
    ///         Honored by the registration, since an interceptor cannot reach the channel in front of
    ///         it. Setting it on an interceptor you construct yourself does nothing.
    ///     </para>
    /// </summary>
    public bool OwnTransportTimeout { get; set; } = true;

    /// <summary>
    ///     Whether the interceptor stamps and reads the nested-retry marker. On by default, as for
    ///     HTTP.
    ///     <para>
    ///         Three layers each retrying three times is twenty-seven attempts at the bottom, and the
    ///         middle hop is the only place that is invisible. The marker travels under the same
    ///         <c>X-NResilience-Retrying</c> name the HTTP handler uses, so the fact crosses the two
    ///         transports.
    ///     </para>
    /// </summary>
    public bool DetectNestedRetries { get; set; } = true;

    /// <summary>
    ///     Whether a method may be repeated. Every method, by default - which is the opposite of the
    ///     HTTP default, on purpose.
    ///     <para>
    ///         The HTTP integration refuses to repeat POST because a repeated POST is a duplicate
    ///         charge. Every gRPC call is a POST at the transport and most of them are reads at the
    ///         application, so carrying that rule across would make the interceptor inert - which is
    ///         a different way of shipping nothing. Mark the methods whose side effects do not
    ///         tolerate a duplicate instead.
    ///     </para>
    ///     <para>
    ///         The parameter is <see cref="IMethod" /> rather than a concrete method type, which also
    ///         puts <see cref="IMethod.Type" /> in reach: <c>m =&gt; m.Type == MethodType.Unary</c> is
    ///         the whole of a "retry reads, not writes" rule.
    ///     </para>
    ///     <para>
    ///         A predicate rather than the <see cref="bool" /> that
    ///         <see cref="HttpResilienceOptions.RetryUnsafeMethods" /> is, because the shapes of the
    ///         two protocols differ: HTTP has a method table with an established safety rule to
    ///         switch on, and gRPC has no equivalent - which method is safe to repeat is a fact about
    ///         your service, so the only honest knob is one that asks.
    ///     </para>
    /// </summary>
    /// <example>
    ///     <code>
    /// o.RepeatableWhen = static m =&gt; m.FullName != "/orders.Orders/ChargeCard";
    /// </code>
    /// </example>
    public Func<IMethod, bool> RepeatableWhen { get; set; } = static _ => true;

    /// <summary>
    ///     The breaker, budget and latency-window scope key for a method. Per service by default;
    ///     null for one scope per registered client.
    ///     <para>
    ///         A gRPC channel serves one host, so there is no per-host scoping to do - but the gRPC
    ///         analog of "host" is not the channel, it is the service. One expensive RPC failing
    ///         should not open the circuit on every other method the client exposes, which is the
    ///         same blast-radius argument that makes the HTTP integration scope per host.
    ///     </para>
    ///     <para>
    ///         <c>static m =&gt; m.FullName</c> is per method, for a service whose methods have
    ///         genuinely independent failure modes. <c>null</c> is one scope for the whole client,
    ///         which is right for a client that fronts one coherent service and wants its breaker to
    ///         see every call.
    ///     </para>
    /// </summary>
    public Func<IMethod, string>? ScopeBy { get; set; } = static m => m.ServiceName;

    /// <summary>
    ///     Whether each <see cref="ScopeBy" /> scope gets its own circuit breaker. On by default, and
    ///     the same decision <see cref="HttpResilienceOptions.BreakerPerHost" /> makes per host.
    ///     <para>
    ///         A policy that already carries a <see cref="Resilience.Breaker" /> keeps it: an explicit
    ///         breaker is a deliberate scope decision and this switch does not overrule it.
    ///     </para>
    /// </summary>
    public bool BreakerPerScope { get; set; } = true;

    /// <summary>
    ///     The settings the per-scope breakers are built with. Defaults to
    ///     <see cref="BreakerSettings" />'s own defaults, on the policy's clock.
    /// </summary>
    public BreakerSettings? BreakerSettings { get; set; }

    /// <summary>
    ///     Whether each <see cref="ScopeBy" /> scope gets its own retry budget. On by default, and the
    ///     same decision <see cref="HttpResilienceOptions.BudgetPerHost" /> makes per host: a storm
    ///     against one service must not throttle retries to another.
    ///     <para>
    ///         A policy carrying an explicit <see cref="Resilience.Budget" /> keeps it, including
    ///         <see cref="RetryBudget.None" />. Turning this off leaves every scope sharing the
    ///         policy's one budget, which is the right reading for a client whose methods all front
    ///         one dependency.
    ///     </para>
    /// </summary>
    public bool BudgetPerScope { get; set; } = true;

    /// <summary>
    ///     How many <see cref="ScopeBy" /> keys to keep. The least-recently-seen are dropped past
    ///     this. Ignored when <see cref="ScopeBy" /> is null, since there is then one scope.
    /// </summary>
    /// <remarks>
    ///     There is no unbounded mode, for the same reason <see cref="PolicyScope{TKey}" /> has none:
    ///     unbounded keying is a memory leak with a breaker and a budget on every entry. The default
    ///     is far above the method count of any real service.
    /// </remarks>
    public int MaximumScopes { get; set; } = 1024;

    /// <summary>
    ///     Runs <see cref="Validate" /> and returns these options, so a bad configuration throws where
    ///     it is written rather than when the interceptor is built.
    /// </summary>
    /// <returns>These options.</returns>
    /// <exception cref="ResilienceConfigurationException">The options cannot be used.</exception>
    public GrpcResilienceOptions Validated()
    {
        Validate();
        return this;
    }

    /// <summary>Checks the options and throws <see cref="ResilienceConfigurationException" /> listing every problem at once.</summary>
    /// <exception cref="ResilienceConfigurationException">The options cannot be used.</exception>
    public void Validate()
    {
        var problems = new List<string>();

        if (RepeatableWhen is null)
            problems.Add("RepeatableWhen must not be null; use `static _ => true` for the default.");

        if (DeadlineSlack < TimeSpan.Zero)
            problems.Add($"DeadlineSlack must not be negative; it is {DeadlineSlack}. Zero arms two timers for the same instant - see the property's remarks.");

        if (MaximumScopes < 1)
            problems.Add($"MaximumScopes must be at least 1; it is {MaximumScopes}.");

        if (problems.Count > 0)
            throw new ResilienceConfigurationException(problems);
    }
}
