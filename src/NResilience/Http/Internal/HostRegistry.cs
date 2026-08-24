using System.Collections.Concurrent;

namespace NResilience.Http.Internal;

/// <summary>
/// The two policies one host is served by - the retrying one, and the single-attempt one a
/// non-repeatable request gets - with that host's breaker and budget already attached.
/// </summary>
/// <remarks>
/// Derived once per host rather than per request. <c>with</c> on a record is cheap, but it is not
/// free, and the HTTP path runs it on every send otherwise.
/// </remarks>
internal sealed class HostScope
{
    internal HostScope(Resilience policy, string host, HttpResilienceOptions options)
    {
        Host = host;

        var scoped = policy with { Name = policy.Name is null ? host : $"{policy.Name}:{host}" };

        // An explicit breaker or budget on the policy is a deliberate scope decision - one breaker
        // shared across several hosts is a legitimate thing to want - and per-host scoping is a
        // default rather than an override.
        if (options.BreakerPerHost && policy.Breaker is null)
        {
            Breaker = new Breaker(options.BreakerSettings) { Name = host };
            scoped = scoped with { Breaker = Breaker };
        }
        else
        {
            Breaker = policy.Breaker;
        }

        if (options.BudgetPerHost && policy.Budget is null)
        {
            Budget = RetryBudget.Of();
            scoped = scoped with { Budget = Budget };
        }
        else
        {
            Budget = policy.Budget;
        }

        Retrying = scoped;
        Single = scoped with { Attempts = 1 };
    }

    /// <summary>The host:port these were scoped to.</summary>
    internal string Host { get; }

    /// <summary>The policy as configured, retries and all.</summary>
    internal Resilience Retrying { get; }

    /// <summary>
    /// The same policy with one attempt. What a POST gets: the breaker still sees the outcome and
    /// the budget still receives its deposit, and nothing is sent twice.
    /// </summary>
    internal Resilience Single { get; }

    /// <summary>This host's breaker, whether created here or inherited from the policy.</summary>
    internal Breaker? Breaker { get; }

    /// <summary>This host's budget, whether created here or inherited from the policy.</summary>
    internal RetryBudget? Budget { get; }
}

/// <summary>
/// Host scopes, created on first sight of a host and kept for the handler's lifetime.
/// </summary>
/// <remarks>
/// Unbounded, and deliberately so: the set of hosts one <see cref="HttpClient"/> talks to is a
/// property of the application rather than of its traffic, and an eviction policy on a dictionary
/// of a dozen entries would be a cache with a bug in it. A client that talks to an unbounded set of
/// hosts wants <see cref="HttpResilienceOptions.BreakerPerHost"/> off, and the docs say so.
/// </remarks>
internal sealed class HostRegistry(Resilience policy, HttpResilienceOptions options)
{
    private readonly ConcurrentDictionary<string, HostScope> _scopes = new(StringComparer.OrdinalIgnoreCase);

    internal HostScope For(string host) =>
        _scopes.TryGetValue(host, out var scope)
            ? scope
            : _scopes.GetOrAdd(host, static (key, state) => new HostScope(state.Policy, key, state.Options), (Policy: policy, Options: options));

    internal IEnumerable<HostScope> Scopes => _scopes.Values;
}
