using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace NResilience.Extensions.Internal;

/// <summary>
///     The join between the host's lifecycle and <see cref="Draining" />: one subscription to
///     <c>ApplicationStopping</c>, made when the host starts.
/// </summary>
/// <remarks>
///     <para>
///         An <see cref="IHostedService" /> rather than a constructor-injected singleton, because
///         nothing resolves a singleton the container is never asked for - and because a bare
///         <c>ServiceCollection.BuildServiceProvider()</c> has no host to start, so a registration that
///         is only ever a hosted service costs a container without one exactly nothing.
///     </para>
///     <para>
///         <c>ApplicationStopping</c> rather than this service's own <see cref="StopAsync" />: stopping
///         hosted services is one of the things the grace period is spent on, so by the time
///         <c>StopAsync</c> runs the calls this exists to stop have already had most of it.
///     </para>
/// </remarks>
internal sealed class DrainSubscription(IHostApplicationLifetime lifetime, IOptions<ResilienceDrainOptions> options) : IHostedService, IDisposable
{
    private CancellationTokenRegistration _registration;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;

        if (!settings.DrainOnShutdown)
            return Task.CompletedTask;

        if (settings.Grace < TimeSpan.Zero && settings.Grace != Timeout.InfiniteTimeSpan)
        {
            throw new ResilienceConfigurationException(
                $"ResilienceDrainOptions.Grace is {settings.Grace}. A grace period cannot be negative; use Timeout.InfiniteTimeSpan to drain without clamping deadlines.");
        }

        // The grace period is captured as the callback's state rather than closed over, so the
        // subscription costs one box at startup instead of a display class.
        _registration = lifetime.ApplicationStopping.Register(static state => Draining.Begin((TimeSpan)state!), settings.Grace);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _registration.Dispose();
}
