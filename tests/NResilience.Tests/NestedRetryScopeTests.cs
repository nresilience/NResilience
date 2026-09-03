namespace NResilience.Tests;

/// <summary>
///     The ambient caller-retrying flag: the inbound half of nested-retry detection, where a server
///     reads the marker a retrying caller put on the request and publishes it for everything the
///     request handler does.
///     <para>
///         The claim being tested is that the flag behaves like the ambient deadline it is modeled
///         on - it flows across awaits, nested scopes restore rather than overwrite, and an
///         explicit opt-out is possible - while staying a bool, because unlike a deadline it does
///         not decay and needs no clock.
///     </para>
/// </summary>
public sealed class NestedRetryScopeTests
{
    [Fact]
    public void The_flag_is_false_when_nobody_published_one()
    {
        Assert.False(ResilienceNestedRetry.IsCallerRetrying);
    }

    [Fact]
    public void Begin_publishes_the_flag_for_the_scope()
    {
        using (ResilienceNestedRetry.Begin(true))
        {
            Assert.True(ResilienceNestedRetry.IsCallerRetrying);
        }

        Assert.False(ResilienceNestedRetry.IsCallerRetrying);
    }

    [Fact]
    public async Task The_flag_survives_an_await()
    {
        using var scope = ResilienceNestedRetry.Begin(true);

        await Task.Yield();

        // The awaiter resuming on another context is the case an AsyncLocal exists for: the request
        // handler awaits, and the outbound call it makes afterwards still needs to know.
        Assert.True(ResilienceNestedRetry.IsCallerRetrying);
    }

    [Fact]
    public void Nested_scopes_restore_the_outer_value()
    {
        using var outer = ResilienceNestedRetry.Begin(true);

        using (ResilienceNestedRetry.Begin(false))
        {
            Assert.False(ResilienceNestedRetry.IsCallerRetrying);
        }

        Assert.True(ResilienceNestedRetry.IsCallerRetrying);
    }

    [Fact]
    public void A_scope_that_publishes_false_hides_an_outer_true()
    {
        using var outer = ResilienceNestedRetry.Begin(true);
        using var inner = ResilienceNestedRetry.Begin(false);

        // Explicit opt-out: a sub-operation that knows its own retries are not the caller's can say
        // so, without un-publishing the flag for the rest of the request.
        Assert.False(ResilienceNestedRetry.IsCallerRetrying);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData(" 1", false)]
    public void IsMarker_accepts_only_the_marker(string? value, bool expected)
    {
        Assert.Equal(expected, ResilienceNestedRetry.IsMarker(value));
    }
}
