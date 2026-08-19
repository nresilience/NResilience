using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NResilience;
using NResilience.Extensions;
using NResilience.Extensions.Internal;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers named policies, so an application says what its dependencies are worth once and
/// injects <see cref="IResiliencePolicies"/> everywhere else.
/// </summary>
/// <remarks>
/// In <c>Microsoft.Extensions.DependencyInjection</c> rather than <c>NResilience.Extensions</c>,
/// which is where every registration method in the ecosystem lives and therefore where people look
/// for one. The types these methods take and return are in their own namespaces.
/// </remarks>
public static class ResilienceServiceCollectionExtensions
{
    /// <summary>Registers a policy under a name.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The name it is resolved by.</param>
    /// <param name="policy">The policy.</param>
    /// <param name="configure">Runs after configuration binding, for the things JSON cannot hold — a classifier, a hook, a shared breaker.</param>
    /// <returns>The service collection.</returns>
    /// <exception cref="ResilienceConfigurationException">The policy cannot be executed. Registration validates eagerly, so a bad policy fails at startup rather than on the first request.</exception>
    /// <example>
    /// <code>
    /// services.AddResilience("api", Resilience.Http with { Deadline = TimeSpan.FromSeconds(10) });
    /// </code>
    /// </example>
    public static IServiceCollection AddResilience(
        this IServiceCollection services,
        string name,
        Resilience policy,
        Func<Resilience, Resilience>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(policy);

        // Eagerly, at registration, which is one of the three places the design promises validation
        // happens. A deadline of minus one second should not survive until the first request.
        policy.Validate();

        Register(services, name);
        services.Configure<ResiliencePolicyRegistration>(name, r =>
        {
            r.Baseline = policy;
            r.Configure = configure;
        });

        return services;
    }

    /// <summary>Registers a policy under a name, configured in code rather than from a section.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The name it is resolved by.</param>
    /// <param name="configureOptions">Sets the bindable properties.</param>
    /// <param name="configure">Runs last, for the things <see cref="ResilienceOptions"/> cannot express.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddResilience(
        this IServiceCollection services,
        string name,
        Action<ResilienceOptions> configureOptions,
        Func<Resilience, Resilience>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configureOptions);

        Register(services, name);
        services.Configure(name, configureOptions);

        if (configure is not null)
        {
            services.Configure<ResiliencePolicyRegistration>(name, r => r.Configure = configure);
        }

        return services;
    }

    /// <summary>Registers a policy under a name, bound to one configuration section.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The name it is resolved by.</param>
    /// <param name="section">The section holding this policy's settings.</param>
    /// <param name="configure">Runs last, for the things JSON cannot hold.</param>
    /// <returns>The service collection.</returns>
    /// <remarks>Reloads. Editing the section swaps the policy; the live breaker and budget survive the edit.</remarks>
    public static IServiceCollection AddResilience(
        this IServiceCollection services,
        string name,
        IConfiguration section,
        Func<Resilience, Resilience>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(section);

        Register(services, name);
        services.Configure<ResilienceOptions>(name, section);

        if (configure is not null)
        {
            services.Configure<ResiliencePolicyRegistration>(name, r => r.Configure = configure);
        }

        return services;
    }

    /// <summary>
    /// Registers every child of a section as a policy, each named by its key.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="section">The parent section — one child per policy.</param>
    /// <returns>The service collection.</returns>
    /// <example>
    /// <code>
    /// services.AddResilience(configuration.GetSection("Resilience"));
    /// </code>
    /// </example>
    /// <remarks>
    /// The set of names is read at registration time, because a name that appears in the file after
    /// the container is built has nothing to be injected into. Values reload; the roster does not.
    /// </remarks>
    public static IServiceCollection AddResilience(this IServiceCollection services, IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);

        foreach (IConfigurationSection child in section.GetChildren())
        {
            services.AddResilience(child.Key, child);
        }

        // So an empty or missing section still yields a working IResiliencePolicies that reports an
        // empty roster, rather than a container that cannot resolve it at all.
        Register(services, name: null);
        return services;
    }

    /// <summary>
    /// Registers <see cref="IResiliencePolicies"/> without registering any policy. Every other
    /// overload calls it, so it is only needed to resolve the service in a container that
    /// configures its policies some other way.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddResilience(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Register(services, name: null);
        return services;
    }

    private static void Register(IServiceCollection services, string? name)
    {
        services.AddOptions();
        services.TryAddSingleton<IResiliencePolicies, ResiliencePolicies>();

        ResilienceNames names = Names(services);
        if (name is not null)
        {
            names.Set.TryAdd(name, 0);
        }
    }

    /// <summary>
    /// Finds the roster already in the collection, or puts one there.
    /// <para>
    /// Registration happens before there is a provider to resolve anything from, and the roster has
    /// to be written to at registration time — so it is a singleton *instance*, found by looking
    /// through the descriptors, which is the shape the platform's own builders use for exactly this.
    /// </para>
    /// </summary>
    private static ResilienceNames Names(IServiceCollection services)
    {
        foreach (ServiceDescriptor descriptor in services)
        {
            if (descriptor.ServiceType == typeof(ResilienceNames) && descriptor.ImplementationInstance is ResilienceNames existing)
            {
                return existing;
            }
        }

        var created = new ResilienceNames();
        services.AddSingleton(created);
        return created;
    }
}
