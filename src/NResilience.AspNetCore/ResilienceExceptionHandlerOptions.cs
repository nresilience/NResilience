using Microsoft.AspNetCore.Http;

namespace NResilience.AspNetCore;

/// <summary>What the exception handler writes, for the applications that want it different.</summary>
public sealed class ResilienceExceptionHandlerOptions
{
    /// <summary>
    ///     The status for <see cref="DeadlineExceededException" /> - the whole call ran out of time.
    ///     504 by default: this service did not get a timely answer from something it depends on,
    ///     which is what 504 is for.
    /// </summary>
    /// <remarks>
    ///     Separate from <see cref="AttemptTimeoutStatusCode" /> because a deadline and an attempt
    ///     timeout are different failures, and this library does not say "timeout" where either could
    ///     be meant. They default to the same code, so splitting them costs nothing until an
    ///     application wants the distinction on the wire.
    /// </remarks>
    public int DeadlineStatusCode { get; set; } = StatusCodes.Status504GatewayTimeout;

    /// <summary>
    ///     The status for <see cref="AttemptTimeoutException" /> - the last attempt ran out of time
    ///     with the call's deadline still unspent. 504 by default, for the same reason as
    ///     <see cref="DeadlineStatusCode" />.
    /// </summary>
    public int AttemptTimeoutStatusCode { get; set; } = StatusCodes.Status504GatewayTimeout;

    /// <summary>
    ///     The status for <see cref="CallRejectedException" /> - an open breaker or an exhausted retry
    ///     budget. 503 by default, with <c>Retry-After</c> when the rejection carried a hint.
    /// </summary>
    public int RejectedStatusCode { get; set; } = StatusCodes.Status503ServiceUnavailable;

    /// <summary>
    ///     The status for <see cref="RateLimitedException" />. 503 by default, not 429.
    ///     <para>
    ///         The refusal is always self-imposed - a limiter in this process said no before anything
    ///         left it. 429 says "you sent too many requests", which blames a caller for this service's
    ///         own admission control; 503 says "this service cannot take it right now", which is what
    ///         happened. ASP.NET Core's own rate limiter middleware defaults to 503 for the same reason.
    ///         Set this to 429 when the limiter really is per-caller quota.
    ///     </para>
    /// </summary>
    public int RateLimitedStatusCode { get; set; } = StatusCodes.Status503ServiceUnavailable;

    /// <summary>
    ///     Whether the response body carries the attempt count and elapsed time. Off by default:
    ///     how many times this service retried is internal structure, and a public caller has no
    ///     business seeing it. Turn it on behind a gateway, or in a non-production environment.
    /// </summary>
    public bool IncludeAttemptDetails { get; set; }
}
