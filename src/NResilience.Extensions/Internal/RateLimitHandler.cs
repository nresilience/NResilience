using System.Diagnostics;
using System.Threading.RateLimiting;

namespace NResilience.Extensions.Internal;

/// <summary>
///     Acquires one permit per attempt, inside <see cref="ResilienceHandler" />.
///     <para>
///         Its position in the chain is the whole design. <c>HttpCall</c> sends through the handler inner to
///         the resilience handler once per attempt, so a limiter installed there is asked for a permit on
///         every attempt and receives that attempt's cancellation token - which is already
///         <c>min(AttemptTimeout, remaining deadline)</c> linked with the caller's. Installed outside it
///         instead, the limiter would be asked once per operation and every retry would bypass the quota it
///         exists to respect.
///     </para>
///     <para>
///         The refusal is a <see cref="RateLimitedException" />, which the executor classifies as
///         <see cref="Verdict.Limited" /> itself: retried on the throttled curve honoring the limiter's own
///         hint, never counted as evidence against the host, and never charged to the retry budget.
///     </para>
/// </summary>
internal sealed class RateLimitHandler : DelegatingHandler
{
    private readonly RateLimiter? _limiter;
    private readonly string _name;
    private readonly bool _owned;
    private readonly PartitionedRateLimiter<HttpRequestMessage>? _partitioned;

    /// <summary>One limiter for the whole client.</summary>
    internal RateLimitHandler(RateLimiter limiter, string name, bool owned)
    {
        _limiter = limiter;
        _name = name;
        _owned = owned;
    }

    /// <summary>One limiter per host, keyed the way the per-host breakers and budgets are.</summary>
    internal RateLimitHandler(PartitionedRateLimiter<HttpRequestMessage> partitioned, string name)
    {
        _partitioned = partitioned;
        _name = name;
        _owned = true;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();

        // Disposing the lease is what releases a concurrency permit, and it has to happen when the
        // attempt ends however it ends - a timeout, a transport exception or a response. `using`
        // over the whole send is the only shape that holds for all three; a rate limiter's lease
        // holds nothing and disposing it is free.
        using var lease = _partitioned is not null
            ? await _partitioned.AcquireAsync(request, 1, cancellationToken).ConfigureAwait(false)
            : await _limiter!.AcquireAsync(1, cancellationToken).ConfigureAwait(false);

        ResilienceTelemetry.RecordLease(_name, lease.IsAcquired, Stopwatch.GetElapsedTime(start));

        if (!lease.IsAcquired)
            throw new RateLimitedException(RateLimiterExtensions.RetryAfterOf(lease), _name);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "NResilience is async-only. Use SendAsync: a limiter that blocks holds a thread for the whole wait.");

    protected override void Dispose(bool disposing)
    {
        // A limiter the registration built belongs to the handler; one the caller passed in does
        // not, and disposing it would break a limiter deliberately shared across clients.
        if (disposing && _owned)
        {
            _limiter?.Dispose();
            _partitioned?.Dispose();
        }

        base.Dispose(disposing);
    }
}
