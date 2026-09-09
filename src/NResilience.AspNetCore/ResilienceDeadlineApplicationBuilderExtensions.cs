using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace NResilience.AspNetCore;

/// <summary>
///     The one registration this package exists for.
/// </summary>
public static class ResilienceDeadlineApplicationBuilderExtensions
{
    /// <summary>
    ///     Reads what the caller propagated and publishes it for the rest of the request: the deadline,
    ///     so every policy with <see cref="Resilience.UseAmbientDeadline" /> set is bounded by
    ///     <c>min(its own deadline, the time the caller is still waiting)</c>, and how much the work
    ///     matters, so every policy with <see cref="Resilience.UseAmbientCriticality" /> set stops
    ///     amplifying a request nobody is waiting for.
    ///     <para>
    ///         One pass for both, because they arrive together and neither is worth a second walk over
    ///         the same headers. <see cref="ResilienceDeadlineOptions.ReadCriticality" /> turns the
    ///         second half off.
    ///     </para>
    /// </summary>
    /// <param name="app">The pipeline.</param>
    /// <param name="configure">
    ///     Changes the headers it reads, caps the deadline it believes, reserves part of the deadline
    ///     for this service's own work, or refuses a request that arrives with nothing left to spend.
    /// </param>
    /// <returns><paramref name="app" />, so the call chains.</returns>
    /// <remarks>
    ///     Register it early - before anything that makes an outbound call, which in practice means
    ///     before routing. The clock is <see cref="TimeProvider" /> from the container when one is
    ///     registered, and <see cref="TimeProvider.System" /> otherwise, so a test can move the inbound
    ///     deadline the same way it moves a policy's.
    /// </remarks>
    /// <example>
    ///     <code>
    /// var app = builder.Build();
    /// app.UseResilienceDeadline();
    /// </code>
    /// </example>
    public static IApplicationBuilder UseResilienceDeadline(this IApplicationBuilder app, Action<ResilienceDeadlineOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = new ResilienceDeadlineOptions();
        configure?.Invoke(options);

        ArgumentException.ThrowIfNullOrWhiteSpace(options.Header);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CriticalityHeader);

        var time = app.ApplicationServices.GetService<TimeProvider>() ?? TimeProvider.System;

        return app.UseMiddleware<ResilienceDeadlineMiddleware>(options, time);
    }
}
