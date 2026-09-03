using Grpc.Net.ClientFactory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NResilience;
using NResilience.Extensions;
using NResilience.Grpc;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     <c>AddGrpcResilience()</c> on the builder <c>AddGrpcClient&lt;T&gt;()</c> returns: the gRPC
///     integration, registered the way every other one is.
/// </summary>
/// <remarks>
///     <b>Not an <c>AddResilience</c> overload, and not a use for the existing one.</b>
///     <c>AddGrpcClient</c> hands back the same <see cref="IHttpClientBuilder" /> the HTTP extension
///     extends, so two overloads applicable with no arguments would be a compile error at every call
///     site. The distinct name also reads honestly, because the HTTP handler is not merely absent for
///     gRPC - it is wrong: every gRPC call is a <c>POST</c>, which the handler refuses to retry, so
///     <c>AddResilience()</c> on a gRPC client is an inert handler that adds overhead and retries
///     nothing.
///     <para>
///         Register this <i>first</i>, before any other interceptor. Interceptors registered after it
///         run inside the retry loop, which is where an auth interceptor refreshing a token wants to
///         be. gRPC's client factory does not expose the registrations already made, so this is a
///         documented rule rather than an enforced one.
///     </para>
/// </remarks>
/// <example>
///     <code>
/// services.AddGrpcClient&lt;Orders.OrdersClient&gt;(o =&gt; o.Address = new Uri("https://orders.internal:5001"))
///     .AddGrpcResilience();
/// </code>
/// </example>
public static class ResilienceGrpcClientBuilderExtensions
{
    /// <summary>Adds the resilience interceptor to this gRPC client.</summary>
    /// <param name="builder">The client builder <c>AddGrpcClient&lt;T&gt;()</c> returned.</param>
    /// <param name="policy">The policy. Defaults to <see cref="GrpcResilience.Default" />.</param>
    /// <param name="configureOptions">The gRPC-specific switches - idempotency, scoping, the wire deadline, transport-timeout ownership.</param>
    /// <param name="telemetry">Whether this client records to <see cref="ResilienceTelemetry" />. On by default.</param>
    /// <param name="logging">
    ///     The log level for this client's records. If null, the process default is used, which is
    ///     <see cref="ResilienceLoggingOptions.Profile" /> when <c>AddResilienceLogging</c> was called
    ///     and <see cref="ResilienceLogProfile.Default" /> otherwise.
    /// </param>
    /// <returns>The client builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is null.</exception>
    /// <exception cref="ResilienceConfigurationException">The policy or the options cannot be used.</exception>
    public static IHttpClientBuilder AddGrpcResilience(
        this IHttpClientBuilder builder,
        Resilience? policy = null,
        Action<GrpcResilienceOptions>? configureOptions = null,
        bool telemetry = true,
        ResilienceLogProfile? logging = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new GrpcResilienceOptions();
        configureOptions?.Invoke(options);
        options.Validate();

        var effective = Named(policy ?? GrpcResilience.Default, builder.Name);
        effective.Validate();

        if (options.OwnTransportTimeout)
        {
            // See GrpcResilienceOptions.OwnTransportTimeout. An interceptor cannot reach the channel
            // in front of it, so this is the only place the switch can be honored. Normally a no-op -
            // grpc-dotnet's own client factory already does this - and it is kept for the caller who
            // supplies a handler of their own.
            builder.ConfigureHttpClient(static client => client.Timeout = Timeout.InfiniteTimeSpan);
        }

        var name = builder.Name;

        // Channel scope, passed explicitly rather than left to the default: the other scope builds an
        // interceptor per resolved client, which hands every client a fresh breaker and a retry budget
        // that never accumulates. That is precisely the failure NRES005 exists to catch, and relying on
        // a default here would ship it from the registration meant to prevent it.
        builder.AddInterceptor(
            InterceptorScope.Channel,
            services =>
            {
                var interceptor = new ResilienceInterceptor(
                    Logged(telemetry ? effective.WithTelemetry() : effective, services, logging),
                    options,
                    name);

                // How the health check sees an interceptor: it walks ResilienceHealthOptions.Watched,
                // and nothing in it knows what an interceptor is. Registering the guards here needs no
                // new type, no new registry, and no change to the Extensions package.
                if (services.GetService<IOptions<ResilienceHealthOptions>>()?.Value is { } health)
                {
                    foreach (var (key, breaker) in interceptor.Breakers())
                    {
                        health.Watch($"{name}:{key}", breaker);
                    }

                    foreach (var (key, budget) in interceptor.Budgets())
                    {
                        health.Watch($"{name}:{key}", budget);
                    }
                }

                return interceptor;
            });

        return builder;
    }

    /// <summary>
    ///     Attaches the log listener using the category derived from the policy's name, which for a
    ///     client registered here is the client name.
    ///     <para>
    ///         First-attach-wins: a policy that is already logging retains its existing listener.
    ///     </para>
    /// </summary>
    private static Resilience Logged(Resilience policy, IServiceProvider services, ResilienceLogProfile? profile)
    {
        if (services.GetService<ILoggerFactory>() is not { } factory)
            return policy;

        var process = services.GetService<IOptions<ResilienceLoggingOptions>>()?.Value;

        if (profile is null)
            return policy.WithLogging(factory, process);

        return policy.WithLogging(factory, new ResilienceLoggingOptions
        {
            Profile = profile.Value,
            RepeatWindow = process?.RepeatWindow ?? new ResilienceLoggingOptions().RepeatWindow,
            IncludeStackTracesOnRetry = process?.IncludeStackTracesOnRetry ?? false,
            Level = process?.Level,
        });
    }

    /// <summary>
    ///     Names the policy after the client unless it carries a name of its own. A preset's name does
    ///     not count: <see cref="GrpcResilience.Default" /> is called "grpc", so without this every
    ///     gRPC client in the process would report under one name.
    /// </summary>
    private static Resilience Named(Resilience policy, string clientName) =>
        policy.Name is null || policy.Name == GrpcResilience.Default.Name
            ? policy with { Name = clientName }
            : policy;
}
