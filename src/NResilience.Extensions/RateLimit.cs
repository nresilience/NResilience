using System.Threading.RateLimiting;

namespace NResilience.Extensions;

/// <summary>
///     The limiters worth having, named. Each returns a platform
///     <see cref="System.Threading.RateLimiting.RateLimiter" />, so nothing here is a wrapper you have to
///     keep: hold the result, hand it to <see cref="RateLimiterExtensions.AcquireOrThrowAsync(RateLimiter, CancellationToken)" />
///     or to <c>AddRateLimit</c>, and dispose it with whatever owns it.
///     <para>
///         A limiter is a different guard from the ones on a policy. A <see cref="Breaker" /> reacts to
///         evidence and a <see cref="RetryBudget" /> bounds retries as a fraction of traffic; a limiter bounds
///         the absolute rate, or the concurrency, of what leaves this process - before anything has gone
///         wrong.
///     </para>
/// </summary>
/// <example>
///     <code>
/// var limiter = Limit.PerSecond(100);
/// 
/// await policy.RunAsync(async ct =>
/// {
///     using RateLimitLease lease = await limiter.AcquireOrThrowAsync(ct);
///     return await client.GetAsync(url, ct);
/// }, cancellationToken);
/// </code>
/// </example>
public static class Limit
{
    /// <summary>
    ///     A rate limit expressed the way a published quota usually is: <paramref name="permits" /> calls
    ///     per second, with one second of burst.
    /// </summary>
    /// <param name="permits">Calls allowed per second. Also the burst ceiling.</param>
    /// <param name="queueLimit">
    ///     How many callers may wait for a permit. Zero - the default - refuses immediately instead, so
    ///     the refusal becomes a retry on the throttled backoff curve rather than opaque latency charged
    ///     against <see cref="Resilience.AttemptTimeout" />. See the rate limiting feature page.
    /// </param>
    /// <returns>The limiter. The caller owns it and should dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="permits" /> is less than one, or <paramref name="queueLimit" /> is negative.</exception>
    public static RateLimiter PerSecond(int permits, int queueLimit = 0)
    {
        Check(permits, queueLimit);

        return new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = permits,
            TokensPerPeriod = permits,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            AutoReplenishment = true,
            QueueLimit = queueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    }

    /// <summary>
    ///     A rate limit over a longer window - a per-minute or per-hour quota.
    /// </summary>
    /// <param name="permits">Calls allowed per window.</param>
    /// <param name="window">The window.</param>
    /// <param name="queueLimit">How many callers may wait. Zero refuses immediately; see <see cref="PerSecond" />.</param>
    /// <returns>The limiter. The caller owns it and should dispose it.</returns>
    /// <remarks>
    ///     The window slides, in eight segments, rather than resetting on a boundary. A fixed window
    ///     lets a caller spend the whole quota at the end of one window and the whole of the next at the
    ///     start of the following one, which is 2x the nominal rate across the boundary and is exactly
    ///     what a server enforcing the same quota counts as a violation.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="permits" /> is less than one, <paramref name="queueLimit" /> is negative, or
    ///     <paramref name="window" /> is not positive.
    /// </exception>
    public static RateLimiter PerWindow(int permits, TimeSpan window, int queueLimit = 0)
    {
        Check(permits, queueLimit);

        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window), window, "The window must be positive.");

        return new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = window,
            SegmentsPerWindow = 8,
            AutoReplenishment = true,
            QueueLimit = queueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    }

    /// <summary>
    ///     A concurrency limit: at most <paramref name="permits" /> calls in flight at once. This is the
    ///     bulkhead - the permit is held for the duration of the attempt and released when it ends,
    ///     including when the attempt times out.
    /// </summary>
    /// <param name="permits">Calls allowed in flight at once.</param>
    /// <param name="queueLimit">How many callers may wait for a slot. Zero refuses immediately.</param>
    /// <returns>The limiter. The caller owns it and should dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="permits" /> is less than one, or <paramref name="queueLimit" /> is negative.</exception>
    public static RateLimiter Concurrency(int permits, int queueLimit = 0)
    {
        Check(permits, queueLimit);

        return new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = permits,
            QueueLimit = queueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    }

    private static void Check(int permits, int queueLimit)
    {
        if (permits < 1)
            throw new ArgumentOutOfRangeException(nameof(permits), permits, "A limiter must allow at least one call.");

        if (queueLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(queueLimit), queueLimit, "The queue limit cannot be negative.");
    }
}

