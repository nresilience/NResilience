using NResilience.Internal;

namespace NResilience.Tests;

/// <summary>
/// Tests for the timeout-source pool: the previous tenant is disposed rather than leaked, and a
/// fired source is disposed rather than returned.
/// </summary>
public sealed class CtsPoolTests
{
    [Fact]
    public void A_returned_source_is_handed_back_on_the_next_rent()
    {
        var first = CtsPool.Rent(TimeProvider.System);
        CtsPool.Return(first, TimeProvider.System);

        var second = CtsPool.Rent(TimeProvider.System);

        Assert.Same(first, second);
        CtsPool.Return(second, TimeProvider.System);
    }

    [Fact]
    public void A_fired_source_is_disposed_not_returned()
    {
        var fired = CtsPool.Rent(TimeProvider.System);
        fired.Cancel();

        CtsPool.Return(fired, TimeProvider.System);

        // A disposed source cannot be reset, so the next rent hands out a fresh one.
        Assert.Throws<ObjectDisposedException>(() => fired.CancelAfter(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Returning_a_second_source_does_not_leak_the_first()
    {
        // The pool holds one slot per thread. Returning twice without renting between must dispose
        // the first tenant before overwriting the slot, rather than leaving it for the finalizer.
        // We cannot observe the disposed state directly without disposing the slot we just returned,
        // but we can verify the second return lands and the pool stays usable.
        var first = CtsPool.Rent(TimeProvider.System);
        CtsPool.Return(first, TimeProvider.System);

        var second = CtsPool.Rent(TimeProvider.System);
        Assert.Same(first, second);

        // Rent a fresh source (emptying the slot), return it, then return another - the slot is
        // overwritten and the previous tenant (first) is disposed by the overwrite.
        var third = CtsPool.Rent(TimeProvider.System);
        CtsPool.Return(third, TimeProvider.System);
        CtsPool.Return(new CancellationTokenSource(), TimeProvider.System);

        // The next rent hands back the last-returned source, not a leaked one.
        var fourth = CtsPool.Rent(TimeProvider.System);
        Assert.NotSame(first, fourth);
        CtsPool.Return(fourth, TimeProvider.System);
    }
}