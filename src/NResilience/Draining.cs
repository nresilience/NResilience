namespace NResilience;

/// <summary>
///     Whether this process is shutting down, and the one thing the executor does about it: it stops
///     retrying.
///     <para>
///         A pod that receives <c>SIGTERM</c> gets a grace period, typically 30 seconds. A call that is
///         mid-retry when the signal arrives will otherwise keep retrying into it - holding a
///         connection, delaying the rollout, and sending an attempt to a dependency whose load balancer
///         has already stopped routing to this instance. That retry cannot succeed in any way that
///         matters, because nobody is going to read the response.
///     </para>
///     <para>
///         The latch is here and the subscription is not. This package has no dependency on
///         <c>Microsoft.Extensions.Hosting</c> and will not acquire one, so core exposes the flag and
///         the reason, <c>NResilience.Extensions</c> sets it from
///         <c>IHostApplicationLifetime.ApplicationStopping</c>, and a console app or a worker without the
///         hosting package sets it itself.
///     </para>
/// </summary>
/// <example>
///     <code>
/// // A host without NResilience.Extensions, or any process that knows it is going away.
/// AppDomain.CurrentDomain.ProcessExit += (_, _) => Draining.Begin();
/// </code>
/// </example>
/// <remarks>
///     <b>In-flight attempts are not touched.</b> Cancelling work that might still complete is the
///     host's job, and the host already has the token for it. Draining stops the next attempt, not the
///     current one.
/// </remarks>
public static class Draining
{
    /// <summary>
    ///     The grace period this process is draining through, or null while it is not draining.
    ///     <para>
    ///         A reference so that <see cref="IsDraining" /> is a single volatile read of a single field -
    ///         the cheapest thing on the retry path - rather than a flag and a deadline that could
    ///         disagree.
    ///     </para>
    /// </summary>
    private static volatile Grace? _grace;

    /// <summary>Whether <see cref="Begin()" /> has been called. One volatile read.</summary>
    public static bool IsDraining => _grace is not null;

    /// <summary>
    ///     How long is left of the grace period <see cref="Begin(TimeSpan, TimeProvider)" /> was given.
    ///     <see cref="TimeSpan.Zero" /> once it has run out, and null when this process is not draining
    ///     or began draining without one.
    /// </summary>
    public static TimeSpan? Remaining
    {
        get
        {
            var left = _grace?.Left();
            return left == Timeout.InfiniteTimeSpan ? null : left;
        }
    }

    /// <summary>
    ///     Latches the process as draining, without a grace period: no call starts another attempt, and
    ///     deadlines are left alone.
    /// </summary>
    public static void Begin() => Begin(Timeout.InfiniteTimeSpan);

    /// <summary>
    ///     Latches the process as draining, and clamps every deadline to what is left of the grace
    ///     period.
    /// </summary>
    /// <param name="grace">
    ///     How long the host will wait before it stops waiting - <c>HostOptions.ShutdownTimeout</c>, for a
    ///     .NET host. <see cref="Timeout.InfiniteTimeSpan" /> drains without clamping anything.
    /// </param>
    /// <param name="time">The clock the grace period is measured against. Defaults to <see cref="TimeProvider.System" />.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="grace" /> is negative and is not <see cref="Timeout.InfiniteTimeSpan" />.</exception>
    /// <remarks>
    ///     <b>The first call wins, and there is no way back.</b> A process that has started shutting down
    ///     does not stop, and a second signal must not be able to hand it a longer grace period than the
    ///     first one did.
    /// </remarks>
    public static void Begin(TimeSpan grace, TimeProvider? time = null)
    {
        if (grace < TimeSpan.Zero && grace != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(grace), grace, "A grace period cannot be negative.");

        Interlocked.CompareExchange(ref _grace, new Grace(grace, time ?? TimeProvider.System), null);
    }

    /// <summary>
    ///     The configured deadline, clamped by what is left of the grace period. The executor's one read
    ///     of the latch per call, taken beside <see cref="AmbientDeadline.Clamp" /> and for the same
    ///     reason: a call that outlives the grace period is a call nobody will read the answer to.
    /// </summary>
    internal static TimeSpan Clamp(TimeSpan configured)
    {
        if (_grace is not { } grace)
            return configured;

        var left = grace.Left();

        if (left == Timeout.InfiniteTimeSpan)
            return configured;

        if (configured == Timeout.InfiniteTimeSpan)
            return left;

        return left < configured ? left : configured;
    }

    /// <summary>
    ///     Unlatches, for tests. There is no public counterpart on purpose: a process that has begun
    ///     shutting down does not change its mind, and an API that let it would be a way to keep
    ///     retrying into a rollout.
    /// </summary>
    internal static void Reset() => _grace = null;

    /// <summary>
    ///     A grace period as the pair that survives being awaited: when draining started, and how long
    ///     it had then. The shape <see cref="AmbientDeadline.Ambient" /> uses, for the same reason.
    /// </summary>
    private sealed class Grace(TimeSpan grace, TimeProvider time)
    {
        private readonly long _start = time.GetTimestamp();

        internal TimeSpan Left()
        {
            if (grace == Timeout.InfiniteTimeSpan)
                return Timeout.InfiniteTimeSpan;

            var left = grace - time.GetElapsedTime(_start);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }
}