/// <summary>
///     The whole translation layer between a platform limiter and the executor: acquire a permit, or
///     throw the one exception the engine treats as local admission control.
/// </summary>
/// <remarks>
///     Call it <b>inside</b> the callback you hand to <c>RunAsync</c>, not around it. Retry re-invokes
///     the callback, so a permit acquired inside it is acquired once per attempt - which is the only
///     granularity that means anything, because a guard a retry bypasses is not a guard. Acquiring it
///     outside would spend one permit for an operation that goes on to make three calls.
/// </remarks>
public static class RateLimiterExtensions
{
    /// <summary>
    ///     Acquires one permit, or throws <see cref="RateLimitedException" /> carrying whatever hint the
    ///     limiter supplied.
    /// </summary>
    /// <param name="limiter">The limiter.</param>
    /// <param name="cancellationToken">
    ///     The attempt's token. Inside a callback this is already
    ///     <c>min(AttemptTimeout, remaining deadline)</c> linked with the caller's token, so a queueing
    ///     acquire is bounded by the policy's own time budget with nothing further to configure.
    /// </param>
    /// <returns>The acquired lease. Dispose it with <c>using</c>, which is what releases a concurrency permit.</returns>
    /// <exception cref="RateLimitedException">The limiter refused.</exception>
    public static ValueTask<RateLimitLease> AcquireOrThrowAsync(this RateLimiter limiter, CancellationToken cancellationToken = default) =>
        limiter.AcquireOrThrowAsync(null, cancellationToken);

    /// <summary>Acquires one permit from a named limiter, or throws <see cref="RateLimitedException" />.</summary>
    /// <param name="limiter">The limiter.</param>
    /// <param name="name">The limiter's name, reported on the exception and in the metrics.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>The acquired lease.</returns>
    /// <exception cref="RateLimitedException">The limiter refused.</exception>
    public static async ValueTask<RateLimitLease> AcquireOrThrowAsync(this RateLimiter limiter, string? name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(limiter);

        var lease = await limiter.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
        return Check(lease, name);
    }

    /// <summary>
    ///     Acquires one permit for one partition, or throws <see cref="RateLimitedException" />. This is
    ///     what per-host limiting is built on.
    /// </summary>
    /// <typeparam name="TKey">The partition key type.</typeparam>
    /// <param name="limiter">The limiter.</param>
    /// <param name="key">The partition.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>The acquired lease.</returns>
    /// <exception cref="RateLimitedException">The limiter refused.</exception>
    public static ValueTask<RateLimitLease> AcquireOrThrowAsync<TKey>(this PartitionedRateLimiter<TKey> limiter, TKey key,
        CancellationToken cancellationToken = default)
        where TKey : notnull =>
        limiter.AcquireOrThrowAsync(key, null, cancellationToken);

    /// <summary>Acquires one permit for one partition of a named limiter.</summary>
    /// <typeparam name="TKey">The partition key type.</typeparam>
    /// <param name="limiter">The limiter.</param>
    /// <param name="key">The partition.</param>
    /// <param name="name">The limiter's name, reported on the exception and in the metrics.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>The acquired lease.</returns>
    /// <exception cref="RateLimitedException">The limiter refused.</exception>
    public static async ValueTask<RateLimitLease> AcquireOrThrowAsync<TKey>(this PartitionedRateLimiter<TKey> limiter, TKey key, string? name,
        CancellationToken cancellationToken = default)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(limiter);

        var lease = await limiter.AcquireAsync(key, 1, cancellationToken).ConfigureAwait(false);
        return Check(lease, name);
    }

    /// <summary>
    ///     The refusal hint, when the limiter supplied one. Null otherwise, which leaves the throttled
    ///     backoff curve to decide the delay.
    /// </summary>
    internal static TimeSpan? RetryAfterOf(RateLimitLease lease) =>
        lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : null;

    private static RateLimitLease Check(RateLimitLease lease, string? name)
    {
        if (lease.IsAcquired)
            return lease;

        // A denied lease still holds the limiter's metadata, and still has to be disposed - the
        // hint is read out of it first and the exception carries it instead.
        var retryAfter = RetryAfterOf(lease);
        lease.Dispose();

        throw new RateLimitedException(name, retryAfter);
    }
}
