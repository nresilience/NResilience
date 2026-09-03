using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace NResilience.AspNetCore;

/// <summary>
///     Reads the retry marker the caller sent and publishes it for the rest of the request, so the
///     outbound calls this request makes report <see cref="CallEventKind.NestedRetry" /> themselves -
///     which is what makes the middle hop of a three-hop chain able to see the amplification it is
///     part of.
///     <para>
///         This is the half of nested-retry detection that needs a server. The other halves are in
///         the core package: a <see cref="Http.ResilienceHandler" /> knows when it is nested inside
///         another retrying handler in this process, and it stamps
///         <see cref="Http.HttpResilience.NestedRetryHeader" /> so the next hop can know too.
///     </para>
/// </summary>
/// <remarks>
///     It reports and does not intervene: the flag changes nothing about how this request behaves,
///     only what its outbound calls can tell you.
/// </remarks>
internal sealed class ResilienceNestedRetryMiddleware(RequestDelegate next, ResilienceNestedRetryOptions options)
{
    /// <summary>Runs the rest of the pipeline, with the caller's retry marker published for its duration.</summary>
    /// <param name="context">The request.</param>
    /// <returns>A task that completes when the rest of the pipeline does.</returns>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // No header, or one carrying anything other than the marker: the request runs exactly as it
        // would without the middleware. Silent failure on caller-controlled input, the same rule
        // ResilienceDeadline.TryParse follows.
        if (!context.Request.Headers.TryGetValue(options.Header, out var values) || !CarriesMarker(values))
            return next(context);

        var scope = ResilienceNestedRetry.Begin(callerRetrying: true);

        // Not a `using` on an async method: keeping the middleware synchronous keeps a state-machine
        // box off every request. See ResilienceDeadlineMiddleware for the same shape and reasoning.
        Task pending;

        try
        {
            pending = next(context);
        }
        catch
        {
            scope.Dispose();
            throw;
        }

        return pending.IsCompleted
            ? Finish(scope, pending)
            : pending.ContinueWith(
                static (completed, state) => Finish((ResilienceNestedRetry.NestedRetryScope)state!, completed),
                scope,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.DenyChildAttach,
                TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    ///     Whether any value on the header is the marker. Any rather than the last, because this is
    ///     the same question <c>ResilienceHandler</c> asks of an outbound request and the two halves
    ///     of one feature must not disagree: an intermediary that appends an empty value to a header
    ///     a retrying caller really did send must not turn the marker off. Unlike a deadline, where
    ///     the last hop's value is the only true one, a presence marker does not get less true for
    ///     having something written after it.
    /// </summary>
    private static bool CarriesMarker(StringValues values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (ResilienceNestedRetry.IsMarker(values[i]))
                return true;
        }

        return false;
    }

    /// <summary>Restores the previous ambient flag and hands the pipeline's own outcome back.</summary>
    private static Task Finish(ResilienceNestedRetry.NestedRetryScope scope, Task completed)
    {
        scope.Dispose();
        return completed;
    }
}