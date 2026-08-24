using System.Net.Http;
using NResilience.Http.Internal;

namespace NResilience.Http;

/// <summary>
/// The resilience handler: one <see cref="DelegatingHandler"/> that runs a
/// <see cref="Resilience"/> policy around the send, and does the five HTTP-specific things a
/// policy on its own cannot.
/// <list type="bullet">
/// <item><description>It builds a fresh request for every attempt, because an
/// <see cref="HttpRequestMessage"/> may be sent once.</description></item>
/// <item><description>It does not retry POST or PATCH unless told to, per client or per
/// request.</description></item>
/// <item><description>It scopes the breaker and the budget to the host.</description></item>
/// <item><description>It reports nested retries.</description></item>
/// <item><description>It disposes the responses a retry supersedes.</description></item>
/// </list>
/// Taking ownership of the transport timeout is the sixth, and it belongs to whoever builds the
/// <see cref="HttpClient"/> - see <see cref="ResilienceHttp.CreateClient"/> and
/// <see cref="HttpResilienceOptions.OwnTransportTimeout"/>.
/// </summary>
/// <example>
/// <code>
/// using HttpClient client = ResilienceHttp.CreateClient();
/// using var response = await client.GetAsync(uri, cancellationToken);
/// </code>
/// </example>
public sealed class ResilienceHandler : DelegatingHandler
{
    /// <summary>
    /// Whether this call is already running inside a retrying handler's attempt.
    /// <para>
    /// The header carries the same fact across a process boundary; this carries it within one, and
    /// it is the half that needs no cooperation from anybody. Written only by handlers that can
    /// actually retry, so a chain of single-attempt clients costs nothing.
    /// </para>
    /// </summary>
    private static readonly AsyncLocal<bool> InsideRetryingClient = new();

    private readonly Resilience _policy;
    private readonly HttpResilienceOptions _options;
    private readonly HostRegistry _hosts;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

    /// <summary>A handler whose inner handler is assigned later, as a client factory does.</summary>
    /// <param name="policy">The policy. Defaults to <see cref="Resilience.Http"/>.</param>
    /// <param name="options">The HTTP switches. Defaults to <see cref="HttpResilienceOptions"/>'s own defaults.</param>
    /// <exception cref="ResilienceConfigurationException">The policy cannot be executed.</exception>
    public ResilienceHandler(Resilience? policy = null, HttpResilienceOptions? options = null)
    {
        _policy = policy ?? Resilience.Http;
        _policy.Validate();
        _options = options ?? new HttpResilienceOptions();
        _hosts = new HostRegistry(_policy, _options);
        _send = SendCoreAsync;
    }

    /// <summary>A handler in front of a transport.</summary>
    /// <param name="innerHandler">The transport.</param>
    /// <param name="policy">The policy. Defaults to <see cref="Resilience.Http"/>.</param>
    /// <param name="options">The HTTP switches. Defaults to <see cref="HttpResilienceOptions"/>'s own defaults.</param>
    /// <exception cref="ResilienceConfigurationException">The policy cannot be executed.</exception>
    public ResilienceHandler(HttpMessageHandler innerHandler, Resilience? policy = null, HttpResilienceOptions? options = null)
        : this(policy, options) => InnerHandler = innerHandler;

    /// <summary>The policy this handler runs, before its per-host scoping.</summary>
    public Resilience Policy => _policy;

    /// <summary>The HTTP switches this handler was built with.</summary>
    public HttpResilienceOptions Options => _options;

    /// <summary>
    /// The breakers, by host, for the hosts this handler has seen. For a health endpoint: a
    /// breaker whose scope is a variable with a name is one an operator can be told about.
    /// </summary>
    /// <returns>A snapshot. Empty when <see cref="HttpResilienceOptions.BreakerPerHost"/> is off and the policy carries no breaker.</returns>
    public IReadOnlyDictionary<string, Breaker> BreakersByHost() => ByHost(static scope => scope.Breaker);

    /// <summary>The retry budgets, by host, for the hosts this handler has seen.</summary>
    /// <returns>A snapshot.</returns>
    public IReadOnlyDictionary<string, RetryBudget> BudgetsByHost() => ByHost(static scope => scope.Budget);

    /// <summary>
    /// Whether a request will be retried, given this handler's policy and options. The decision
    /// the handler itself makes, exposed so a test - or a log line - can ask about it.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>True when the policy allows more than one attempt and the request may be repeated.</returns>
    /// <remarks>
    /// GET, HEAD, PUT, DELETE, OPTIONS and TRACE are repeatable; POST and PATCH are not, and
    /// neither is any method the library has not heard of. <see cref="ResilienceHttp.Repeatable"/>
    /// on the request overrides all of it, in both directions.
    /// </remarks>
    public bool WillRetry(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ShouldRetry(request);
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        HostScope scope = _hosts.For(request.RequestUri?.Authority ?? string.Empty);
        bool retrying = ShouldRetry(request);
        Resilience policy = retrying ? scope.Retrying : scope.Single;

        bool nested = false;
        bool wasInside = false;
        if (retrying && _options.DetectNestedRetries)
        {
            wasInside = InsideRetryingClient.Value;
            bool inbound = request.Headers.Contains(ResilienceHttp.NestedRetryHeader);
            nested = wasInside || inbound;
            if (nested && policy.OnEvent is { } listener)
            {
                listener(new CallEvent(CallEventKind.NestedRetry, policy.Name, 1, Verdict.Ok, TimeSpan.Zero, null, null, null, null));
            }

            if (!inbound)
            {
                request.Headers.TryAddWithoutValidation(ResilienceHttp.NestedRetryHeader, "1");
            }

            InsideRetryingClient.Value = true;
        }

        var call = new HttpCall(request, _send, clone: retrying);
        if (retrying)
        {
            await call.BufferAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await policy.RunAsync(
                static (HttpCall c, CancellationToken ct) => c.SendAsync(ct),
                call,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Nobody is going to receive the last response, so nobody is going to dispose it.
            call.DisposeLast();
            throw;
        }
        finally
        {
            if (retrying && _options.DetectNestedRetries)
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
    /// One host-scoped guard per host that has one, as a snapshot. Both public views are the same
    /// walk over the same scopes and differ only in which guard they read.
    /// </summary>
    private Dictionary<string, T> ByHost<T>(Func<HostScope, T?> guard)
        where T : class
    {
        var found = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (HostScope scope in _hosts.Scopes)
        {
            if (guard(scope) is { } value)
            {
                found[scope.Host] = value;
            }
        }

        return found;
    }

    private bool ShouldRetry(HttpRequestMessage request) => _policy.Attempts > 1 && IsRepeatable(request);

    private bool IsRepeatable(HttpRequestMessage request)
    {
        // An explicit declaration beats everything, in both directions: whoever wrote the request
        // knows whether it carries an idempotency key, and the client registration does not.
        if (request.Options.TryGetValue(ResilienceHttp.Repeatable, out bool declared))
        {
            return declared;
        }

        // An unrecognized method is treated as unsafe. Retrying something the library has never
        // heard of is a guess, and the direction to guess in is the one that does not duplicate it.
        return IsIdempotentMethod(request.Method) || _options.RetryUnsafeMethods;
    }

    private static bool IsIdempotentMethod(HttpMethod method) =>
        method == HttpMethod.Get
        || method == HttpMethod.Head
        || method == HttpMethod.Put
        || method == HttpMethod.Delete
        || method == HttpMethod.Options
        || method == HttpMethod.Trace;

    private Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        base.SendAsync(request, cancellationToken);
}
