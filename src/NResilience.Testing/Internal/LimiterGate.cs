using System.Threading.RateLimiting;

namespace NResilience.Testing.Internal;

/// <summary>
///     One run's limiter, and the acquire an attempt makes against it.
///     <para>
///         Nothing here models a limiter. It builds the caller's, refuses the two kinds a virtual
///         clock cannot answer for, and throws the <see cref="RateLimitedException" /> a real callback
///         throws - which the executor reads as local admission control all on its own.
///     </para>
/// </summary>
internal sealed class LimiterGate
{
    private readonly RateLimiter _limiter;

    internal LimiterGate(Func<TimeProvider, RateLimiter> build, TimeProvider clock, string where)
    {
        _limiter = build(clock)
            ?? throw new InvalidOperationException($"The limiter factory for {where} returned null. WithLimiter must build a limiter.");

        // A token bucket and a sliding window refill against the wall clock, and a run has none: five
        // virtual minutes are a few real milliseconds, so one would hand out a single window of permits
        // and refuse everything after it. Refused rather than reported, because a limit nobody
        // configured is worse than no limit at all.
        if (_limiter is ReplenishingRateLimiter)
        {
            throw new InvalidOperationException(
                $"The limiter for {where} is a replenishing limiter, which cannot run on a virtual clock: it refills against the "
                + "wall clock, and a simulation's five minutes are a few real milliseconds, so it would hand out one window of "
                + "permits and refuse everything after it. Limit.Concurrency and Limit.Adaptive measure what a run gives them.");
        }
    }

    /// <summary>Whether the limiter queued an attempt, which a virtual clock cannot carry.</summary>
    internal bool Queued { get; private set; }

    /// <summary>Attempts refused before they could leave the process.</summary>
    internal int Refused { get; private set; }

    /// <summary>The message a run reports when a limiter queued.</summary>
    internal static string QueuedMessage(string where) =>
        $"The limiter for {where} queued an attempt, and a queueing limiter cannot run on a virtual clock: the wait ends when "
        + "another caller releases a permit, and the platform resumes it on the thread pool rather than on the single thread a "
        + "run drives everything from. Build the limiter with queueLimit 0 - the default, and the one the library recommends, "
        + "because a refusal a policy can retry on the throttled backoff curve beats opaque latency charged against "
        + "AttemptTimeout.";

    /// <summary>
    ///     Acquires one permit for one attempt, or throws what a real callback throws.
    /// </summary>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>The lease. The caller disposes it when the attempt ends.</returns>
    /// <exception cref="RateLimitedException">The limiter refused, or queued.</exception>
    internal async ValueTask<RateLimitLease> AcquireAsync(CancellationToken cancellationToken)
    {
        var pending = _limiter.AcquireAsync(1, cancellationToken);

        // A limiter with a queue hands back a task that completes when somebody else's permit is
        // released, and it completes that task on the thread pool rather than inline. Every other
        // suspension in a run is on the virtual clock and resumes on the driver's thread, so awaiting
        // this one would put a second thread inside a single-threaded simulation and corrupt the
        // tallies it is halfway through writing. It is recorded, left un-awaited, and reported at the
        // end of the run.
        if (!pending.IsCompleted)
        {
            Queued = true;

            throw new RateLimitedException();
        }

        var lease = await pending.ConfigureAwait(false);

        if (lease.IsAcquired)
            return lease;

        // A denied lease still holds the limiter's metadata and still has to be disposed; the hint is
        // read out of it first and the exception carries it instead.
        var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : (TimeSpan?)null;

        lease.Dispose();
        Refused++;

        throw new RateLimitedException(retryAfter);
    }
}
