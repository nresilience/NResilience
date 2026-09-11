namespace NResilience.Testing;

/// <summary>
///     One call in a <see cref="Topology" />: who calls, and what they call.
///     <para>
///         The unit a policy belongs to. A service does not have one policy, it has one per thing it
///         calls - a checkout retries its payment provider on different terms from its catalog - and
///         the numbers worth reading are per edge too: the load multiplier on
///         <c>payments -&gt; bank</c> is the number the bank's owner asks for.
///     </para>
/// </summary>
/// <param name="caller">The service making the call.</param>
/// <param name="callee">The service or leaf being called.</param>
/// <seealso cref="TopologyReport.On" />
public readonly struct Edge(string caller, string callee)
{
    /// <summary>The service making the call.</summary>
    public string Caller { get; } = caller;

    /// <summary>The service or leaf being called.</summary>
    public string Callee { get; } = callee;

    /// <summary>The edge as <c>caller -&gt; callee</c>.</summary>
    /// <returns>The edge.</returns>
    public override string ToString() => $"{Caller} -> {Callee}";
}
