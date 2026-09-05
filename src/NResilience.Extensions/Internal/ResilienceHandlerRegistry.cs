using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace NResilience.Extensions.Internal;

/// <summary>
///     The resilience handler currently serving each named client, so something outside the pipeline
///     can read the per-host breakers and budgets it holds. The health check is the only consumer.
///     <para>
///         One entry per client name, holding the newest handler: <c>IHttpClientFactory</c> builds a
///         fresh chain when the handler lifetime expires - two minutes by default - and the newest is
///         the one guarding traffic now.
///     </para>
/// </summary>
/// <remarks>
///     The reference is weak, so tracking a handler never keeps a chain the factory has dropped alive.
///     A rotation therefore has two visible consequences and both are the factory's rather than this
///     registry's: the previous generation becomes collectable, and the per-host breakers it had
///     accumulated go with it. A health check reports the current generation, which is the only one
///     whose state affects the next request.
/// </remarks>
internal sealed class ResilienceHandlerRegistry
{
    private readonly ConcurrentDictionary<string, WeakReference<HttpResilienceHandler>> _handlers = new(StringComparer.Ordinal);

    /// <summary>Records the handler now serving a client, replacing the generation before it.</summary>
    public void Track(string client, HttpResilienceHandler handler) =>
        _handlers[client] = new WeakReference<HttpResilienceHandler>(handler);

    /// <summary>
    ///     Every client whose handler is still alive, pruning the ones that are not.
    ///     <para>
    ///         Removing during enumeration is safe on a <see cref="ConcurrentDictionary{TKey,TValue}" />,
    ///         and the sweep costs nothing because it happens on the health check's own path rather than
    ///         on a request's.
    ///     </para>
    /// </summary>
    public IEnumerable<KeyValuePair<string, HttpResilienceHandler>> Live()
    {
        foreach (var entry in _handlers)
        {
            if (entry.Value.TryGetTarget(out var handler))
                yield return new KeyValuePair<string, HttpResilienceHandler>(entry.Key, handler);
            else
                _handlers.TryRemove(entry.Key, out _);
        }
    }

    /// <summary>
    ///     Finds the registry already in the collection, or puts one there. The same shape
    ///     <c>HandlerOrder</c> and <c>ResilienceNames</c> use, and for the same reason: registration
    ///     happens before there is a provider to resolve anything from.
    /// </summary>
    public static ResilienceHandlerRegistry For(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(ResilienceHandlerRegistry) && descriptor.ImplementationInstance is ResilienceHandlerRegistry existing)
                return existing;
        }

        var created = new ResilienceHandlerRegistry();
        services.AddSingleton(created);
        return created;
    }
}
