using Microsoft.Extensions.Diagnostics.HealthChecks;
using NResilience;
using NResilience.Extensions;
using NResilience.Extensions.Internal;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     <c>AddResilience()</c> on an <see cref="IHealthChecksBuilder" /> puts every breaker and retry
///     budget in the process on your health endpoint.
/// </summary>
/// <example>
///     <code>
/// services.AddHealthChecks().AddResilience();
///
/// // Or with the thresholds and statuses spelled out.
/// services.AddHealthChecks().AddResilience(configure: o =>
/// {
///     o.BudgetThreshold = 0.75;
///     o.Watch("payments", Policies.PaymentsBreaker);   // a policy held in a static field
/// });
/// </code>
/// </example>
public static class ResilienceHealthChecksBuilderExtensions
{
    /// <summary>The name the check is registered under when none is given.</summary>
    public const string DefaultName = "resilience";

    /// <summary>
    ///     Adds a health check reporting the state of every breaker and the utilization of every retry
    ///     budget this process can see: the ones behind policies registered with
    ///     <c>services.AddResilience(name, …)</c>, the per-host ones held by clients registered with
    ///     <c>AddResilience()</c>, and any handed to <see cref="ResilienceHealthOptions.Watch(string, Breaker)" />.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">The check's name, as it appears in the report.</param>
    /// <param name="configure">The thresholds, the reported statuses, and any extra guards to watch.</param>
    /// <param name="tags">Tags, for filtering one endpoint's checks from another's.</param>
    /// <returns>The health checks builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> or <paramref name="name" /> is null.</exception>
    /// <exception cref="ResilienceConfigurationException">The options cannot be used.</exception>
    /// <remarks>
    ///     The check reads state that is already there and contacts nothing, so it is safe on a liveness
    ///     endpoint as well as a readiness one. What it reports for an open breaker is
    ///     <see cref="HealthStatus.Degraded" /> by default rather than
    ///     <see cref="HealthStatus.Unhealthy" />; <see cref="ResilienceHealthOptions" /> carries the
    ///     reasoning, and both are configurable.
    /// </remarks>
    public static IHealthChecksBuilder AddResilience(
        this IHealthChecksBuilder builder,
        string name = DefaultName,
        Action<ResilienceHealthOptions>? configure = null,
        IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        var options = new ResilienceHealthOptions();
        configure?.Invoke(options);

        // Eagerly, so a threshold of 1.5 fails at startup rather than on the first probe - a health
        // check that throws is reported as unhealthy, which would be an outage caused by the thing
        // that exists to report one.
        options.Validate();

        // So IResiliencePolicies resolves even in a process whose only registration is on an
        // HttpClient. TryAddSingleton, so a container that already registered policies is untouched.
        builder.Services.AddResilience();

        // An explicit factory rather than AddCheck<T>(), which resolves the check through
        // ActivatorUtilities - reflection this package is AOT-clean without. The closure is built
        // once, at registration.
        //
        // failureStatus stays null, which reports an Unhealthy result as Unhealthy: the check
        // already returns the status the options asked for, and overriding it here would quietly
        // beat ResilienceHealthOptions.
        builder.Add(new HealthCheckRegistration(
            name,
            provider => new ResilienceHealthCheck(
                options,
                provider.GetService<IResiliencePolicies>(),
                provider.GetService<ResilienceHandlerRegistry>()),
            null,
            tags));

        return builder;
    }
}
