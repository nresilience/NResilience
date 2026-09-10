using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace NResilience.Stress;

/// <summary>
///     The Polly HTTP arm: the smallest possible composition point, an <see cref="HttpMessageHandler" />
///     wrapping a retry x2-on-5xx + 10 s attempt-timeout pipeline - the same shape as the
///     NResilience arm's policy, and nothing else. The transport runs below it and is the same
///     transport every other arm uses.
///     <para>
///         A minimal bridge rather than Microsoft's integration on purpose: <c>Polly.Extensions.Http</c>
///         has no v8 release, and the v8 integration carries <see cref="IHttpClientFactory" />
///         machinery this comparison does not want to measure. Requests must be rebuilt per
///         attempt - an <see cref="HttpRequestMessage" /> cannot be resent - so the callback clones
///         the request the pipeline hands it, exactly the way a retrying HTTP strategy has to.
///     </para>
/// </summary>
internal static class PollyHttp
{
    public static HttpClient CreateClient() => new(new PipelineHandler(Arms.BuildTransport()), disposeHandler: true)
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>
    ///     A handler for one send at a time: the request travels in the callback's state tuple
    ///     rather than a field, because a field would race when the shared pipeline's callbacks
    ///     from two concurrent sends interleave. The transport wrapper exists so the static
    ///     callback can reach the protected <see cref="HttpMessageHandler.SendAsync" />.
    /// </summary>
    private sealed class PipelineHandler(HttpMessageHandler inner) : HttpMessageHandler
    {
        private static readonly ResiliencePipeline<HttpResponseMessage> Pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.Zero,
                UseJitter = false,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>().HandleResult(static r => (int)r.StatusCode >= 500),
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(10),
            })
            .Build();

        private readonly TransportHandler _transport = new(inner);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Pipeline.ExecuteAsync(
                static async (s, ct) =>
                {
                    // A request that has already been sent cannot be resent, so each attempt
                    // clones the original - the genuine per-retry cost every Polly HTTP
                    // integration incurs, which is why the NResilience handler rebuilds
                    // requests too, and why the raw arms, which never retry, need none of it.
                    using var clone = new HttpRequestMessage(s.Request.Method, s.Request.RequestUri);

                    foreach (var header in s.Request.Headers)
                        clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

                    return await s.Handler._transport.Dispatch(clone, ct).ConfigureAwait(false);
                },
                (Handler: this, Request: request),
                cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _transport.Dispose();

            base.Dispose(disposing);
        }

        /// <summary>
        ///     The transport wrapper: a <see cref="DelegatingHandler" /> exists exactly for this -
        ///     its own protected <c>SendAsync</c> delegates to the handler it wraps, so the
        ///     static pipeline callback can reach the transport through the public
        ///     <see cref="Dispatch" /> surface.
        /// </summary>
        private sealed class TransportHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
        {
            public Task<HttpResponseMessage> Dispatch(HttpRequestMessage request, CancellationToken cancellationToken) =>
                base.SendAsync(request, cancellationToken);
        }
    }
}
