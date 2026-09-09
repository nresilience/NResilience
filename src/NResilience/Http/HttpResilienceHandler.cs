using System.Net;
using System.Net.Http.Headers;
using NResilience.Internal;

namespace NResilience;

/// <summary>
///     The resilience handler: one <see cref="DelegatingHandler" /> that runs a
///     <see cref="Resilience" /> policy around the send, and does the five HTTP-specific things a
///     policy on its own cannot.
///     <list type="bullet">
///         <item>
///             <description>
///                 It builds a fresh request for every attempt, because an
///                 <see cref="HttpRequestMessage" /> may be sent once.
///             </description>
///         </item>
///         <item>
///             <description>
///                 It does not retry POST or PATCH unless told to, per client or per
///                 request.
///             </description>
///         </item>
///         <item>
///             <description>It scopes the breaker and the budget to the host.</description>
///         </item>
///         <item>
///             <description>It reports nested retries.</description>
///         </item>
///         <item>
///             <description>It disposes the responses a retry supersedes.</description>
///         </item>
///     </list>
///     Taking ownership of the transport timeout is the sixth, and it belongs to whoever builds the
///     <see cref="HttpClient" /> - see <see cref="HttpResilience.CreateClient" /> and
///     <see cref="HttpResilienceOptions.OwnTransportTimeout" />.
/// </summary>
/// <example>
///     <code>
/// using HttpClient client = HttpResilience.CreateClient();
/// using var response = await client.GetAsync(uri, cancellationToken);
/// </code>
/// </example>
public sealed class HttpResilienceHandler : DelegatingHandler
{
    /// <summary>
    ///     Whether this call is already running inside a retrying handler's attempt.
    ///     <para>
    ///         The header carries the same fact across a process boundary; this carries it within one, and
    ///         it is the half that needs no cooperation from anybody. Written only by handlers that can
    ///         actually retry, so a chain of single-attempt clients costs nothing.
    ///     </para>
    /// </summary>
    private static readonly AsyncLocal<bool> InsideRetryingClient = new();

    private readonly HostRegistry _hosts;

    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

    /// <summary>A handler whose inner handler is assigned later, as a client factory does.</summary>
    /// <param name="policy">The policy. Defaults to <see cref="Resilience.Http" />.</param>
    /// <param name="options">The HTTP switches. Defaults to <see cref="HttpResilienceOptions" />'s own defaults.</param>
    /// <exception cref="ResilienceConfigurationException">The policy cannot be executed, or the options cannot be used.</exception>
    public HttpResilienceHandler(Resilience? policy = null, HttpResilienceOptions? options = null)
    {
        Policy = policy ?? Resilience.Http;
        Policy.Validate();
        Options = options ?? new HttpResilienceOptions();
        Options.Validate();
        _hosts = new HostRegistry(Policy, Options);
        _send = SendCoreAsync;
    }

    /// <summary>A handler in front of a transport.</summary>
    /// <param name="innerHandler">The transport.</param>
    /// <param name="policy">The policy. Defaults to <see cref="Resilience.Http" />.</param>
    /// <param name="options">The HTTP switches. Defaults to <see cref="HttpResilienceOptions" />'s own defaults.</param>
    /// <exception cref="ResilienceConfigurationException">The policy cannot be executed, or the options cannot be used.</exception>
    public HttpResilienceHandler(HttpMessageHandler innerHandler, Resilience? policy = null, HttpResilienceOptions? options = null)
        : this(policy, options)
    {
        InnerHandler = innerHandler;
    }

    /// <summary>The policy this handler runs, before its per-host scoping.</summary>
    public Resilience Policy { get; }

    /// <summary>The HTTP switches this handler was built with.</summary>
    public HttpResilienceOptions Options { get; }

