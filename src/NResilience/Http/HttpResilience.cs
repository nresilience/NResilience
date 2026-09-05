namespace NResilience;

/// <summary>
///     The per-request switches the HTTP integration reads. The headers it reads and writes are
///     <see cref="AmbientDeadline.Header" /> and <see cref="NestedRetry.Header" />, each next to the
///     ambient value it carries.
/// </summary>
public static class HttpResilience
{
    /// <summary>
    ///     Per-request override of the idempotency decision.
    ///     <para>
    ///         Set it to <see langword="true" /> on a POST or PATCH that is safe to repeat - which in
    ///         practice means one carrying an idempotency key - and to <see langword="false" /> on a
    ///         request that must be sent at most once whatever its method says.
    ///     </para>
    ///     <para>
    ///         When the key is set it decides, and it beats
    ///         <see cref="HttpResilienceOptions.RetryUnsafeMethods" /> in both directions. The whole point
    ///         of the option is that whoever writes the request knows something the client registration
    ///         cannot.
    ///     </para>
    /// </summary>
    /// <example>
    ///     <code>
    /// var request = new HttpRequestMessage(HttpMethod.Post, "/orders") { Content = body };
    /// request.Headers.Add("Idempotency-Key", key);
    /// request.Options.Set(HttpResilience.Repeatable, true);
    /// </code>
    /// </example>
    public static HttpRequestOptionsKey<bool> Repeatable { get; } = new("NResilience.Repeatable");

    /// <summary>
    ///     An <see cref="HttpClient" /> with an <see cref="HttpResilienceHandler" /> in front of it, built the
    ///     way the DI registration builds one - including taking ownership of the transport timeout.
    /// </summary>
    /// <param name="policy">The policy. Defaults to <see cref="Resilience.Http" />.</param>
    /// <param name="options">The HTTP-specific switches. Defaults to <see cref="HttpResilienceOptions" />'s own defaults.</param>
    /// <param name="innerHandler">The transport. Defaults to a fresh <see cref="HttpClientHandler" />.</param>
    /// <returns>The client. Disposing it disposes the handler chain.</returns>
    /// <remarks>
    ///     This exists so the handler is usable - and testable - without a DI container.
    ///     <c>AddHttpClient(…).AddResilience()</c> arrives with <c>NResilience.Extensions</c>; a single
    ///     long-lived client built here is the correct shape for everything else, because the per-host
    ///     breakers and budgets live on the handler and are worth nothing to a client that is rebuilt
    ///     per call.
    /// </remarks>
    public static HttpClient CreateClient(
        Resilience? policy = null,
        HttpResilienceOptions? options = null,
        HttpMessageHandler? innerHandler = null)
    {
        options ??= new HttpResilienceOptions();
        var handler = new HttpResilienceHandler(innerHandler ?? new HttpClientHandler(), policy, options);
        var client = new HttpClient(handler, true);

        if (options.OwnTransportTimeout)
        {
            // See HttpResilienceOptions.OwnTransportTimeout: the deadline on the policy becomes
            // the only bound.
            client.Timeout = Timeout.InfiniteTimeSpan;
        }

        return client;
    }
}
