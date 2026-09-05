using System.Diagnostics;

namespace NResilience.Extensions.Internal;

/// <summary>
///     Gives one logical operation - the whole retry sequence - a span of its own, outside the
///     <see cref="HttpResilienceHandler" /> so it spans every attempt rather than one of them.
///     <para>
///         This is the boundary a per-attempt HTTP span cannot show. Without it, three attempts against a
///         flaky dependency are three unrelated client spans and the trace never says that they were one
///         call that eventually succeeded - which is the whole question an operator is asking.
///     </para>
/// </summary>
/// <remarks>
///     Free when nobody is listening: <c>StartActivity</c> returns null unless a listener has sampled
///     the source, so an always-registered handler costs one virtual call and a null check.
/// </remarks>
internal sealed class ResilienceTelemetryHandler(string clientName) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var activity = ResilienceTelemetry.StartCall(clientName);

        if (activity is null)
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // The listener inside the executor annotates Activity.Current, which is this one for the
        // duration of the send - so attempts, retries and breaker transitions land on the span
        // covering the operation they belong to.
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            activity.SetTag("http.response.status_code", (int)response.StatusCode);
            return response;
        }
        catch (Exception error)
        {
            activity.SetStatus(ActivityStatusCode.Error, error.Message);
            activity.SetTag("exception.type", error.GetType().FullName);
            throw;
        }
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "NResilience is async-only. Use SendAsync: a retry loop that blocks holds a thread through every backoff delay.");
}
