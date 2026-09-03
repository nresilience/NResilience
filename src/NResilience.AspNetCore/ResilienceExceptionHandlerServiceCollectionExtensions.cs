using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NResilience.AspNetCore;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     Registers the exception handler that maps NResilience's exceptions onto the responses they
///     mean.
/// </summary>
/// <remarks>
///     Provided in <c>Microsoft.Extensions.DependencyInjection</c> to align with the registration
///     patterns in <c>NResilience.Extensions</c>, whose registrations live in the same namespace so
///     one <c>using</c> gets them all.
/// </remarks>
public static class ResilienceExceptionHandlerServiceCollectionExtensions
{
    /// <summary>
    ///     Maps the exceptions NResilience invents onto the responses they mean: 504 for a call that
    ///     ran out of time, 503 with <c>Retry-After</c> for one a breaker, a retry budget or a rate
    ///     limiter refused. The body is an RFC 9457 problem document.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="configure">Changes the status codes, or turns on the attempt-count extension member.</param>
    /// <returns><paramref name="services" />, so the call chains.</returns>
    /// <remarks>
    ///     Call <c>app.UseExceptionHandler()</c> as well - that is the framework middleware that runs
    ///     registered handlers. Anything this handler does not recognize is reported unhandled, so it
    ///     composes with the application's own handlers and with MVC's exception filters.
    /// </remarks>
    /// <example>
    ///     <code>
    /// builder.Services.AddResilienceExceptionHandler();
    ///
    /// var app = builder.Build();
    /// app.UseExceptionHandler();
    /// </code>
    /// </example>
    public static IServiceCollection AddResilienceExceptionHandler(
        this IServiceCollection services,
        Action<ResilienceExceptionHandlerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Configure(null) throws, and null is the default: the no-argument call is the common one.
        if (configure is not null)
            services.Configure(configure);

        services
            .AddOptions<ResilienceExceptionHandlerOptions>()
            .Validate(
                static o => IsStatus(o.DeadlineStatusCode) && IsStatus(o.AttemptTimeoutStatusCode) &&
                            IsStatus(o.RejectedStatusCode) && IsStatus(o.RateLimitedStatusCode),
                "Status codes must be between 100 and 599.");

        // AddExceptionHandler<T>() is a plain AddSingleton, so calling this twice - a library and
        // the application it is hosted in, both wanting the mapping - would register the handler
        // twice. TryAddEnumerable keys on the implementation type, so it is a no-op the second
        // time and still leaves room for every other IExceptionHandler.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExceptionHandler, ResilienceExceptionHandler>());
        return services;
    }

    private static bool IsStatus(int code) => code is >= 100 and <= 599;
}
