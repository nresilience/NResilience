namespace NResilience.Http;

/// <summary>
///     Per-request helpers over <see cref="HttpResilience" />'s option keys.
/// </summary>
public static class ResilienceHttpRequestExtensions
{
    /// <summary>The IETF draft header most services that deduplicate use. A draft, not a standard.</summary>
    private const string DefaultIdempotencyHeader = "Idempotency-Key";

    /// <summary>
    ///     Marks this request as safe to send more than once. Sets
    ///     <see cref="HttpResilience.Repeatable" />, so the handler retries it whatever its method
    ///     says, and stamps an idempotency key header when one is supplied, so the service can
    ///     discard the duplicate. The two serve different consumers and a retryable POST needs both.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="idempotencyKey">
    ///     The key the service deduplicates on. Null leaves the headers alone, for a service that
    ///     names its key header something else or does not need one.
    /// </param>
    /// <param name="headerName">The header the key is stamped under.</param>
    /// <returns>The same request, so this composes in an initializer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request" /> is null.</exception>
    /// <example>
    ///     <code>
    /// var request = new HttpRequestMessage(HttpMethod.Post, "/orders") { Content = body }
    ///     .MarkRepeatable(Guid.NewGuid().ToString());
    /// </code>
    /// </example>
    public static HttpRequestMessage MarkRepeatable(
        this HttpRequestMessage request,
        string? idempotencyKey = null,
        string headerName = DefaultIdempotencyHeader)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Options.Set(HttpResilience.Repeatable, true);

        // Contains first: TryAddWithoutValidation appends, and two idempotency keys on one request
        // is a request most services reject outright.
        if (idempotencyKey is not null && !request.Headers.Contains(headerName))
        {
            request.Headers.TryAddWithoutValidation(headerName, idempotencyKey);
        }

        return request;
    }

    /// <summary>
    ///     Marks this request as one that must be sent at most once, whatever its method and
    ///     whatever <see cref="HttpResilienceOptions.RetryUnsafeMethods" /> says.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The same request.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request" /> is null.</exception>
    public static HttpRequestMessage MarkSingleShot(this HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Options.Set(HttpResilience.Repeatable, false);
        return request;
    }
}
