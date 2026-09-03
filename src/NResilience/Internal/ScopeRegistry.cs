using System.Collections.Concurrent;

namespace NResilience.Internal;

/// <summary>
///     The base of anything a <see cref="ScopeRegistry{TKey,TScope}" /> keeps, which is only the
///     recency flag the sweep reads.
/// </summary>
internal abstract class Scoped
{
    /// <summary>
    ///     Set on use and cleared by an eviction sweep, so a key seen since the last sweep survives
    ///     the next one. A plain field rather than an interlocked counter: this is an approximation,
    ///     and a lost race under-counts a key's recency by one sweep, which is not a correctness
    ///     property.
    /// </summary>
    internal int Used;
}

/// <summary>
///     Scopes, created on first sight of a key and kept until an eviction sweep drops them.
/// </summary>
/// <remarks>
///     The read path stays a lock-free dictionary lookup plus a predicated store, and eviction is
///     second chance rather than true LRU: maintaining access order would put linked-list surgery
///     under a lock on every lookup, which is a worse trade than approximating recency.
///     <para>
///         One implementation serves both keyed things the library has: the per-host scoping the HTTP
///         handler has always done, and the <see cref="PolicyScope{TKey}" /> a caller keys by anything
///         else. The bound and the eviction are the part that gets forgotten in a hand-rolled
///         dictionary, so there is exactly one of them.
///     </para>
/// </remarks>
internal sealed class ScopeRegistry<TKey, TScope>
    where TKey : notnull
    where TScope : Scoped
{
    /// <summary>
    ///     Held in a field rather than built per lookup, so the <c>GetOrAdd</c> on the miss path
    ///     allocates no delegate and captures nothing.
    /// </summary>
    private readonly Func<TKey, TScope> _create;

    /// <summary>The cap, or zero for an unbounded registry.</summary>
    private readonly int _max;

    private readonly ConcurrentDictionary<TKey, TScope> _scopes;

    private int _sweeping;

    internal ScopeRegistry(Func<TKey, TScope> create, int max, IEqualityComparer<TKey>? comparer = null)
    {
        _create = create;
        _max = max > 0 ? max : 0;
        _scopes = comparer is null ? new ConcurrentDictionary<TKey, TScope>() : new ConcurrentDictionary<TKey, TScope>(comparer);
    }

    internal IEnumerable<TScope> Scopes => _scopes.Values;

    internal IEnumerable<KeyValuePair<TKey, TScope>> Entries => _scopes;

    internal int Count => _scopes.Count;

    internal TScope For(TKey key)
    {
        if (_scopes.TryGetValue(key, out var scope))
        {
            // Guarded so a steady-state call does not dirty a shared cache line on every lookup.
            if (scope.Used == 0)
                scope.Used = 1;

            return scope;
        }

        var created = _scopes.GetOrAdd(key, _create);

        created.Used = 1;

        if (_max > 0 && _scopes.Count > _max)
            Sweep();

        return created;
    }

    /// <summary>
    ///     Drops the keys that have not been seen since the last sweep, plus enough headroom that a
    ///     sweep runs once per batch of new keys rather than once per key past the cap.
    /// </summary>
    private void Sweep()
    {
        var count = _scopes.Count;

        // Past twice the cap the registry has stopped approximating its bound and is simply
        // growing, and the two concessions below are both withdrawn until it is back under.
        var crowded = count > _max * 2;

        // One sweeper at a time. Everyone else keeps serving calls against a registry that is
        // briefly over its cap, which is the correct trade: the cap bounds growth, it is not a hard
        // invariant worth blocking a call for.
        //
        // Deferring unconditionally is a different matter. The sweeper is an ordinary caller thread
        // holding no lock, so a loaded scheduler can leave it descheduled part-way through its
        // iteration while every other thread adds a key and declines to sweep - and then nothing
        // bounds anything. Eight threads looking up 400 keys against a cap of 32 were observed
        // keeping all 400. So a thread that arrives while the registry is crowded sweeps alongside
        // whoever is already sweeping. Concurrent sweeps need no coordination: TryRemove settles
        // which one evicts an entry, and Used is an approximation by construction.
        if (Interlocked.Exchange(ref _sweeping, 1) == 1 && !crowded)
            return;

        try
        {
            var target = count - _max + _max / 8;

            foreach (var (key, scope) in _scopes)
            {
                if (target <= 0)
                    return;

                // Second chance: seen since the last sweep, so it survives this one. Withheld while
                // crowded, because a caller whose every lookup is a key it has not seen before
                // leaves every entry warm, and a sweep that can only clear flags reclaims nothing.
                // Evicting a warm key costs it a rebuilt breaker and budget; not evicting it costs
                // the bound the cap exists to provide.
                if (scope.Used != 0 && !crowded)
                {
                    scope.Used = 0;
                    continue;
                }

                if (_scopes.TryRemove(key, out _))
                    target--;
            }
        }
        finally
        {
            Volatile.Write(ref _sweeping, 0);
        }
    }
}
