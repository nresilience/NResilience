namespace NResilience.Extensions;

/// <summary>
///     Whether registered policies stop retrying when the host starts shutting down, and how long the
///     grace period is.
///     <para>
///         Process-wide, like <see cref="ResilienceLoggingOptions" /> and for the same reason: a
///         process is either draining or it is not, and a policy that kept retrying through a rollout
///         while the one beside it stopped would be a very hard incident to read.
///     </para>
/// </summary>
/// <example>
///     <code>
/// services.AddResilienceDraining(o => o.DrainOnShutdown = false);
/// </code>
/// </example>
public sealed class ResilienceDrainOptions
{
    /// <summary>
    ///     Whether <c>IHostApplicationLifetime.ApplicationStopping</c> latches
    ///     <see cref="Draining" />. On by default: every registration turns it on, and this is how it is
    ///     turned off.
    /// </summary>
    /// <remarks>
    ///     Turn it off for a process whose outbound calls must run to their own bounds during shutdown -
    ///     a drain job that has to finish flushing before the host goes. Everything else wants the
    ///     default: a retry sent after the load balancer stopped routing to this instance is a retry
    ///     nobody will read the answer to.
    /// </remarks>
    public bool DrainOnShutdown { get; set; } = true;

    /// <summary>
    ///     How long the host waits before it stops waiting, which every deadline is clamped to once
    ///     draining begins. Thirty seconds, which is what <c>HostOptions.ShutdownTimeout</c> defaults to.
    ///     <see cref="Timeout.InfiniteTimeSpan" /> drains without clamping anything.
    /// </summary>
    /// <remarks>
    ///     <b>Set this to match if the host's own timeout was changed</b> - <c>HostOptions</c> lives in
    ///     <c>Microsoft.Extensions.Hosting</c>, which this package deliberately does not reference, so
    ///     the two numbers are not read from one place. Setting it lower than the host's leaves room for
    ///     whatever runs after the calls stop, such as flushing telemetry; setting it higher does not buy
    ///     time the host does not have.
    /// </remarks>
    public TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(30);
}
