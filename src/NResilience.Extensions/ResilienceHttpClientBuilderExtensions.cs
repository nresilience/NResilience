using NResilience;
using NResilience.Extensions;
using NResilience.Extensions.Internal;
using NResilience.Http;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// <c>AddResilience()</c> on an <see cref="IHttpClientBuilder"/> — the one line most people need,
/// and the highest-leverage API in the library.
/// </summary>
/// <example>
/// <code>
/// services.AddHttpClient&lt;MyClient&gt;().AddResilience();
/// services.AddHttpClient&lt;MyClient&gt;().AddResilience(Resilience.Http with { Attempts = 5 });
/// services.AddHttpClient&lt;MyClient&gt;().AddResilience("api", o => o.RetryUnsafeMethods = true);
/// </code>
/// </example>
public static class ResilienceHttpClientBuilderExtensions
{
    /// <summary>Adds the resilience handler to this client.</summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="policy">The policy. Defaults to <see cref="Resilience.Http"/>.</param>
    /// <param name="configureOptions">The HTTP-specific switches — idempotency, per-host scoping, transport-timeout ownership.</param>
    /// <param name="telemetry">Whether this client records to <see cref="ResilienceTelemetry"/>. On by default.</param>
    /// <returns>The client builder.</returns>
    /// <exception cref="ResilienceConfigurationException">The policy cannot be executed.</exception>
    public static IHttpClientBuilder AddResilience(
        this IHttpClientBuilder builder,
        Resilience? policy = null,
        Action<HttpResilienceOptions>? configureOptions = null,
        bool telemetry = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        Resilience effective = Named(policy ?? Resilience.Http, builder.Name);

        effective.Validate();

        return Add(builder, _ => telemetry ? effective.WithTelemetry() : effective, configureOptions, telemetry);
    }

    /// <summary>
    /// Adds the resilience handler to this client, using a policy registered with
    /// <c>services.AddResilience(name, …)</c>.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="policyName">The registered policy's name.</param>
    /// <param name="configureOptions">The HTTP-specific switches.</param>
    /// <param name="telemetry">Whether this client records to <see cref="ResilienceTelemetry"/>. On by default; a registration whose <see cref="ResilienceOptions.Telemetry"/> is false stays off either way.</param>
    /// <returns>The client builder.</returns>
    /// <remarks>
    /// The policy is read when the handler chain is built, which
    /// <see cref="IHttpClientFactory"/> does afresh every two minutes by default. A configuration
    /// reload therefore reaches an <see cref="HttpClient"/> at the next handler rotation rather than
    /// on the next request — the handler holds its per-host breakers and budgets, and rebuilding it
    /// per request to make reload instant would throw that state away on every call.
    /// </remarks>
    public static IHttpClientBuilder AddResilience(
        this IHttpClientBuilder builder,
        string policyName,
        Action<HttpResilienceOptions>? configureOptions = null,
        bool telemetry = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(policyName);

        builder.Services.AddResilience();

        return Add(
            builder,
            services =>
            {
                Resilience resolved = services.GetRequiredService<IResiliencePolicies>()[policyName];

                // Not renamed: a policy resolved by name was already named by its registration, and
                // that name is what the rest of the process reports it under.
                return telemetry ? resolved.WithTelemetry() : resolved;
            },
            configureOptions,
            telemetry);
    }

    /// <summary>
    /// Names the policy after the client unless it carries a name of its own.
    /// <para>
    /// A preset's name does not count as a name of its own: <see cref="Resilience.Http"/> is called
    /// "http", so without this every client in the process would report under one name and four of
    /// them would be indistinguishable in the metrics. The client name is the identity an operator
    /// is looking for.
    /// </para>
    /// </summary>
    private static Resilience Named(Resilience policy, string clientName) =>
        policy.Name is null || policy.Name == Resilience.Http.Name
            ? policy with { Name = clientName }
            : policy;

    private static IHttpClientBuilder Add(
        IHttpClientBuilder builder,
        Func<IServiceProvider, Resilience> policy,
        Action<HttpResilienceOptions>? configureOptions,
        bool telemetry)
    {
        var options = new HttpResilienceOptions();
        configureOptions?.Invoke(options);

        HandlerOrder order = HandlerOrder.For(builder.Services);
        if (order.RateLimit.ContainsKey(builder.Name))
        {
            throw new ResilienceConfigurationException(
                $"Client '{builder.Name}' registered AddRateLimit() before AddResilience(). The limiter has to be inner to " +
                "the resilience handler, or every retry bypasses the quota - call AddResilience() first.");
        }

        order.Resilience.TryAdd(builder.Name, 0);

        if (options.OwnTransportTimeout)
        {
            // HttpClient.Timeout defaults to 100 seconds and applies to the *entire* retry
            // sequence, not per attempt - a silent cap nothing in the policy can see. A
            // DelegatingHandler cannot reach the client in front of it, so this is the only place
            // the switch can be honoured, and it is the reason the option exists on an options
            // object rather than on the handler.
            builder.ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);
        }

        if (telemetry)
        {
            // First, so it is outermost: the span has to cover every attempt, and the handler added
            // next is the one that makes the attempts.
            string name = builder.Name;
            builder.AddHttpMessageHandler(() => new ResilienceTelemetryHandler(name));
        }

        builder.AddHttpMessageHandler(services => new ResilienceHandler(policy(services), options));

        return builder;
    }
}
