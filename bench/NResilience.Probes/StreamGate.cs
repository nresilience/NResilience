using System.Runtime.CompilerServices;

namespace NResilience.Probes;

/// <summary>
///     The suspension primitive for the streaming arms, built on the same reasoning as
///     <see cref="Gate" />: allocation comparisons are only meaningful if every arm enumerates
///     the same source the same number of times, suspends the same number of times, and pays
///     the same enumerator costs. The raw baseline is the identical <c>await foreach</c> over the
///     identical source, so the difference is the policy's iterator and nothing else.
/// </summary>
public static class StreamGate
{
    public const int Items = 3;

    public const int Value = 42;

    /// <summary>
    ///     A source that suspends before every element, so a full enumeration suspends
    ///     <see cref="Items" /> times. An element count above one keeps the passthrough loop
    ///     honest: the handover, the first yield and every further pull all run on every operation.
    /// </summary>
    public static async IAsyncEnumerable<int> SuspendAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < Items; i++)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return Value;
        }
    }

    /// <summary>
    ///     The consumer loop every streaming arm shares, fully draining its enumeration and
    ///     summing what it saw. The policy's iterator sits between this loop and
    ///     <see cref="SuspendAsync" /> on the library arm, and nowhere on the raw arm.
    /// </summary>
    public static async ValueTask<int> DrainAsync(IAsyncEnumerable<int> stream)
    {
        var sum = 0;

        await foreach (var item in stream.ConfigureAwait(false))
            sum += item;

        return sum;
    }

    /// <summary>The raw baseline: the identical consumer over the identical source, with no policy in the middle.</summary>
    public static ValueTask<int> RawSuspending() => DrainAsync(SuspendAsync());

    /// <summary>
    ///     A source whose first element is a -1, which <c>Classifier.Default.OnResult</c> style rules
    ///     call Permanent. For the AOT probe's refused-element check: the streaming path must throw
    ///     <c>CallRejectedException</c> from the first pull rather than yield an element the
    ///     classifier rejected.
    /// </summary>
    public static async IAsyncEnumerable<int> RejectedAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield return -1;
    }
}