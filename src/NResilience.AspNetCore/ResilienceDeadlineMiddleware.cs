using Microsoft.AspNetCore.Http;

namespace NResilience.AspNetCore;

/// <summary>
///     Reads the deadline the caller sent and publishes it for the rest of the request, so a policy
///     with <see cref="Resilience.UseAmbientDeadline" /> set is bounded by the time its caller is
///     actually still waiting.
///     <para>
///         This is the half of deadline propagation that needs a server. The other half -
///         <see cref="Http.HttpResilienceOptions.PropagateDeadline" /> - is in the core package,
///         because a handler already owns the whole call and knows what is left of it.
///     </para>
/// </summary>
/// <remarks>
///     It does not reject a request whose deadline has already expired, and that is deliberate: the
///     request may well be answerable from cache, or from work that costs nobody anything. What an
///     expired deadline stops is the outbound calls - each one fails immediately with
///     <see cref="DeadlineExceededException" /> rather than asking a dependency for an answer nobody is
///     waiting for.
/// </remarks>
internal sealed class ResilienceDeadlineMiddleware(RequestDelegate next, ResilienceDeadlineOptions options, TimeProvider time)
{
    /// <summary>Runs the rest of the pipeline, with the inbound deadline published for its duration.</summary>
    /// <param name="context">The request.</param>
    /// <returns>A task that completes when the rest of the pipeline does.</returns>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // No header, an unreadable one, or one this service does not believe: the request runs exactly
        // as it would without the middleware, bounded by whatever each policy says. A deadline is only
        // ever used to tighten a bound, so failing to read one is never worse than not being sent one.
        if (!context.Request.Headers.TryGetValue(options.Header, out var values)
            || !ResilienceDeadline.TryParse(values.Count > 0 ? values[^1] : null, out var remaining)
            || (options.Maximum is { } cap && remaining > cap))
            return next(context);

        var scope = ResilienceDeadline.Begin(remaining - options.Reserve, time);

        // Not a `using` on an async method: the middleware has nothing else to await, so keeping it
        // synchronous keeps a state-machine box off every request. The continuation restores the
        // previous ambient deadline whether the pipeline succeeded or threw.
        Task pending;

        try
        {
            pending = next(context);
        }
        catch
        {
            // The rest of the pipeline threw before it returned a task, so there is no continuation to
            // restore the scope.
            scope.Dispose();
            throw;
        }

        return pending.IsCompleted
            ? Finish(scope, pending)
            : pending.ContinueWith(
                static (completed, state) => Finish((ResilienceDeadline.DeadlineScope)state!, completed),
                scope,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.DenyChildAttach,
                TaskScheduler.Default).Unwrap();
    }

    /// <summary>Restores the previous ambient deadline and hands the pipeline's own outcome back.</summary>
    private static Task Finish(ResilienceDeadline.DeadlineScope scope, Task completed)
    {
        scope.Dispose();
        return completed;
    }
}
