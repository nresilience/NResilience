using Grpc.Core;
using Grpc.Core.Interceptors;
using NResilience.Grpc.Internal;

namespace NResilience.Grpc;

/// <summary>
///     The resilience interceptor: one <see cref="Interceptor" /> that runs a
///     <see cref="Resilience" /> policy around each gRPC call, and does the gRPC-specific things a
///     policy on its own cannot.
///     <list type="bullet">
///         <item>
///             <description>It classifies a <see cref="StatusCode" />, which is where a gRPC failure actually lives.</description>
///         </item>
///         <item>
///             <description>It repeats unary calls by default, and lets you say which ones it must not.</description>
///         </item>
///         <item>
///             <description>It writes each attempt's ceiling into <see cref="CallOptions.Deadline" />, so the peer learns it as <c>grpc-timeout</c>.</description>
///         </item>
///         <item>
///             <description>It scopes the breaker and the budget per service.</description>
///         </item>
///         <item>
///             <description>It reports nested retries, under the same marker the HTTP handler uses.</description>
///         </item>
///         <item>
///             <description>It disposes the calls a retry supersedes.</description>
///         </item>
///     </list>
/// </summary>
/// <remarks>
///     Hold one per channel. An interceptor created per call hands every call a fresh breaker and a
///     retry budget that never accumulates, which is the failure <c>NRES005</c> exists to catch, one
///     level up; <c>AddGrpcResilience()</c> registers it at
///     <c>InterceptorScope.Channel</c> for that reason.
///     <para>
///         Server-streaming calls are retried until their first message and never after it; see
///         <see cref="AsyncServerStreamingCall{TRequest,TResponse}" />.
///     </para>
///     <para>
///         Client-streaming and duplex calls pass through untouched. The request stream is a source
///         the caller drives interactively, and a retry would have to re-enumerate something the
///         failed attempt has already partially consumed - which is the duplicates-or-buffering
///         problem, not a resilience feature.
///     </para>
/// </remarks>
/// <example>
///     <code>
/// services.AddGrpcClient&lt;Orders.OrdersClient&gt;(o =&gt; o.Address = new Uri("https://orders.internal:5001"))
///     .AddGrpcResilience();
/// </code>
/// </example>
public sealed class ResilienceInterceptor : Interceptor
{
    /// <summary>
    ///     Whether this call is already running inside a retrying client's attempt.
    ///     <para>
    ///         The metadata marker carries the same fact across a process boundary; this carries it
    ///         within one, and it is the half that needs no cooperation from anybody. Static, and
    ///         shared with nothing: two interceptors nested in one process are exactly the case it
    ///         exists to see.
    ///     </para>
    /// </summary>
    private static readonly AsyncLocal<bool> InsideRetryingClient = new();

    private readonly MethodScopes _scopes;

    /// <summary>Creates the interceptor.</summary>
    /// <param name="policy">The policy. Defaults to <see cref="GrpcResilience.Default" />.</param>
    /// <param name="options">The gRPC switches. Defaults to <see cref="GrpcResilienceOptions" />'s own defaults.</param>
    /// <param name="name">
    ///     What the single scope is reported under when <see cref="GrpcResilienceOptions.ScopeBy" />
    ///     is null and the policy carries no name. Usually the client's name.
    /// </param>
    /// <exception cref="ResilienceConfigurationException">The policy or the options cannot be used.</exception>
    public ResilienceInterceptor(Resilience? policy = null, GrpcResilienceOptions? options = null, string? name = null)
    {
        Policy = policy ?? GrpcResilience.Default;
        Policy.Validate();

        Options = options ?? new GrpcResilienceOptions();
        Options.Validate();

        _scopes = new MethodScopes(Policy, Options, name ?? Policy.Name ?? "grpc");
    }

    /// <summary>The policy this interceptor runs, before its per-scope derivation.</summary>
    public Resilience Policy { get; }

    /// <summary>The gRPC switches this interceptor was built with.</summary>
    public GrpcResilienceOptions Options { get; }

    /// <summary>
    ///     The breakers, by scope key, for the scopes this interceptor has seen. For a health
    ///     endpoint: a breaker whose scope is a key with a name is one an operator can be told about.
    /// </summary>
    /// <returns>
    ///     A snapshot. Empty when <see cref="GrpcResilienceOptions.BreakerPerScope" /> is off and the
    ///     policy carries no breaker.
    /// </returns>
    public IReadOnlyDictionary<string, Breaker> Breakers() => _scopes.Breakers();

