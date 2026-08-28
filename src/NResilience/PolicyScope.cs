using NResilience.Internal;

namespace NResilience;

/// <summary>
///     One policy per key, each with its own breaker, retry budget and hedging latency estimate, and
///     the whole thing bounded so an unbounded set of keys cannot leak.
///     <para>
///         This is what the HTTP handler has always done per host, available for everything else a
///         caller keys by: a database, a queue, a gRPC channel, a tenant partition. The alternative is
///         one breaker shared across every key - which is the blast radius per-host scoping exists to
///         remove - or a hand-rolled dictionary, which is where the bound and the eviction get
///         forgotten.
///     </para>
/// </summary>
/// <typeparam name="TKey">What the policies are keyed by. A tenant id, a shard name, a channel.</typeparam>
/// <example>
///     <code>
/// static readonly PolicyScope&lt;string&gt; Tenants = new(Resilience.Default with { Breaker = new Breaker() });
///
/// await Tenants.For(tenantId).RunAsync(ct =&gt; Work(ct), cancellationToken);
/// </code>
/// </example>
/// <remarks>
///     Hold one in a <c>static readonly</c> field or a container singleton. A scope created per call
///     is the same bug as a breaker created per call, one level up, and NRES005 says so.
///     <para>
///         Every member is safe to call from any thread. Lookups are a dictionary read; a key is
///         derived once, on first sight.
///     </para>
/// </remarks>
public sealed class PolicyScope<TKey>
    where TKey : notnull
{
    private readonly ScopeRegistry<TKey, KeyedScope> _scopes;

    /// <summary>Creates a scope over a template policy.</summary>
    /// <param name="template">
    ///     The policy every key starts from. Its <see cref="Resilience.Breaker" /> is a prototype
    ///     rather than a shared instance - see the remarks - and its
    ///     <see cref="Resilience.Budget" /> is per key unless it names a shared one.
    /// </param>
    /// <param name="shape">
    ///     Optional per-key shaping, run once per key on first sight: the key's policy is
    ///     <c>shape(key)</c> if given, and <paramref name="template" /> otherwise. Use it where one key
    ///     needs a different deadline or attempt count; the per-key guards are derived from whatever it
    ///     returns, so shaping a policy does not cost the key its own breaker and budget.
    /// </param>
    /// <param name="maxKeys">
    ///     How many keys to keep. The least-recently-seen are dropped past this, approximately - the
    ///     same second-chance eviction <see cref="Http.HttpResilienceOptions.MaxHosts" /> uses.
    /// </param>
    /// <param name="comparer">How keys are compared. Defaults to <see cref="EqualityComparer{T}.Default" />.</param>
    /// <exception cref="ArgumentNullException"><paramref name="template" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxKeys" /> is not positive.</exception>
    /// <exception cref="ResilienceConfigurationException">The template cannot be executed.</exception>
    /// <remarks>
    ///     There is no unbounded mode. Unbounded keying is a memory leak with a breaker and a budget
    ///     attached to every entry, and it is the failure this type exists to prevent - a caller who
    ///     genuinely wants one policy for everything wants a policy, not a scope.
    ///     <para>
    ///         A breaker on the template - or on what <paramref name="shape" /> returns - is a
    ///         <i>prototype</i>: each key gets a breaker of its own with those settings, and the
    ///         template's own instance is never executed against. Sharing one breaker's state across
    ///         every key would defeat the point of keying, so it is not offered here; a policy with a
    ///         breaker on it already does exactly that.
    ///     </para>
    ///     <para>
    ///         A budget is treated the way the HTTP handler treats it: <c>null</c> or
    ///         <see cref="RetryBudget.Automatic" /> means "no scope decision was made" and the key gets
    ///         its own, while an explicit instance - <see cref="RetryBudget.Shared(string, double, int)" />
    ///         in particular - is a deliberate decision and is left alone. A retry ceiling shared across
    ///         keys is a real thing to want, and <c>Shared</c> is how you say it.
    ///     </para>
    ///     <para>
    ///         The hedging latency estimate needs no arrangement here: it is keyed by policy instance,
    ///         and every key has its own. A slow tenant does not lower the hedge threshold for a fast
    ///         one.
    ///     </para>
    /// </remarks>
    public PolicyScope(Resilience template, Func<TKey, Resilience>? shape = null, int maxKeys = 1024, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (maxKeys < 1)
            throw new ArgumentOutOfRangeException(nameof(maxKeys), maxKeys, "maxKeys must be at least 1; a scope is bounded by construction.");

        // Eagerly, so a bad template throws where it is written rather than on the first call against
        // whichever key happened to arrive first. The shaped policies are validated on their own first
        // execution, like any other policy.
        template.Validate();

        Template = template;
        MaxKeys = maxKeys;

        _scopes = new ScopeRegistry<TKey, KeyedScope>(key => new KeyedScope(shape is null ? template : shape(key), key), maxKeys, comparer);
    }

    /// <summary>The policy every key starts from, as handed in.</summary>
    public Resilience Template { get; }

    /// <summary>How many keys this scope keeps.</summary>
    public int MaxKeys { get; }

    /// <summary>How many keys it is currently holding.</summary>
    /// <remarks>
    ///     Approximate under concurrency, and can briefly exceed <see cref="MaxKeys" /> while a sweep
    ///     catches up: the cap bounds growth rather than pinning the count, because no lookup ever
    ///     waits on a sweep.
    /// </remarks>
    public int Count => _scopes.Count;

    /// <summary>The policy for one key, derived on first sight and cached.</summary>
    /// <param name="key">The key.</param>
    /// <returns>That key's policy, with its own breaker and budget already attached.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key" /> is null.</exception>
    /// <remarks>
    ///     A key that has been evicted and returns gets a fresh policy, which means a fresh breaker:
    ///     eviction discards state, and a dropped key does not remember that its breaker was open. Size
    ///     <c>maxKeys</c> above the number of keys you expect to be active at once.
    /// </remarks>
    public Resilience For(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return _scopes.For(key).Policy;
    }

    /// <summary>The breakers, by key, for the keys this scope currently holds.</summary>
    /// <returns>
    ///     A snapshot. Empty when the template carries no breaker, since there is then nothing to
    ///     scope.
    /// </returns>
    /// <remarks>For a health endpoint: a breaker whose scope is a key with a name is one an operator can be told about.</remarks>
    public IReadOnlyDictionary<TKey, Breaker> Breakers() => ByKey(static scope => scope.Breaker);

    /// <summary>The retry budgets, by key, for the keys this scope currently holds.</summary>
    /// <returns>A snapshot.</returns>
    public IReadOnlyDictionary<TKey, RetryBudget> Budgets() => ByKey(static scope => scope.Budget);

    /// <summary>One guard per key that has one, as a snapshot.</summary>
    private Dictionary<TKey, T> ByKey<T>(Func<KeyedScope, T?> guard)
        where T : class
    {
        var found = new Dictionary<TKey, T>();

        foreach (var (key, scope) in _scopes.Entries)
        {
            if (guard(scope) is { } value)
                found[key] = value;
        }

        return found;
    }

    /// <summary>
    ///     One key's policy, with the guards it was given so they can be handed back by
    ///     <see cref="Breakers" /> and <see cref="Budgets" /> without re-reading the policy.
    /// </summary>
    private sealed class KeyedScope : Scoped
    {
        internal KeyedScope(Resilience policy, TKey key)
        {
            var name = key.ToString();

            var scoped = policy with { Name = policy.Name is null ? name : $"{policy.Name}:{name}" };

            if (policy.Breaker is { } prototype)
            {
                // Built here rather than handed in, so it takes the policy's clock unless the settings
                // named one. Without that, a fake clock on the policy could not drive the per-key
                // breakers it created.
                var settings = prototype.Settings;

                if (settings.ConfiguredTime is null)
                    settings = settings with { Time = policy.Time };

                Breaker = new Breaker(settings) { Name = name };
                scoped = scoped with { Breaker = Breaker };
            }

            if (policy.Budget is null or { IsAutomatic: true })
            {
                Budget = RetryBudget.Of(time: policy.Time);
                scoped = scoped with { Budget = Budget };
            }
            else
                Budget = policy.Budget.IsNone ? null : policy.Budget;

            Policy = scoped;
        }

        internal Resilience Policy { get; }

        internal Breaker? Breaker { get; }

        internal RetryBudget? Budget { get; }
    }
}
