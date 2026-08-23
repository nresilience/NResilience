using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace NResilience.Extensions.Internal;

/// <summary>
/// Which handlers each client has already registered, so the one ordering that matters can be
/// checked rather than hoped for.
/// <para>
/// <c>IHttpClientFactory</c> runs handlers in registration order, outermost first, and a rate
/// limiter has to be <b>inner</b> to the resilience handler: that is what makes it acquire one
/// permit per attempt instead of one per operation. Registered the other way round every retry
/// bypasses the quota, and nothing about the resulting behavior looks wrong until a dependency
/// starts returning 429s under load - which is exactly the class of silent misconfiguration a
/// registration API should refuse rather than accept.
/// </para>
/// </summary>
/// <remarks>
/// A singleton instance found by scanning the descriptors, for the reason
/// <c>ResilienceNames</c> is: registration happens before there is a provider to resolve anything
/// from, and this has to be written to at registration time.
/// </remarks>
internal sealed class HandlerOrder
{
    /// <summary>The names of the clients that have a resilience handler.</summary>
    public ConcurrentDictionary<string, byte> ResilienceClients { get; } = new(StringComparer.Ordinal);

    /// <summary>The names of the clients that have a rate limit handler.</summary>
    public ConcurrentDictionary<string, byte> RateLimitClients { get; } = new(StringComparer.Ordinal);

    /// <summary>Finds the record already in the collection, or puts one there.</summary>
    public static HandlerOrder For(IServiceCollection services)
    {
        foreach (ServiceDescriptor descriptor in services)
        {
            if (descriptor.ServiceType == typeof(HandlerOrder) && descriptor.ImplementationInstance is HandlerOrder existing)
            {
                return existing;
            }
        }

        var created = new HandlerOrder();
        services.AddSingleton(created);
        return created;
    }
}
