using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace NResilience.AspNetCore;

/// <summary>
///     The one registration this package exists for.
/// </summary>
public static class ResilienceDeadlineApplicationBuilderExtensions
{
    /// <summary>
    ///     Reads the deadline a caller sent and publishes it for the rest of the request, so every
    ///     policy with <see cref="Resilience.UseAmbientDeadline" /> set is bounded by
    ///     <c>min(its own deadline, the time the caller is still waiting)</c>.
    /// </summary>
    /// <param name="app">The pipeline.</param>
    /// <param name="configure">Changes the header it reads, caps what it believes, or reserves part of the deadline for this service's own work.</param>
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

        var time = app.ApplicationServices.GetService<TimeProvider>() ?? TimeProvider.System;

        return app.UseMiddleware<ResilienceDeadlineMiddleware>(options, time);
    }
}
