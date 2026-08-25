using System.Collections.Concurrent;

namespace NResilience.Http.Internal;

/// <summary>
///     The two policies one host is served by - the retrying one, and the single-attempt one a
///     non-repeatable request gets - with that host's breaker and budget already attached.
/// </summary>
/// <remarks>
///     Derived once per host rather than per request. <c>with</c> on a record is cheap, but it is not
///     free, and the HTTP path runs it on every send otherwise.
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
            // The breaker is built here rather than handed in, so it takes the policy's clock unless
            // the settings named one. Without that, a fake clock on the policy could not drive the
            // per-host breakers it created.
            var settings = options.BreakerSettings ?? new BreakerSettings();

            if (settings.ConfiguredTime is null)
                settings = settings with { Time = policy.Time };

            Breaker = new Breaker(settings) { Name = host };
            scoped = scoped with { Breaker = Breaker };
        }
        else
            Breaker = policy.Breaker;

        // A null budget and the Automatic marker both mean "no deliberate scope decision was made",
        // which is what per-host scoping is allowed to override. An explicit instance is not.
        if (options.BudgetPerHost && policy.Budget is null or { IsAutomatic: true })
        {
            Budget = RetryBudget.Of(time: policy.Time);
            scoped = scoped with { Budget = Budget };
        }
        else
            Budget = policy.Budget;

        Retrying = scoped;
        Single = scoped with { Attempts = 1 };
    }

    /// <summary>The host:port these were scoped to.</summary>
    internal string Host { get; }

    /// <summary>The policy as configured, retries and all.</summary>
    internal Resilience Retrying { get; }

    /// <summary>
    ///     The same policy with one attempt. What a POST gets: the breaker still sees the outcome and
    ///     the budget still receives its deposit, and nothing is sent twice.
    /// </summary>
    internal Resilience Single { get; }

    /// <summary>This host's breaker, whether created here or inherited from the policy.</summary>
    internal Breaker? Breaker { get; }

    /// <summary>This host's budget, whether created here or inherited from the policy.</summary>
    internal RetryBudget? Budget { get; }

    /// <summary>
    ///     Set on use and cleared by an eviction sweep, so a host seen since the last sweep survives
    ///     the next one. A plain field rather than an interlocked counter: this is an approximation,
    ///     and a lost race under-counts a host's recency by one sweep, which is not a correctness
    ///     property.
    /// </summary>
    internal int Used;
}

/// <summary>
///     Host scopes, created on first sight of a host and kept until an eviction sweep drops them.
/// </summary>
/// <remarks>
///     Bounded by <see cref="HttpResilienceOptions.MaxHosts" />. The read path stays a lock-free
///     dictionary lookup plus a predicated store, and eviction is second chance rather than true LRU:
///     maintaining access order would put linked-list surgery under a lock on every HTTP send, which
///     is a worse trade than approximating recency.
/// </remarks>
internal sealed class HostRegistry(Resilience policy, HttpResilienceOptions options)
{
    private readonly ConcurrentDictionary<string, HostScope> _scopes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The cap, or zero for an unbounded registry.</summary>
    private readonly int _max = options.MaxHosts is > 0 ? options.MaxHosts.Value : 0;

    private int _sweeping;

    internal IEnumerable<HostScope> Scopes => _scopes.Values;

    internal HostScope For(string host)
    {
        if (_scopes.TryGetValue(host, out var scope))
        {
            // Guarded so a steady-state request does not dirty a shared cache line on every send.
            if (scope.Used == 0)
                scope.Used = 1;

            return scope;
        }

        var created = _scopes.GetOrAdd(host, static (key, state) => new HostScope(state.Policy, key, state.Options), (Policy: policy, Options: options));

        created.Used = 1;

        if (_max > 0 && _scopes.Count > _max)
            Sweep();

        return created;
    }

    /// <summary>
    ///     Drops the hosts that have not been seen since the last sweep, plus enough headroom that a
    ///     sweep runs once per batch of new hosts rather than once per host past the cap.
    /// </summary>
    private void Sweep()
    {
        // One sweeper at a time. Everyone else keeps serving requests against a registry that is
        // briefly over its cap, which is the correct trade: the cap bounds growth, it is not a hard
        // invariant worth blocking a request for.
        if (Interlocked.Exchange(ref _sweeping, 1) == 1)
            return;

        try
        {
            var target = _scopes.Count - _max + (_max / 8);

            foreach (var (host, scope) in _scopes)
            {
                if (target <= 0)
                    return;

                if (scope.Used != 0)
                {
                    scope.Used = 0; // Second chance: seen since the last sweep, so it survives this one.
                    continue;
                }

                if (_scopes.TryRemove(host, out _))
                    target--;
            }
        }
        finally
        {
            Volatile.Write(ref _sweeping, 0);
        }
    }
}
