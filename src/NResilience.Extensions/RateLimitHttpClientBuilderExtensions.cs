using System.Net.Http;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Configuration;
using NResilience;
using NResilience.Extensions;
using NResilience.Extensions.Internal;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// <c>AddRateLimit()</c> on an <see cref="IHttpClientBuilder"/>: outbound admission control, one
/// permit per attempt.
/// <para>
/// Call it <b>after</b> <c>AddResilience()</c> on the same client. Handlers run in registration
/// order, outermost first, so that puts the limiter inner to the resilience handler - where it is
/// asked for a permit on every attempt rather than once per operation. The other order is refused at
/// registration time.
/// </para>
/// </summary>
/// <example>
/// <code>
/// services.AddHttpClient&lt;MyClient&gt;()
///         .AddResilience()
///         .AddRateLimit(o => o.PermitsPerSecond = 100);
/// </code>
/// </example>
public static class RateLimitHttpClientBuilderExtensions
{
    /// <summary>
    /// Adds a rate limit handler using a limiter you own.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="limiter">
    /// The limiter. Not disposed by the handler, so one limiter can be shared across several clients
    /// that are meant to share a quota.
    /// </param>
    /// <param name="name">The name reported on <see cref="RateLimitedException.Limiter"/> and in the metrics. Defaults to the client's name.</param>
    /// <returns>The client builder.</returns>
    public static IHttpClientBuilder AddRateLimit(this IHttpClientBuilder builder, RateLimiter limiter, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(limiter);

        Mark(builder);

        string reported = name ?? builder.Name;
        return builder.AddHttpMessageHandler(() => new RateLimitHandler(limiter, reported, owned: false));
    }

    /// <summary>
    /// Adds a rate limit handler built from options, per host by default.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="configure">The limit. Set exactly one of <see cref="RateLimitOptions.PermitsPerSecond"/>, <see cref="RateLimitOptions.Permits"/> with <see cref="RateLimitOptions.Window"/>, or <see cref="RateLimitOptions.Concurrency"/>.</param>
    /// <returns>The client builder.</returns>
    /// <exception cref="ResilienceConfigurationException">The options do not describe one limiter, or the handler order is wrong.</exception>
    public static IHttpClientBuilder AddRateLimit(this IHttpClientBuilder builder, Action<RateLimitOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new RateLimitOptions();
        configure(options);

        return Add(builder, options);
    }

    /// <summary>
    /// Adds a rate limit handler bound from configuration.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="section">The section.</param>
    /// <returns>The client builder.</returns>
    /// <remarks>
    /// Bound once, at registration time, rather than through <c>IOptionsMonitor</c>. A limiter holds
    /// live state - its permits, and anyone queued for one - and rebuilding it on reload would hand
    /// every waiting caller a permit at once, which is the opposite of what a limiter is for. Change
    /// a limit by restarting the process.
    /// </remarks>
    /// <exception cref="ResilienceConfigurationException">The section does not describe one limiter, or the handler order is wrong.</exception>
    public static IHttpClientBuilder AddRateLimit(this IHttpClientBuilder builder, IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(section);

        var options = new RateLimitOptions();
        section.Bind(options);

        return Add(builder, options);
    }

    private static IHttpClientBuilder Add(IHttpClientBuilder builder, RateLimitOptions options)
    {
        options.Validate();
        Mark(builder);

        string reported = options.Name ?? builder.Name;

        if (!options.PerHost)
        {
            return builder.AddHttpMessageHandler(() => new RateLimitHandler(options.ToLimiter(), reported, owned: true));
        }

        return builder.AddHttpMessageHandler(() => new RateLimitHandler(
            PartitionedRateLimiter.Create<HttpRequestMessage, string>(
                request => RateLimitPartition.Get(Host(request), _ => options.ToLimiter()),

                // The same comparer the per-host breakers and budgets use, so all three agree on
                // what one host is.
                StringComparer.OrdinalIgnoreCase),
            reported));
    }

    /// <summary>
    /// The partition key, derived exactly as <c>ResilienceHandler</c> derives its host scope so the
    /// limiter, the breaker and the budget partition identically.
    /// </summary>
    private static string Host(HttpRequestMessage request) => request.RequestUri?.Authority ?? string.Empty;

    private static void Mark(IHttpClientBuilder builder) =>
        HandlerOrder.For(builder.Services).RateLimit.TryAdd(builder.Name, 0);
}
