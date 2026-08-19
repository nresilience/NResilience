using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using NResilience.Probes;
using Xunit;

namespace NResilience.Gates;

/// <summary>
/// The cancellation facts the timeout design is built on, re-run on the exact target TFMs.
///
/// These are gated rather than merely reported because the arrangement they justify — pool the
/// timer source, link per attempt, never hand the pooled token to user code, and fall back to
/// per-call construction under a custom <see cref="TimeProvider"/> — is not obvious from reading
/// the BCL, and would silently degrade to no benefit at all if any of them changed.
/// </summary>
[Collection(BaselineCollection.Name)]
public sealed class CancellationFactsTests
{
    private readonly BaselineFixture _baseline;
    private readonly ITestOutputHelper _output;

    public CancellationFactsTests(BaselineFixture baseline, ITestOutputHelper output)
    {
        _baseline = baseline;
        _output = output;
    }

    [Fact]
    public void A_bare_source_costs_one_small_object()
        => AssertAtMost(Baseline.NewSource, Budgets.NewSource);

    [Fact]
    public void Arming_a_timer_on_a_source_costs_a_timer()
        => AssertAtMost(Baseline.NewSourceCancelAfter, Budgets.NewSourceWithCancelAfter);

    [Fact]
    public void Linking_a_non_cancellable_token_short_circuits()
        => AssertAtMost(Baseline.LinkedNone, Budgets.LinkedFromNone);

    /// <summary>
    /// The 0 B figure the pooling story rests on. It applies to the timer source only — the
    /// per-attempt linked child is a separate, non-zero cost, which is why the executor's
    /// suspending budget is not zero.
    /// </summary>
    [Fact]
    public void A_pooled_timer_source_is_free_to_reuse()
        => AssertAtMost(Baseline.PooledSource, Budgets.PooledSourceReuse);

    [Fact]
    public void Linking_a_cancellable_token_costs_materially_more_than_linking_none()
    {
        double cancellable = _baseline.CancellationBytes(Baseline.LinkedCancellable);
        double none = _baseline.CancellationBytes(Baseline.LinkedNone);

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"CreateLinked: cancellable {cancellable:0.0} B, None {none:0.0} B"));

        Assert.True(cancellable > none * 2, "The short-circuit for a non-cancellable caller token no longer pays for itself.");
    }

    /// <summary>
    /// The interaction that makes CTS pooling and injectable-<see cref="TimeProvider"/>
    /// testability mutually exclusive: the runtime type-tests <c>_timer is TimerQueueTimer</c>,
    /// and a custom provider's <c>ITimer</c> is not one.
    /// </summary>
    [Fact]
    public void TryReset_fails_on_a_source_built_with_a_custom_TimeProvider()
    {
        Assert.False(CtsFacts.TryResetWithCustomProvider(new FakeTimeProvider()));
        Assert.True(CtsFacts.TryResetWithSystemProvider());
    }

    /// <summary>A source that actually fired is poison and must be discarded rather than pooled.</summary>
    [Fact]
    public void TryReset_fails_on_a_source_that_has_already_cancelled()
        => Assert.False(CtsFacts.TryResetAfterCancellation());

    /// <summary>
    /// The other half of the resolution: <c>CancelAfter()</c> does drive an injected provider's
    /// timer, so virtual time still cancels an attempt in tests even though the source is
    /// constructed per call rather than pooled.
    /// </summary>
    [Fact]
    public void CancelAfter_honours_an_injected_TimeProvider()
    {
        var time = new FakeTimeProvider();
        using CancellationTokenSource cts = CtsFacts.CancelAfterOnProvider(time, TimeSpan.FromSeconds(5));

        Assert.False(cts.IsCancellationRequested);

        time.Advance(TimeSpan.FromSeconds(4));
        Assert.False(cts.IsCancellationRequested);

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(cts.IsCancellationRequested);
    }

    /// <summary>
    /// The executor honours the pooling/testability split: the system provider takes the pooled
    /// path, anything else does not. Getting this backwards would mean either a broken test clock
    /// or a pool that silently never hits.
    /// </summary>
    [Fact]
    public void The_pool_is_used_for_the_system_provider_and_not_for_any_other()
    {
        Assert.True(FusedPolicy.Default.Time == TimeProvider.System);
        Assert.False(CtsFacts.TryResetWithCustomProvider(new FakeTimeProvider()));
    }

    private void AssertAtMost(string arm, double budget)
    {
        double actual = _baseline.CancellationBytes(arm);

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{arm}: {actual:0.0} B/op; budget {budget:0} B"));

        Assert.True(
            actual <= budget,
            string.Create(CultureInfo.InvariantCulture, $"'{arm}' now allocates {actual:0.0} B/op against a budget of {budget:0} B/op."));
    }
}
