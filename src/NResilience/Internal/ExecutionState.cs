using System.Runtime.CompilerServices;

namespace NResilience.Internal;

/// <summary>
/// Per-policy-instance runtime state that must not be visible to the record's equality.
/// <para>
/// A record's synthesized <c>Equals</c> and <c>GetHashCode</c> compare every instance field, so a
/// private "already validated" flag would make two identically-configured policies stop being
/// equal — and change hash code — as a side effect of one of them having executed, silently
/// corrupting any <c>Dictionary&lt;Resilience, …&gt;</c>. This table is keyed by reference
/// identity, which the record's equality cannot see.
/// </para>
/// <para>
/// Phase 2's automatic per-policy retry budget lives here too, for the same reason.
/// </para>
/// </summary>
internal sealed class ExecutionState
{
    private static readonly ConditionalWeakTable<Resilience, ExecutionState> Table = new();

    private static readonly ConditionalWeakTable<Resilience, ExecutionState>.CreateValueCallback Create =
        static policy =>
        {
            policy.Validate();
            return new ExecutionState();
        };

    /// <summary>
    /// The last policy this thread validated. Steady state is one reference comparison; the table
    /// is only consulted when a call site changes policy, and only the first such call validates.
    /// </summary>
    [ThreadStatic]
    private static Resilience? t_lastValidated;

    public static void EnsureValidated(Resilience policy)
    {
        if (ReferenceEquals(t_lastValidated, policy))
        {
            return;
        }

        Table.GetValue(policy, Create);
        t_lastValidated = policy;
    }
}
