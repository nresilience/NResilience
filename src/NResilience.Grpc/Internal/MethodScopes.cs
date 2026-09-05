using System.Runtime.CompilerServices;
using Grpc.Core;

namespace NResilience.Grpc.Internal;

/// <summary>
///     The policies one interceptor serves its methods with: the retrying one, the single-attempt
///     one a non-repeatable method gets, and the breaker and budget behind both.
/// </summary>
/// <remarks>
///     Two shapes behind one seam. With <see cref="GrpcResilienceOptions.ScopeBy" /> set - per
///     service by default - this is a <see cref="PolicyScope{TKey}" /> keyed by whatever the
///     delegate returns, which is where the bound and the eviction already live. With it null it is
///     one policy, and the keyed machinery is never built at all.
///     <para>
///         The single-attempt variant is cached rather than derived per call. <c>with</c> on a record
///         is cheap, but a fresh policy instance is a fresh entry in the executor's state table and a
///         fresh automatic retry budget - which is to say a budget that never accumulates, the exact
///         failure NRES005 exists to catch. The cache is a
///         <see cref="ConditionalWeakTable{TKey,TValue}" /> so that an evicted key's policies become
///         collectable together, without a second bound to get wrong.
///     </para>
/// </remarks>
internal sealed class MethodScopes
{
    /// <summary>
    ///     Hedging goes with the attempts: a hedge is a concurrent retry, so a method that may not be
    ///     repeated may not be hedged either.
    /// </summary>
    private static readonly ConditionalWeakTable<Resilience, Resilience>.CreateValueCallback ToSingleAttempt =
        static policy => policy with { Attempts = 1, Hedge = null };

    private readonly PolicyScope<string>? _keyed;

    /// <summary>The one scope, when <see cref="_scopeBy" /> is null. Null otherwise.</summary>
    private readonly Resilience? _one;

    private readonly Breaker? _oneBreaker;
    private readonly RetryBudget? _oneBudget;

    /// <summary>What the one scope is reported under. The client's name, where a registration made it.</summary>
    private readonly string _oneKey;

    private readonly Func<IMethod, string>? _scopeBy;
    private readonly ConditionalWeakTable<Resilience, Resilience> _singles = new();

    internal MethodScopes(Resilience policy, GrpcResilienceOptions options, string scopeName)
    {
        _scopeBy = options.ScopeBy;
        _oneKey = scopeName;

        // The breaker is built here rather than left to the policy, for the same reason the HTTP
        // handler builds one per host: a breaker is the thing a caller most often forgets, and an
        // integration that silently ships without one is worse than one that ships with a default.
        // A breaker already on the policy is a deliberate scope decision and is never overruled.
        var template = policy;

        if (options.BreakerPerScope && policy.Breaker is null)
        {
            var settings = options.BreakerSettings ?? new BreakerSettings { Time = policy.Time };
            template = policy with { Breaker = new Breaker(settings) };
        }

        if (_scopeBy is not null)
        {
            // The template's breaker is a prototype: PolicyScope gives each key one of its own with
            // those settings, which is the whole point of keying.
            _keyed = new PolicyScope<string>(template, maximumKeys: options.MaxScopes, comparer: StringComparer.Ordinal);
            return;
        }

        // One scope. The same two decisions PolicyScope makes per key, made once: the breaker is
        // whatever the template ended up with, and an automatic budget becomes a real instance so
        // that a health endpoint has something to report.
        var scoped = template;
        _oneBreaker = template.Breaker;

        if (template.Budget.IsAutomatic)
        {
            _oneBudget = RetryBudget.Of(time: template.Time);
            scoped = scoped with { Budget = _oneBudget };
        }
        else
            _oneBudget = template.Budget.IsNone ? null : template.Budget;

        if (scoped.Name is null)
            scoped = scoped with { Name = scopeName };

        _one = scoped;
    }

    /// <summary>The retrying policy for a method, with that scope's breaker and budget attached.</summary>
    internal Resilience Retrying(IMethod method) =>
        _scopeBy is null ? _one! : _keyed!.For(_scopeBy(method));

    /// <summary>
    ///     The same policy with one attempt and no hedging. What a method the caller marked
    ///     non-repeatable gets: the breaker still sees the outcome and the budget still receives its
    ///     deposit, and nothing is sent twice.
    /// </summary>
    internal Resilience Single(Resilience retrying) => _singles.GetValue(retrying, ToSingleAttempt);

    /// <summary>The breakers, by scope key, for the scopes this interceptor currently holds.</summary>
    internal IReadOnlyDictionary<string, Breaker> Breakers()
    {
        if (_keyed is not null)
            return _keyed.Breakers();

        return _oneBreaker is null
            ? new Dictionary<string, Breaker>()
            : new Dictionary<string, Breaker>(StringComparer.Ordinal) { [_oneKey] = _oneBreaker };
    }

    /// <summary>The retry budgets, by scope key, for the scopes this interceptor currently holds.</summary>
    internal IReadOnlyDictionary<string, RetryBudget> Budgets()
    {
        if (_keyed is not null)
            return _keyed.Budgets();

        return _oneBudget is null
            ? new Dictionary<string, RetryBudget>()
            : new Dictionary<string, RetryBudget>(StringComparer.Ordinal) { [_oneKey] = _oneBudget };
    }
}