    /// <summary>
    ///     The breakers, by host, for the hosts this handler has seen. For a health endpoint: a
    ///     breaker whose scope is a variable with a name is one an operator can be told about.
    /// </summary>
    /// <returns>A snapshot. Empty when <see cref="HttpResilienceOptions.BreakerPerHost" /> is off and the policy carries no breaker.</returns>
    public IReadOnlyDictionary<string, Breaker> BreakersByHost() => ByHost(static scope => scope.Breaker);

    /// <summary>The retry budgets, by host, for the hosts this handler has seen.</summary>
    /// <returns>A snapshot.</returns>
    public IReadOnlyDictionary<string, RetryBudget> BudgetsByHost() => ByHost(static scope => scope.Budget);

    /// <summary>
    ///     Whether a request will be retried, given this handler's policy and options. The decision
    ///     the handler itself makes, exposed so a test - or a log line - can ask about it.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>True when the policy allows more than one attempt and the request may be repeated.</returns>
    /// <remarks>
    ///     GET, HEAD, PUT, DELETE, OPTIONS and TRACE are repeatable; POST and PATCH are not, and
    ///     neither is any method the library has not heard of. <see cref="HttpResilience.Repeatable" />
    ///     on the request overrides all of it, in both directions.
    /// </remarks>
    public bool WillRetry(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ShouldRetry(request);
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scope = _hosts.For(request.RequestUri?.Authority ?? string.Empty);
        var retrying = ShouldRetry(request);
        var policy = retrying ? scope.Retrying : scope.Single;

        var wasInside = false;

        if (retrying && Options.DetectNestedRetries)
        {
            wasInside = InsideRetryingClient.Value;
            var inbound = CarriesRetryMarker(request);

            // The three ways this call can already be inside a retry loop: an outer handler in this process,
            // a marker on the outbound request itself, and the inbound request that started this one - the
            // half that needs a server to read it, and the one that makes the middle hop of a chain able to
            // see the amplification it is part of. The ambient read is last so a call already known to be
            // nested does not pay for it.
            var nested = wasInside || inbound || NestedRetry.IsCallerRetrying;

            if (nested && policy.OnEvent is { } listener)
                listener(new CallEvent(CallEventKind.NestedRetry, policy.Name, 1, Verdict.Ok, TimeSpan.Zero, null, null, null, null));

            if (!inbound)
                request.Headers.TryAddWithoutValidation(NestedRetry.Header, NestedRetry.Marker);

            InsideRetryingClient.Value = true;
        }

        // A hedged policy runs the callback concurrently and disposes every response it discards, so the
        // call must not also dispose "the previous one" - there is no such thing when attempts overlap.
        var call = new HttpCall(
            request, _send, retrying, policy.Hedge is not null, StampFor(policy), Options.BufferResponses, scope.Quota, CriticalityFor());

        if (retrying)
            await call.BufferAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var response = await policy.RunAsync(
                static (c, ct) => c.SendAsync(ct),
                call,
                cancellationToken).ConfigureAwait(false);

            // A buffered body is already read, so there is nothing left to stall on and nothing to
            // wrap - the attempt covered it, which is the whole point of the option.
            return Options.BufferResponses ? response : Bound(response, policy, request);
        }
        catch
        {
            // Nobody is going to receive the last response, so nobody is going to dispose it.
            call.DisposeLast();
            throw;
        }
        finally
        {
            if (retrying && Options.DetectNestedRetries)
            {
                // Restore the previous value rather than clearing unconditionally. AsyncLocal value
                // changes in a child context do not flow back to the parent, so this is a defensive
                // measure: the handler's own context is left exactly as it found it, which is the
                // correct invariant regardless of how the runtime flows the value.
                InsideRetryingClient.Value = wasInside;
            }
        }
    }

    /// <summary>Not supported. The library is async-only, by design and throughout.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>Never returns.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "NResilience is async-only: a retry loop that blocks holds a thread through every backoff delay. Use SendAsync.");

    /// <summary>
    ///     Substitutes a body whose reads are bounded, when the policy asks for it and there is a body
    ///     to bound.
    /// </summary>
    /// <remarks>
    ///     This is the one place the bound can be applied. The transport returns at the response
    ///     headers, so the body is not part of the attempt and the executor has already judged, logged
    ///     and returned by the time there is a body to wrap - which is also why nothing here is live
    ///     across the attempt <c>await</c> and the state-machine box of every call is unmoved.
    ///     <para>
    ///         Skipped for a declared empty body, which is what a 204 and a HEAD response carry: the
    ///         transport supplies an empty content rather than a null one, and wrapping it would pay a
    ///         timer and a stream for a body that cannot stall.
    ///     </para>
    /// </remarks>
    /// <param name="response">The response the executor returned.</param>
    /// <param name="policy">The host-scoped policy that produced it.</param>
    /// <param name="request">The final request that produced <paramref name="response" />, for a resume's <c>Range</c>/<c>If-Range</c>.</param>
    /// <returns>The same response, with its content wrapped where that applies.</returns>
    private HttpResponseMessage Bound(HttpResponseMessage response, Resilience policy, HttpRequestMessage request)
    {
        // An infinite attempt timeout is the absence of the bound this feature applies, so there is
        // nothing to apply. Said here rather than in ProgressStream so the timer is never created.
        if (!policy.BoundProgress || policy.AttemptTimeout == Timeout.InfiniteTimeSpan)
            return response;

        if (response.Content is not { } body || body.Headers.ContentLength == 0 || body is ProgressContent)
            return response;

        response.Content = new ProgressContent(
            body, policy.AttemptTimeout, policy.Time, policy.Name, policy.OnEvent, ResumeFor(response, request));

        return response;
    }

    /// <summary>
    ///     Builds the callback a stalled read resumes through, or null when
    ///     <see cref="HttpResilienceOptions.ResumeDownloads" /> does not apply to this response.
    /// </summary>
    /// <remarks>
    ///     Read here, once, from the response the caller is about to start reading - not per stall -
    ///     because the precondition is a fact about <i>this</i> representation and does not change
    ///     while the caller reads it.
    /// </remarks>
    /// <param name="response">The response whose body may need resuming.</param>
    /// <param name="request">The request that produced it.</param>
    /// <returns>The callback, or null.</returns>
    private Func<long, CancellationToken, Task<Stream?>>? ResumeFor(HttpResponseMessage response, HttpRequestMessage request)
    {
        if (!Options.ResumeDownloads || request.Method != HttpMethod.Get)
            return null;

        if (response.Headers.ETag is not { IsWeak: false } etag || !AcceptsByteRanges(response))
            return null;

        return async (offset, cancellationToken) =>
        {
            using var next = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };

            foreach (var header in request.Headers)
            {
                next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            next.Headers.Range = new RangeHeaderValue(offset, null);
            next.Headers.IfRange = new RangeConditionHeaderValue(etag);

            HttpResponseMessage resumed;

            try
            {
                resumed = await _send(next, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The resume attempt itself failed to reach the dependency. Not the caller's stall to
                // hear about twice - ProgressStream reports the original one.
                return null;
            }

            // Anything but 206 means the server did not resume: a 200 says the representation moved
            // on despite the ETag matching for If-Range's own purposes, and anything else is a
            // response this method has no reading for. Spliced content is worse than none, so this
            // is refused rather than guessed at.
            if (resumed.StatusCode != HttpStatusCode.PartialContent || resumed.Content is not { } continuation)
            {
                resumed.Dispose();
                return null;
            }

            var stream = await continuation.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            // The continuation's HttpResponseMessage owns the connection the stream reads from, and
            // nothing else references it once this method returns - so it is disposed with the stream
            // rather than leaked until finalization.
            return new ResponseOwnedStream(resumed, stream);
        };
    }

    /// <summary>Whether a response declared it can serve byte ranges of itself.</summary>
    private static bool AcceptsByteRanges(HttpResponseMessage response)
    {
        foreach (var range in response.Headers.AcceptRanges)
        {
            if (string.Equals(range, "bytes", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     One host-scoped guard per host that has one, as a snapshot. Both public views are the same
    ///     walk over the same scopes and differ only in which guard they read.
    /// </summary>
    private Dictionary<string, T> ByHost<T>(Func<HostScope, T?> guard)
        where T : class
    {
        var found = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

        foreach (var scope in _hosts.Scopes)
        {
            if (guard(scope) is { } value)
                found[scope.Host] = value;
        }

        return found;
    }

    /// <summary>
    ///     Tells the peer how long this side is prepared to wait, or null when
    ///     <see cref="HttpResilienceOptions.PropagateDeadline" /> is off.
    /// </summary>
    /// <remarks>
    ///     The clock is read here because the executor cannot return a timestamp. Reading it before
    ///     buffering the body makes the figure slightly conservative, ensuring the peer is told it
    ///     has no more time than it actually does.
    /// </remarks>
    private DeadlineStamp? StampFor(Resilience policy)
    {
        if (!Options.PropagateDeadline)
            return null;

        // The same clamp the executor is about to apply. Read here as well because the handler has to
        // put a number on the wire before the executor has started, and the inbound deadline is what
        // makes that number honest.
        var deadline = policy.UseAmbientDeadline ? AmbientDeadline.Clamp(policy.Deadline) : policy.Deadline;

        if (deadline == Timeout.InfiniteTimeSpan && policy.AttemptTimeout == Timeout.InfiniteTimeSpan)
            return null;

        return new DeadlineStamp(Options.DeadlineHeader, deadline, policy.AttemptTimeout, policy.Time.GetTimestamp(), policy.Time);
    }

    /// <summary>
    ///     Tells the peer how much this work matters, or null when
    ///     <see cref="HttpResilienceOptions.PropagateCriticality" /> is off.
    /// </summary>
    /// <remarks>
    ///     Read once per call rather than per attempt, because the level cannot change while the call
    ///     runs - and unlike the deadline there is no arithmetic to redo. Independent of the policy's
    ///     <see cref="Resilience.UseAmbientCriticality" />: sending a level and acting on one are two
    ///     halves that are each useful without the other, exactly as they are for the deadline.
    /// </remarks>
    private CriticalityStamp? CriticalityFor() =>
        Options.PropagateCriticality
            ? new CriticalityStamp(Options.CriticalityHeader, AmbientCriticality.Format(AmbientCriticality.Current))
            : null;

    private bool ShouldRetry(HttpRequestMessage request) => Policy.Attempts > 1 && IsRepeatable(request);

    private bool IsRepeatable(HttpRequestMessage request)
    {
        // An explicit declaration beats everything, in both directions: whoever wrote the request
        // knows whether it carries an idempotency key, and the client registration does not.
        if (request.Options.TryGetValue(HttpResilience.Repeatable, out var declared))
            return declared;

        // An unrecognized method is treated as unsafe. Retrying something the library has never
        // heard of is a guess, and the direction to guess in is the one that does not duplicate it.
        return IsIdempotentMethod(request.Method) || Options.RetryUnsafeMethods;
    }

    private static bool IsIdempotentMethod(HttpMethod method) =>
        method == HttpMethod.Get
        || method == HttpMethod.Head
        || method == HttpMethod.Put
        || method == HttpMethod.Delete
        || method == HttpMethod.Options
        || method == HttpMethod.Trace;

    /// <summary>
    ///     Whether the request already carries the retry marker. Presence is not enough: an
    ///     intermediary that forwards unknown headers can add an empty value, and only
    ///     <see cref="NestedRetry.Marker" /> is a value this library wrote. A loop rather
    ///     than LINQ - this runs on every retrying send.
    /// </summary>
    private static bool CarriesRetryMarker(HttpRequestMessage request)
    {
        if (!request.Headers.TryGetValues(NestedRetry.Header, out var values))
            return false;

        foreach (var value in values)
        {
            if (NestedRetry.IsMarker(value))
                return true;
        }

        return false;
    }

    private Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        base.SendAsync(request, cancellationToken);
}