    /// <summary>The retry budgets, by scope key, for the scopes this interceptor has seen.</summary>
    /// <returns>A snapshot.</returns>
    public IReadOnlyDictionary<string, RetryBudget> Budgets() => _scopes.Budgets();

    /// <summary>
    ///     Whether a method will be retried, given this interceptor's policy and options. The decision
    ///     the interceptor itself makes, exposed so a test - or a log line - can ask about it.
    /// </summary>
    /// <param name="method">The method.</param>
    /// <returns>True when the policy allows more than one attempt and the method may be repeated.</returns>
    /// <remarks>
    ///     Reads <see cref="GrpcResilience.SingleShot" /> as well, so the answer is the one that will
    ///     be used for a call made right here.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="method" /> is null.</exception>
    public bool WillRetry(IMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return ShouldRetry(method);
    }

    /// <inheritdoc />
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);

        var retrying = ShouldRetry(context.Method);
        var scoped = _scopes.Retrying(context.Method);
        var policy = retrying ? scoped : _scopes.Single(scoped);

        var call = new UnaryCall<TRequest, TResponse>(request, context, continuation, policy, Options, retrying);

        if (retrying && Options.DetectNestedRetries && policy.OnEvent is { } listener)
        {
            // The three ways this call can already be inside a retry loop: an outer client in this
            // process, a marker on the outbound call itself, and the inbound request that started
            // this one - the half that needs a server to read it, and the one that makes the middle
            // hop of a chain able to see the amplification it is part of. The library reports it and
            // does nothing else; silently dropping the caller's configured retries would be a bigger
            // surprise than the amplification.
            if (InsideRetryingClient.Value || call.CarriesRetryMarker || ResilienceNestedRetry.IsCallerRetrying)
                listener(new CallEvent(CallEventKind.NestedRetry, policy.Name, 1, Verdict.Ok, TimeSpan.Zero, null, null, null, null));
        }

        return call.ToCall(call.RunAsync(InsideRetryingClient));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Retried until the first message and never after it, which is the only honest semantic a
    ///     stream has: a retry once the consumer holds a message would duplicate work they have
    ///     already acted on. The wire deadline is the whole call's remaining budget rather than the
    ///     attempt ceiling, because a deadline is fixed when the call starts and
    ///     <see cref="Resilience.AttemptTimeout" /> bounds only the time to that first message.
    /// </remarks>
    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);

        var retrying = ShouldRetry(context.Method);
        var scoped = _scopes.Retrying(context.Method);
        var policy = retrying ? scoped : _scopes.Single(scoped);

        var call = new ServerStreamingCall<TRequest, TResponse>(request, context, continuation, policy, Options, retrying);

        if (retrying && Options.DetectNestedRetries && policy.OnEvent is { } listener)
        {
            // The same three ways this call can already be inside a retry loop as for a unary call.
            // What is not done here is publishing the ambient flag for the duration: a stream's
            // duration is the consumer's enumeration, on whatever context they enumerate from, and
            // an AsyncLocal cannot describe that. The metadata marker still travels on every attempt.
            if (InsideRetryingClient.Value || call.CarriesRetryMarker || ResilienceNestedRetry.IsCallerRetrying)
                listener(new CallEvent(CallEventKind.NestedRetry, policy.Name, 1, Verdict.Ok, TimeSpan.Zero, null, null, null, null));
        }

        return call.ToCall();
    }

    /// <summary>Not supported. The library is async-only, by design and throughout.</summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="request">The request.</param>
    /// <param name="context">The call context.</param>
    /// <param name="continuation">The next handler in the chain.</param>
    /// <returns>Never returns.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    /// <remarks>
    ///     Refused rather than passed through. A blocking call that silently ran without the policy
    ///     would be the one call in the client that has no retry, no breaker and no deadline, and
    ///     nothing on the surface would say so - which is a worse outcome than a compile-time-visible
    ///     failure at the one call site that uses it. The same decision
    ///     <see cref="Http.ResilienceHandler" /> makes about synchronous sends.
    /// </remarks>
    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        BlockingUnaryCallContinuation<TRequest, TResponse> continuation) =>
        throw new NotSupportedException(
            "NResilience is async-only: a retry loop that blocks holds a thread through every backoff delay. Use the generated client's Async overload.");

    private bool ShouldRetry(IMethod method) =>
        Policy.Attempts > 1 && !GrpcResilience.IsSingleShot && Options.IsRepeatable(method);
}
