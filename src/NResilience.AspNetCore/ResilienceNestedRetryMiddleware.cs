using Microsoft.AspNetCore.Http;

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
///         <see cref="Http.ResilienceHttp.NestedRetryHeader" /> so the next hop can know too.
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
        if (!context.Request.Headers.TryGetValue(options.Header, out var values)
            || values.Count == 0
            || !ResilienceNestedRetry.IsMarker(values[^1]))
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

    /// <summary>Restores the previous ambient flag and hands the pipeline's own outcome back.</summary>
    private static Task Finish(ResilienceNestedRetry.NestedRetryScope scope, Task completed)
    {
        scope.Dispose();
        return completed;
    }
}