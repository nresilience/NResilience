using Microsoft.AspNetCore.Http;

namespace NResilience.AspNetCore;

/// <summary>
///     Reads what the caller propagated and publishes it for the rest of the request: the deadline, so
///     a policy with <see cref="Resilience.UseAmbientDeadline" /> set is bounded by the time its
///     caller is actually still waiting, and how much the work matters, so a policy with
///     <see cref="Resilience.UseAmbientCriticality" /> set stops amplifying work nobody is waiting
///     for.
///     <para>
///         This is the half of propagation that needs a server. The other half -
///         <see cref="HttpResilienceOptions.PropagateDeadline" /> and
///         <see cref="HttpResilienceOptions.PropagateCriticality" /> - is in the core package,
///         because a handler already owns the whole call and knows what is left of it.
///     </para>
/// </summary>
/// <remarks>
///     It does not reject a request whose deadline has already expired unless
///     <see cref="ResilienceDeadlineOptions.RejectExpired" /> says to, and that default is
///     deliberate: the request may well be answerable from cache, or from work that costs nobody
///     anything. What an expired deadline stops on its own is the outbound calls - each one fails
///     immediately with <see cref="DeadlineExceededException" /> rather than asking a dependency for
///     an answer nobody is waiting for.
/// </remarks>
internal sealed class ResilienceDeadlineMiddleware(RequestDelegate next, ResilienceDeadlineOptions options, TimeProvider time)
{
    /// <summary>Runs the rest of the pipeline, with what the caller propagated published for its duration.</summary>
    /// <param name="context">The request.</param>
    /// <returns>A task that completes when the rest of the pipeline does.</returns>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var headers = context.Request.Headers;

        // No header, an unreadable one, or one this service does not believe: the request runs exactly
        // as it would without the middleware, bounded by whatever each policy says. A deadline is only
        // ever used to tighten a bound, so failing to read one is never worse than not being sent one.
        var inherited =
            headers.TryGetValue(options.Header, out var values)
            && AmbientDeadline.TryParse(values.Count > 0 ? values[^1] : null, out var parsed)
            && (options.Maximum is not { } cap || parsed <= cap)
                ? parsed
                : (TimeSpan?)null;

        if (options.RejectExpired && inherited is { } left && left <= options.Reserve)
            return Refuse(context, left);

        // Both scopes are taken before the pipeline runs and restored together after it, which is why
        // they travel as one boxed pair rather than as two continuations.
        var scopes = new Scopes(
            inherited is { } remaining ? AmbientDeadline.Begin(remaining - options.Reserve, time) : null,
            options.ReadCriticality
            && headers.TryGetValue(options.CriticalityHeader, out var levels)
            && AmbientCriticality.TryParse(levels.Count > 0 ? levels[^1] : null, out var criticality)
                ? AmbientCriticality.Begin(criticality)
                : null);

        if (scopes.IsEmpty)
            return next(context);

        // Not a `using` on an async method: the middleware has nothing else to await, so keeping it
        // synchronous keeps a state-machine box off every request. The continuation restores the
        // previous ambient values whether the pipeline succeeded or threw.
        Task pending;

        try
        {
            pending = next(context);
        }
        catch
        {
            // The rest of the pipeline threw before it returned a task, so there is no continuation to
            // restore the scopes.
            scopes.Dispose();
            throw;
        }

        return pending.IsCompleted
            ? Finish(scopes, pending)
            : pending.ContinueWith(
                static (completed, state) => Finish((Scopes)state!, completed),
                scopes,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.DenyChildAttach,
                TaskScheduler.Default).Unwrap();
    }

    /// <summary>Restores the previous ambient values and hands the pipeline's own outcome back.</summary>
    private static Task Finish(Scopes scopes, Task completed)
    {
        scopes.Dispose();
        return completed;
    }

    /// <summary>
    ///     Answers a request that arrived with nothing left to spend, without running the pipeline.
    /// </summary>
    /// <remarks>
    ///     504 rather than a 503, and not configurable: the caller's own deadline is what ran out, so
    ///     this is a gateway timeout in the sense the status was defined for, and there is no
    ///     <c>Retry-After</c> to give because coming back later does not make the request younger.
    /// </remarks>
    private static Task Refuse(HttpContext context, TimeSpan remaining)
    {
        context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;

        var problem = new ResilienceProblemDetails
        {
            Type = "urn:nresilience:deadline-expired-on-arrival",
            Title = "Deadline Expired On Arrival",
            Status = StatusCodes.Status504GatewayTimeout,

            // The number the caller sent, not a name, a route or anything else about this service.
            Detail = FormattableString.Invariant(
                $"The request arrived with {remaining.TotalMilliseconds:F0}ms left, which is not enough to answer it."),
            Instance = context.Request.Path,
        };

        return context.Response.WriteAsJsonAsync(
            problem, ResilienceProblemJsonContext.Default.ResilienceProblemDetails, "application/problem+json", context.RequestAborted);
    }

    /// <summary>
    ///     The two scopes one request takes, so the continuation restores both. Boxed once, which is
    ///     what the single-scope version already cost.
    /// </summary>
    private sealed class Scopes(AmbientDeadline.Scope? deadline, AmbientCriticality.Scope? criticality)
    {
        internal bool IsEmpty => deadline is null && criticality is null;

        internal void Dispose()
        {
            // Restored in the reverse of the order they were taken, which is what a nested `using`
            // would do and what keeps a caller's own outer scopes intact.
            criticality?.Dispose();
            deadline?.Dispose();
        }
    }
}
