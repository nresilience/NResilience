namespace NResilience.Extensions;

/// <summary>
/// The registered policies, by name. Inject this rather than a <see cref="Resilience"/>, and
/// resolve the policy on every call.
/// <para>
/// A policy is an immutable value, so hot reload is a reference swap: <c>IOptionsMonitor</c> fires,
/// the configuration is projected onto a new <see cref="Resilience"/>, and this hands out the new
/// one. There is no in-flight execution to drain and no pipeline to rebuild.
/// </para>
/// </summary>
/// <example>
/// <code>
/// public class MyClient(IResiliencePolicies policies)
/// {
///     public async Task&lt;User&gt; GetAsync(CancellationToken ct) =>
///         await policies["api"].RunAsync(ct2 => FetchAsync(ct2), ct);
/// }
/// </code>
/// </example>
/// <remarks>
/// Two consequences of the swap, both of which are the design working rather than a caveat:
/// <list type="number">
/// <item><description>
/// <b>Resolve per call, not into a <c>readonly</c> field.</b> A policy captured at construction
/// time is a snapshot, and the swap will never reach it. The indexer is a dictionary lookup.
/// </description></item>
/// <item><description>
/// <b>Live breakers and budgets are not replaced on reload</b>, because their state is the point.
/// A breaker that reopened because a dependency is down stays open across a configuration edit; the
/// reloaded policy is handed the breaker the old one was using.
/// </description></item>
/// </list>
/// </remarks>
public interface IResiliencePolicies
{
    /// <summary>The policy registered under a name.</summary>
    /// <param name="name">The registration name.</param>
    /// <returns>The current policy for that name.</returns>
    /// <exception cref="ResilienceConfigurationException">Nothing is registered under that name. The message lists what is.</exception>
    Resilience this[string name] { get; }

    /// <summary>Every registered name.</summary>
    IReadOnlyCollection<string> Names { get; }

    /// <summary>The policy registered under a name, without throwing.</summary>
    /// <param name="name">The registration name.</param>
    /// <param name="policy">The policy, or <see cref="Resilience.Default"/> when there is none.</param>
    /// <returns>Whether a policy is registered under that name.</returns>
    bool TryGet(string name, out Resilience policy);
}
