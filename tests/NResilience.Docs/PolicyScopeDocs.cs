using NResilience.Testing;

namespace NResilience.Docs;

/// <summary>Keying a policy by something other than a host: <c>PolicyScope&lt;TKey&gt;</c>.</summary>
public sealed class PolicyScopeDocs
{
    // <snippet:policy-scope-field>
    // One scope for the process, like the breaker it holds. A scope built per call would hand every
    // call a fresh breaker and a fresh budget, which is what NRES005 says.
    private static readonly PolicyScope<string> Tenants = new(Resilience.Default with { Breaker = new Breaker() });

    // </snippet:policy-scope-field>

    [Fact]
    public async Task A_key_gets_its_own_policy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var calls = Sequence.For<int>().Returns(result: 42);
        var tenantId = "acme";

        // <snippet:policy-scope-use>
        // The policy for this tenant, with the tenant's own breaker and retry budget attached.
        var value = await Tenants.For(tenantId).RunAsync(attempt => calls.NextAsync(cancellationToken: attempt), cancellationToken: cancellationToken);

        // </snippet:policy-scope-use>

        Assert.Equal(expected: 42, actual: value);
    }

    // <snippet:policy-scope-shape>
    private static readonly PolicyScope<string> Shards = new(
        Resilience.Default with { Breaker = new Breaker() },

        // Run once per key, on first sight. The per-key breaker and budget are derived from whatever
        // it returns, so shaping a key does not cost it guards of its own.
        shape: key => Resilience.Default with
        {
            Breaker = new Breaker(),
            Attempts = key == "reporting" ? 1 : 3,
        },

        // How many keys to keep. The least-recently-seen are dropped past this.
        maximumKeys: 64);

    // </snippet:policy-scope-shape>

    [Fact]
    public void Keys_can_be_shaped_and_bounded()
    {
        Assert.Equal(expected: 1, actual: Shards.For(key: "reporting").Attempts);
        Assert.Equal(expected: 3, actual: Shards.For(key: "orders").Attempts);
    }

    [Fact]
    public void The_guards_are_reported_by_key()
    {
        Tenants.For(key: "acme");

        // <snippet:policy-scope-inspect>
        // For a health endpoint: a breaker whose scope is a key with a name is one an operator can
        // be told about.
        foreach (var (tenant, breaker) in Tenants.Breakers())
            Console.WriteLine(value: $"{tenant}: {breaker.State}");
        // </snippet:policy-scope-inspect>

        Assert.Contains(expected: "acme", collection: Tenants.Breakers().Keys);
    }
}
