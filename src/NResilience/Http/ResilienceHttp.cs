using System.Net.Http;

namespace NResilience.Http;

/// <summary>
/// The per-request switches and the well-known header the HTTP integration reads and writes.
/// </summary>
public static class ResilienceHttp
{
    /// <summary>
    /// Per-request override of the idempotency decision.
    /// <para>
    /// Set it to <see langword="true"/> on a POST or PATCH that is safe to repeat - which in
    /// practice means one carrying an idempotency key - and to <see langword="false"/> on a
    /// request that must be sent at most once whatever its method says.
    /// </para>
    /// <para>
    /// When the key is set it decides, and it beats
    /// <see cref="HttpResilienceOptions.RetryUnsafeMethods"/> in both directions. The whole point
    /// of the option is that whoever writes the request knows something the client registration
    /// cannot.
    /// </para>
    /// </summary>
    /// <example>
    /// <code>
    /// var request = new HttpRequestMessage(HttpMethod.Post, "/orders") { Content = body };
    /// request.Headers.Add("Idempotency-Key", key);
    /// request.Options.Set(ResilienceHttp.Repeatable, true);
    /// </code>
    /// </example>
    public static HttpRequestOptionsKey<bool> Repeatable { get; } = new("NResilience.Repeatable");

    /// <summary>
    /// The header a retrying client stamps on every request it sends, so the service receiving it
    /// can see that its caller will retry.
    /// <para>
    /// Retries compose multiplicatively - three layers each retrying three times is 27 attempts at
    /// the bottom - and the amplification is invisible from any single layer. A service that reads
    /// this header off its inbound request knows it is already being retried, which is the
    /// information it needs to stop retrying again underneath.
    /// </para>
    /// </summary>
    /// <remarks>
    /// A <see cref="CallEventKind.NestedRetry"/> event is raised when a request that already
    /// carries this header is about to be retried again, and when one retrying handler executes
    /// inside another's attempt in the same process. The library reports it and does nothing else:
    /// silently dropping the caller's configured retries would be a bigger surprise than the
    /// amplification.
    /// </remarks>
    public const string NestedRetryHeader = "X-NResilience-Retrying";

    /// <summary>
    /// An <see cref="HttpClient"/> with a <see cref="ResilienceHandler"/> in front of it, built the
    /// way the DI registration builds one - including taking ownership of the transport timeout.
    /// </summary>
    /// <param name="policy">The policy. Defaults to <see cref="Resilience.Http"/>.</param>
    /// <param name="options">The HTTP-specific switches. Defaults to <see cref="HttpResilienceOptions"/>'s own defaults.</param>
    /// <param name="innerHandler">The transport. Defaults to a fresh <see cref="HttpClientHandler"/>.</param>
    /// <returns>The client. Disposing it disposes the handler chain.</returns>
    /// <remarks>
    /// This exists so the handler is usable - and testable - without a DI container.
    /// <c>AddHttpClient(…).AddResilience()</c> arrives with <c>NResilience.Extensions</c>; a single
    /// long-lived client built here is the correct shape for everything else, because the per-host
    /// breakers and budgets live on the handler and are worth nothing to a client that is rebuilt
    /// per call.
    /// </remarks>
    public static HttpClient CreateClient(
        Resilience? policy = null,
        HttpResilienceOptions? options = null,
        HttpMessageHandler? innerHandler = null)
    {
        options ??= new HttpResilienceOptions();
        var handler = new ResilienceHandler(innerHandler ?? new HttpClientHandler(), policy, options);
        var client = new HttpClient(handler, disposeHandler: true);

        if (options.OwnTransportTimeout)
        {
            // See HttpResilienceOptions.OwnTransportTimeout: the deadline on the policy becomes
            // the only bound.
            client.Timeout = Timeout.InfiniteTimeSpan;
        }

        return client;
    }
}
