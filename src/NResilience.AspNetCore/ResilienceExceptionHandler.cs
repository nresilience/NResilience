using System.Globalization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace NResilience.AspNetCore;

/// <summary>
///     Turns the four exceptions the library invents into the responses they mean: 504 for a call
///     that ran out of time, 503 with <c>Retry-After</c> for one a guard or a limiter refused.
///     <para>
///         Every service that lets these propagate writes the same try/catch, and the
///         <c>RetryAfter</c> properties exist precisely so a caller can be told when to come back -
///         which is a header, on a response, and nowhere the core library can reach.
///     </para>
/// </summary>
/// <remarks>
///     An <see cref="IExceptionHandler" /> rather than a middleware: it is a chain of responsibility,
///     so an exception this handler does not recognize falls through to the application's own
///     handlers and then to the framework's, and it is registered in DI rather than positioned in a
///     pipeline.
/// </remarks>
internal sealed class ResilienceExceptionHandler(IOptions<ResilienceExceptionHandlerOptions> options) : IExceptionHandler
{
    private readonly ResilienceExceptionHandlerOptions _options = options.Value;

    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(exception);

        if (Map(exception, _options) is not { } mapped)
            return false;

        // Bytes are already on the wire: the status cannot be changed and appending a problem
        // document to a half-written body would produce garbage. Reporting the exception unhandled
        // lets the framework log and abort the connection, which is the only honest outcome.
        if (context.Response.HasStarted)
            return false;

        context.Response.StatusCode = mapped.Status;

        if (mapped.RetryAfter is { } after && after > TimeSpan.Zero)
        {
            // Whole seconds, rounded up: telling a caller to come back sooner than the guard will
            // actually admit them buys another rejection.
            context.Response.Headers.RetryAfter =
                ((long)Math.Ceiling(after.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        var problem = new ResilienceProblemDetails
        {
            Type = mapped.Type,
            Title = mapped.Title,
            Status = mapped.Status,

            // The library already formats these readably - "The operation exceeded its 10s deadline
            // after 3 attempt(s)." - and they name no dependency, host or credential.
            Detail = exception.Message,
            Instance = context.Request.Path,
            Resilience = _options.IncludeAttemptDetails ? DetailsOf(exception) : null,
        };

        await context.Response
            .WriteAsJsonAsync(problem, ResilienceProblemJsonContext.Default.ResilienceProblemDetails,
                contentType: "application/problem+json", cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>The response an exception means, or null when it means nothing to this handler.</summary>
    private static Mapped? Map(Exception exception, ResilienceExceptionHandlerOptions options) => exception switch
    {
        DeadlineExceededException => new Mapped(
            options.DeadlineStatusCode, "urn:nresilience:deadline-exceeded", "Deadline Exceeded", null),

        AttemptTimeoutException => new Mapped(
            options.AttemptTimeoutStatusCode, "urn:nresilience:attempt-timeout", "Attempt Timeout", null),

        CallRejectedException { Reason: StopReason.BudgetExhausted } rejected => new Mapped(
            options.RejectedStatusCode, "urn:nresilience:retry-budget-exhausted", "Retry Budget Exhausted",
            rejected.RetryAfter),

        CallRejectedException rejected => new Mapped(
            options.RejectedStatusCode, "urn:nresilience:dependency-unavailable", "Dependency Unavailable",
            rejected.RetryAfter),

        RateLimitedException limited => new Mapped(
            options.RateLimitedStatusCode, "urn:nresilience:rate-limited", "Rate Limited", limited.RetryAfter),

        // Including ResilienceConfigurationException, which is a bug in the application's own setup
        // and deserves the 500 the framework gives it, and OperationCanceledException, whose caller
        // is not waiting for a problem document.
        _ => null,
    };

    /// <summary>
    ///     The attempt log, from the typed property when the exception carries one and from
    ///     <see cref="Exception.Data" /> otherwise - a <see cref="RateLimitedException" /> an
    ///     application threw itself has neither, and gets no extension member.
    /// </summary>
    private static ResilienceAttemptDetails? DetailsOf(Exception exception)
    {
        var log = exception switch
        {
            DeadlineExceededException deadline => deadline.Attempts,
            CallRejectedException rejected => rejected.Attempts,
            AttemptTimeoutException timedOut => timedOut.Attempts,
            _ => AttemptLog.Of(exception),
        };

        return log is { Count: > 0 }
            ? new ResilienceAttemptDetails { Attempts = log.Count, ElapsedMs = log.Elapsed.TotalMilliseconds }
            : null;
    }

    private readonly record struct Mapped(int Status, string Type, string Title, TimeSpan? RetryAfter);
}