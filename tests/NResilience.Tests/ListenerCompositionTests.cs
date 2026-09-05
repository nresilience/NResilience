using NResilience.Extensions;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     <see cref="Resilience.WithListener" />: the combinator the library used on itself, so a caller
///     can add a listener without taking one away.
/// </summary>
public sealed class ListenerCompositionTests
{
    [Fact]
    public async Task A_listener_is_added_to_whatever_is_already_there()
    {
        var first = new EventRecorder();
        var second = new EventRecorder();

        var policy = TestPolicy.Instant
            .WithListener(first.Record)
            .WithListener(second.Record);

        await policy.RunAsync(ct => Task.FromResult(1), CancellationToken.None);

        Assert.True(first.Contains(CallEventKind.Succeeded));
        Assert.True(second.Contains(CallEventKind.Succeeded));
    }

    [Fact]
    public async Task Listeners_run_in_the_order_they_were_added()
    {
        var order = new List<string>();

        var policy = TestPolicy.Instant
            .WithListener(_ => order.Add("first"))
            .WithListener(_ => order.Add("second"));

        await policy.RunAsync(ct => Task.FromResult(1), CancellationToken.None);

        Assert.Equal(["first", "second"], order.Take(2));
    }

    [Fact]
    public async Task It_keeps_the_telemetry_a_registration_attached_where_a_with_expression_would_drop_it()
    {
        var mine = new EventRecorder();
        var registered = new EventRecorder();

        // What AddResilience() does to a policy before handing it over.
        var asRegistered = TestPolicy.Instant with { OnEvent = registered.Record };

        var replaced = asRegistered with { OnEvent = mine.Record };
        var added = asRegistered.WithListener(mine.Record);

        await replaced.RunAsync(ct => Task.FromResult(1), CancellationToken.None);

        Assert.True(mine.Contains(CallEventKind.Succeeded));
        Assert.Equal(0, registered.Count);

        mine.Clear();
        await added.RunAsync(ct => Task.FromResult(1), CancellationToken.None);

        Assert.True(mine.Contains(CallEventKind.Succeeded));
        Assert.True(registered.Contains(CallEventKind.Succeeded));
    }

    [Fact]
    public async Task It_composes_with_the_telemetry_and_logging_listeners()
    {
        var mine = new EventRecorder();

        var policy = TestPolicy.Instant
            .WithTelemetry()
            .WithListener(mine.Record);

        await policy.RunAsync(ct => Task.FromResult(1), CancellationToken.None);

        Assert.True(mine.Contains(CallEventKind.Succeeded));

        // WithTelemetry is idempotent by reference, so the shared listener is still the one attached
        // and adding it again after ours changes nothing.
        Assert.Equal(policy.OnEvent, policy.WithTelemetry().OnEvent);
    }

    [Fact]
    public void It_rejects_a_null_listener()
        => Assert.Throws<ArgumentNullException>(() => TestPolicy.Instant.WithListener(null!));

    [Fact]
    public void It_leaves_the_receiver_alone()
    {
        var policy = TestPolicy.Instant;

        _ = policy.WithListener(_ => { });

        Assert.Null(policy.OnEvent);
    }
}
