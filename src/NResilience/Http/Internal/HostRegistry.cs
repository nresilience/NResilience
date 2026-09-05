namespace NResilience.Internal;

/// <summary>
///     The two policies one host is served by - the retrying one, and the single-attempt one a
///     non-repeatable request gets - with that host's breaker and budget already attached.
/// </summary>
/// <remarks>
///     Derived once per host rather than per request. <c>with</c> on a record is cheap, but it is not
///     free, and the HTTP path runs it on every send otherwise.
/// </remarks>
internal sealed class HostScope : Scoped
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

            Breaker = new Breaker(settings with { Name = host });
            scoped = scoped with { Breaker = Breaker };
        }
        else
            Breaker = policy.Breaker;

        // The Automatic marker means "no deliberate scope decision was made", which is what per-host
        // scoping is allowed to override. An explicit instance, RetryBudget.None included, is not.
        if (options.BudgetPerHost && policy.Budget.IsAutomatic)
        {
            Budget = RetryBudget.Of(time: policy.Time);
            scoped = scoped with { Budget = Budget };
        }
        else
            Budget = policy.Budget;

        Retrying = scoped;

        // Hedging goes with the attempts: a hedge is a concurrent retry, so a request that may not be
        // repeated may not be hedged either, and Validate() would refuse the combination anyway.
        Single = scoped with { Attempts = 1, Hedge = null };
    }

    /// <summary>The host:port these were scoped to.</summary>
    internal string Host { get; }

    /// <summary>The policy as configured, retries and all.</summary>
    internal Resilience Retrying { get; }

    /// <summary>
    ///     The same policy with one attempt and no hedging. What a POST gets: the breaker still sees the
    ///     outcome and the budget still receives its deposit, and nothing is sent twice.
    /// </summary>
    internal Resilience Single { get; }

    /// <summary>This host's breaker, whether created here or inherited from the policy.</summary>
    internal Breaker? Breaker { get; }

    /// <summary>This host's budget, whether created here or inherited from the policy.</summary>
    internal RetryBudget? Budget { get; }
}

/// <summary>
///     Host scopes, created on first sight of a host and kept until an eviction sweep drops them.
/// </summary>
/// <remarks>
///     Bounded by <see cref="HttpResilienceOptions.MaximumHosts" />. The bound, the sweep and the
///     dictionary are <see cref="ScopeRegistry{TKey,TScope}" />'s; what is left here is the host
///     comparison - <see cref="StringComparer.OrdinalIgnoreCase" />, because a host is not case
///     sensitive and two spellings of one authority must not get two breakers.
/// </remarks>
internal sealed class HostRegistry
{
    private readonly ScopeRegistry<string, HostScope> _scopes;

    internal HostRegistry(Resilience policy, HttpResilienceOptions options)
    {
        _scopes = new ScopeRegistry<string, HostScope>(
            host => new HostScope(policy, host, options),
            options.MaximumHosts,
            StringComparer.OrdinalIgnoreCase);
    }

    internal IEnumerable<HostScope> Scopes => _scopes.Scopes;

    internal HostScope For(string host) => _scopes.For(host);
}
