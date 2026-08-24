using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.IntegrationTests;

/// <summary>
///     Work that outlives its attempt timeout - the real-IO version of
///     <c>TelemetryTests.Work_that_outlives_its_attempt_timeout_raises_OrphanedWork</c>.
///     <para>
///         The behavioural suite tests this with a <c>TaskCompletionSource</c> that never observes its
///         token. The production scenario is a callback that ignores its cancellation token past the
///         attempt timeout - a real HTTP call that does this cannot be tested over a real socket,
///         because <c>SocketsHttpHandler</c> observes the token and tears the connection down, which is
///         the correct behavior. The real-IO scenario that reproduces the orphan is a non-HTTP callback
///         that holds a resource open past the timeout, which is what these tests use.
///     </para>
/// </summary>
public sealed class OrphanedWorkTests
{
    /// <summary>
    ///     An attempt that ignores its token past the timeout raises <see cref="CallEventKind.OrphanedWork" />.
    ///     The callback awaits a <c>TaskCompletionSource</c> that never observes its token, so it keeps
    ///     running well past the 20ms attempt timeout. The contract: the <c>OrphanedWork</c> event is
    ///     raised, and the call succeeds with the callback's result - the executor is blocked on the
    ///     orphaned task until it completes, so the caller sees its value rather than a timeout.
    /// </summary>
    [Fact]
    public async Task An_attempt_ignoring_its_token_raises_OrphanedWork()
    {
        var events = new EventRecorder();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var policy = Resilience.Default with
        {
            Backoff = Backoff.None,
            Attempts = 1,
            AttemptTimeout = TimeSpan.FromMilliseconds(20),
            Deadline = Timeout.InfiniteTimeSpan,
            OnEvent = events.Record,
        };

        // The callback never looks at its token, which is the whole point.
        var call = policy.TryRunAsync(_ => release.Task).AsTask();

        // Wait past the 1s orphan grace threshold so the orphan is reported.
        await Task.Delay(TimeSpan.FromMilliseconds(1_100), TestContext.Current.CancellationToken);
        release.SetResult();

        var result = await call;

        Assert.True(result.IsSuccess);
        Assert.Equal(1, events.CountOf(CallEventKind.OrphanedWork));
        Assert.Equal(1, events.Single(CallEventKind.OrphanedWork).AttemptNumber);
    }

    /// <summary>
    ///     Orphaned work does not double-count against the retry budget. The same scenario as above,
    ///     but with a retry budget attached. The contract: the budget's available count is not
    ///     decremented twice for the one orphaned attempt - the orphan event is a diagnostic and charges
    ///     nothing, and the one successful deposit is the only charge.
    /// </summary>
    [Fact]
    public async Task Orphaned_work_does_not_double_count_against_the_retry_budget()
    {
        var events = new EventRecorder();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Capacity 1, no floor refill: a fresh budget starts with exactly one token. Draining it
        // leaves zero, so a single Deposit of fraction tokens is the only thing that can restore
        // any - and one Deposit is distinguishable from two by whether TrySpend succeeds after.
        var time = new FakeTimeProvider();
        var budget = RetryBudget.Of(0.5, 0, time);
        Assert.True(budget.TrySpend(), "A fresh budget should have one token to spend.");
        Assert.False(budget.TrySpend(), "The budget should now be empty.");

        var policy = Resilience.Default with
        {
            Backoff = Backoff.None,
            Attempts = 1,
            AttemptTimeout = TimeSpan.FromMilliseconds(20),
            Deadline = Timeout.InfiniteTimeSpan,
            Budget = budget,
            OnEvent = events.Record,
        };

        var call = policy.TryRunAsync(_ => release.Task).AsTask();

        await Task.Delay(TimeSpan.FromMilliseconds(1_100), TestContext.Current.CancellationToken);
        release.SetResult();

        await call;

        // The one attempt was orphaned. The event fires exactly once.
        Assert.Equal(1, events.CountOf(CallEventKind.OrphanedWork));

        // The orphan event is a diagnostic and charges nothing to the budget. The callback
        // succeeded, so exactly one Deposit landed: 0.5 tokens, which is below the 1-token
        // spend floor. A double-count (one Deposit for the success, one for the orphan) would
        // restore 1.0 token and make TrySpend succeed - it must not.
        Assert.False(budget.TrySpend(),
            "The orphan did not double-count: the budget holds 0.5 tokens from the one success Deposit, below the spend floor.");
    }
}
