using Microsoft.AspNetCore.Builder;

namespace NResilience.AspNetCore;

/// <summary>
///     The inbound half of nested-retry detection.
/// </summary>
public static class ResilienceNestedRetryApplicationBuilderExtensions
{
    /// <summary>
    ///     Reads the retry marker a caller sent and publishes it for the rest of the request, so the
    ///     outbound calls this request makes - through a retrying
    ///     <see cref="Http.ResilienceHandler" /> with
    ///     <see cref="Http.HttpResilienceOptions.DetectNestedRetries" /> set - report
    ///     <see cref="CallEventKind.NestedRetry" /> themselves.
    /// </summary>
    /// <param name="app">The pipeline.</param>
    /// <param name="configure">Changes the header it reads.</param>
    /// <returns><paramref name="app" />, so the call chains.</returns>
    /// <remarks>
    ///     Register it early - before anything that makes an outbound call, which in practice means
    ///     before routing. The middleware reports and does not intervene: it changes nothing about how
    ///     the request behaves, only what its outbound calls can tell you. Register
    ///     <see cref="ResilienceDeadlineApplicationBuilderExtensions.UseResilienceDeadline" />
    ///     alongside it when both halves of the picture are wanted - the two middlewares are the same
    ///     shape and are usually registered together.
    /// </remarks>
    /// <example>
    ///     <code>
    /// var app = builder.Build();
    /// app.UseResilienceNestedRetry();
    /// </code>
    /// </example>
    public static IApplicationBuilder UseResilienceNestedRetry(this IApplicationBuilder app, Action<ResilienceNestedRetryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = new ResilienceNestedRetryOptions();
        configure?.Invoke(options);

        ArgumentException.ThrowIfNullOrWhiteSpace(options.Header);

        return app.UseMiddleware<ResilienceNestedRetryMiddleware>(options);
    }
}