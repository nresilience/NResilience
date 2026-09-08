using System.Globalization;
using System.Net;
using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     The published quota: what the handler reads off a response, when it refuses an attempt without
///     sending it, and what the refusal is charged to.
/// </summary>
public sealed class HttpQuotaTests
{
    private static readonly Uri Thing = new("https://api.test/thing");
    private static readonly Uri Other = new("https://other.test/thing");

    // ---- Reading the two shapes ----

    [Fact]
    public async Task The_standard_fields_refuse_an_attempt_once_the_remaining_allowance_is_inside_the_reserve()
    {
        var time = Clock();

        var transport = new ScriptedHttpHandler()
            .Responds(() => Standard(remaining: 5, quota: 100, resets: 30), 4);

        using var client = Client(transport, time);

        using var first = await client.GetAsync(Thing);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(1, transport.CallCount);

        await Assert.ThrowsAsync<RateLimitedException>(() => client.GetAsync(Thing));

        // Nothing was sent for the second call: the handler answered from the numbers the first
        // response published.
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task The_legacy_triple_is_read_when_the_standard_fields_are_absent()
    {
        var time = Clock();

        var transport = new ScriptedHttpHandler()
            .Responds(() => Legacy(limit: 100, remaining: 5, reset: "30"), 4);

        using var client = Client(transport, time);

        using var first = await client.GetAsync(Thing);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        await Assert.ThrowsAsync<RateLimitedException>(() => client.GetAsync(Thing));
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task A_legacy_reset_is_read_as_a_unix_timestamp_when_it_is_too_large_to_be_a_delta()
    {
        var time = Clock();
        var epoch = time.GetUtcNow().AddMinutes(10).ToUnixTimeSeconds();

        var transport = new ScriptedHttpHandler()
            .Responds(() => Legacy(limit: 100, remaining: 0, reset: epoch.ToString(CultureInfo.InvariantCulture)), 4);

        using var client = Client(transport, time);

        using var first = await client.GetAsync(Thing);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var refused = await Assert.ThrowsAsync<RateLimitedException>(() => client.GetAsync(Thing));

        Assert.Equal(TimeSpan.FromMinutes(10), refused.RetryAfter);
    }

    [Fact]
    public async Task The_standard_fields_win_over_the_legacy_triple()
    {
        var time = Clock();

        var transport = new ScriptedHttpHandler().Responds(
            () =>
            {
                var response = Standard(remaining: 90, quota: 100, resets: 30);
                response.Headers.TryAddWithoutValidation("X-RateLimit-Limit", "100");
                response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
                response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "30");

                return response;
            },
            4);

        using var client = Client(transport, time);

        using var first = await client.GetAsync(Thing);
        using var second = await client.GetAsync(Thing);

        // The standard field said 90 of 100 are left, so the legacy triple's zero is not read at all.
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, transport.CallCount);
    }

    [Fact]
    public async Task The_most_constraining_published_policy_is_the_one_that_binds()
    {
        var time = Clock();

        var transport = new ScriptedHttpHandler().Responds(
            () =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.TryAddWithoutValidation("RateLimit-Policy", "\"burst\";q=100;w=60, \"daily\";q=1000;w=86400");
                response.Headers.TryAddWithoutValidation("RateLimit", "\"burst\";r=50;t=30, \"daily\";r=40;t=600");

                return response;
            },
            4);

        using var client = Client(transport, time);

        using var first = await client.GetAsync(Thing);

        // Two policies: 50 of a 100 burst is well clear of the reserve, 40 of a 1,000 daily is not.
        var refused = await Assert.ThrowsAsync<RateLimitedException>(() => client.GetAsync(Thing));

        Assert.Equal(TimeSpan.FromSeconds(600), refused.RetryAfter);
    }

    [Fact]
    public async Task A_custom_legacy_triple_is_read_in_place_of_the_default_one()
    {
        var time = Clock();

        var transport = new ScriptedHttpHandler().Responds(
            () =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.TryAddWithoutValidation("X-Rate-Limit-Limit", "100");
                response.Headers.TryAddWithoutValidation("X-Rate-Limit-Remaining", "1");
                response.Headers.TryAddWithoutValidation("X-Rate-Limit-Reset", "30");

                return response;
            },
            4);

        var quota = Quota.Reserving() with
        {
            LegacyHeaders = ["X-Rate-Limit-Limit", "X-Rate-Limit-Remaining", "X-Rate-Limit-Reset"],
        };

        using var client = Client(transport, time, quota);

        using var first = await client.GetAsync(Thing);

        await Assert.ThrowsAsync<RateLimitedException>(() => client.GetAsync(Thing));
        Assert.Equal(1, transport.CallCount);
    }

    // ---- When it says nothing ----

    [Fact]
    public async Task A_host_that_publishes_nothing_is_never_refused()
    {
        var time = Clock();
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.OK, 4);

        using var client = Client(transport, time);

        using var first = await client.GetAsync(Thing);
        using var second = await client.GetAsync(Thing);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, transport.CallCount);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("\"default\"")]
    [InlineData("\"default\";r=x;t=30")]
    [InlineData("\"default\";r=5")]
    [InlineData(";;;")]
    public async Task A_malformed_field_is_ignored_rather_than_thrown(string value)
    {
        var time = Clock();

        var transport = new ScriptedHttpHandler().Responds(
            () =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.TryAddWithoutValidation("RateLimit", value);

                return response;
            },
            4);

        using var client = Client(transport, time);

        using var first = await client.GetAsync(Thing);
        using var second = await client.GetAsync(Thing);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, transport.CallCount);
    }

    [Fact]
    public async Task A_window_whose_reset_has_already_passed_is_no_constraint()
    {
        var time = Clock();

        var transport = new ScriptedHttpHandler()
            .Responds(() => Standard(remaining: 0, quota: 100, resets: 30), 4);

        using var client = Client(transport, time);

        using var first = await client.GetAsync(Thing);

        // The reading described a window that is now over, so the guard has no opinion until the next
        // response replaces it.
        time.Advance(TimeSpan.FromSeconds(31));

        using var second = await client.GetAsync(Thing);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, transport.CallCount);
    }

    [Fact]
    public async Task No_remaining_allowance_refuses_even_when_the_host_publishes_no_quota_to_take_a_fraction_of()
    {
        var time = Clock();

        var transport = new ScriptedHttpHandler().Responds(
            () =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.TryAddWithoutValidation("RateLimit", "\"default\";r=0;t=30");

                return response;
            },
            4);

        using var client = Client(transport, time);

        using var first = await client.GetAsync(Thing);

        var refused = await Assert.ThrowsAsync<RateLimitedException>(() => client.GetAsync(Thing));

        Assert.Equal(TimeSpan.FromSeconds(30), refused.RetryAfter);
    }

    [Fact]
    public async Task A_null_quota_reads_no_headers_at_all()
    {
        var time = Clock();

        var transport = new ScriptedHttpHandler()
            .Responds(() => Standard(remaining: 0, quota: 100, resets: 30), 4);

        using var client = new HttpClient(
            new HttpResilienceHandler(transport, TestPolicy.InstantHttp.WithClock(time), new HttpResilienceOptions { Quota = null }));

        using var first = await client.GetAsync(Thing);
        using var second = await client.GetAsync(Thing);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, transport.CallCount);
    }

    // ---- The reserve ----

    [Theory]
    [InlineData(11, false)]
    [InlineData(10, true)]
    [InlineData(9, true)]
    public async Task The_reserve_is_the_boundary_the_refusal_starts_at(int remaining, bool refused)
    {
        var time = Clock();

        var transport = new ScriptedHttpHandler()
            .Responds(() => Standard(remaining, quota: 100, resets: 30), 4);

        using var client = Client(transport, time);

        using var first = await client.GetAsync(Thing);

        if (refused)
        {
            await Assert.ThrowsAsync<RateLimitedException>(() => client.GetAsync(Thing));
            Assert.Equal(1, transport.CallCount);

            return;
        }

        using var second = await client.GetAsync(Thing);

        Assert.Equal(2, transport.CallCount);
    }

    [Fact]
    public async Task A_reserve_of_zero_spends_the_whole_published_allowance()
    {
        var time = Clock();

        var transport = new ScriptedHttpHandler()
            .Responds(() => Standard(remaining: 1, quota: 100, resets: 30), 4);

        using var client = Client(transport, time, Quota.Reserving(0));

        using var first = await client.GetAsync(Thing);
        using var second = await client.GetAsync(Thing);

        Assert.Equal(2, transport.CallCount);
    }

    // ---- Scope, and what the refusal costs ----

    [Fact]
    public async Task Two_hosts_keep_independent_allowances()
    {
        var time = Clock();

        using var client = Client(new SpentForOneHost(), time);

        using var spent = await client.GetAsync(Thing);
        using var healthy = await client.GetAsync(Other);

        // api.test published a spent allowance; other.test published none, and its calls are unaffected.
        await Assert.ThrowsAsync<RateLimitedException>(() => client.GetAsync(Thing));

        using var again = await client.GetAsync(Other);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task The_refusal_is_charged_to_neither_the_breaker_nor_the_retry_budget()
    {
        var time = Clock();
        var recorder = new EventRecorder();

        var transport = new ScriptedHttpHandler()
            .Responds(() => Standard(remaining: 0, quota: 100, resets: 30), 4);

        var policy = TestPolicy.InstantHttp.WithClock(time) with
        {
            Budget = RetryBudget.Of(time: time),
            OnEvent = recorder.Record,
        };

        var handler = new HttpResilienceHandler(transport, policy, new HttpResilienceOptions());

        using var client = new HttpClient(handler);

        using var first = await client.GetAsync(Thing);

        recorder.Clear();

        await Assert.ThrowsAsync<RateLimitedException>(() => client.GetAsync(Thing));

        // Three attempts, all of them refused locally. Nothing left the process, so the budget funded
        // nothing and the breaker learned nothing about a dependency it never contacted.
        Assert.Equal(3, recorder.CountOf(CallEventKind.Attempt));
        Assert.Equal(0, handler.BudgetsByHost()["api.test"].Utilization);
        Assert.Equal(BreakerState.Closed, handler.BreakersByHost()["api.test"].State);

        foreach (var attempt in recorder.OfKind(CallEventKind.Attempt))
        {
            Assert.True(attempt.Verdict.SelfImposed);
            Assert.Equal(VerdictKind.Throttled, attempt.Verdict.Kind);
        }
    }

    [Fact]
    public async Task The_refusal_raises_RejectedByQuota_carrying_the_time_until_the_window_resets()
    {
        var time = Clock();
        var recorder = new EventRecorder();

        var transport = new ScriptedHttpHandler()
            .Responds(() => Standard(remaining: 0, quota: 100, resets: 30), 4);

        var policy = TestPolicy.InstantHttp.WithClock(time) with { Attempts = 1, OnEvent = recorder.Record };

        using var client = new HttpClient(new HttpResilienceHandler(transport, policy, new HttpResilienceOptions()));

        using var first = await client.GetAsync(Thing);

        recorder.Clear();

        await Assert.ThrowsAsync<RateLimitedException>(() => client.GetAsync(Thing));

        var raised = recorder.Single(CallEventKind.RejectedByQuota);

        Assert.Equal(TimeSpan.FromSeconds(30), raised.Delay);
        Assert.True(raised.Verdict.SelfImposed);
        Assert.False(raised.IsTerminal);
        Assert.False(raised.IsRejection);
    }

    [Fact]
    public async Task The_allowance_is_spendable_again_once_the_published_window_resets()
    {
        var time = Clock();

        var transport = new ScriptedHttpHandler()
            .Responds(() => Standard(remaining: 0, quota: 100, resets: 30), 1)
            .Responds(() => Standard(remaining: 100, quota: 100, resets: 60), 4);

        using var client = Client(transport, time);

        using var first = await client.GetAsync(Thing);

        await Assert.ThrowsAsync<RateLimitedException>(() => client.GetAsync(Thing));

        time.Advance(TimeSpan.FromSeconds(31));

        using var second = await client.GetAsync(Thing);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, transport.CallCount);
    }

    // ---- Configuration ----

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void A_reserve_outside_zero_to_one_is_refused_at_construction(double reserve)
    {
        var options = new HttpResilienceOptions { Quota = Quota.Reserving(reserve) };

        var problem = Assert.Throws<ResilienceConfigurationException>(options.Validate);

        Assert.Contains(problem.Problems, message => message.Contains("Quota.Reserve", StringComparison.Ordinal));
    }

    [Fact]
    public void A_legacy_triple_that_is_not_three_names_is_refused_at_construction()
    {
        var options = new HttpResilienceOptions
        {
            Quota = Quota.Reserving() with { LegacyHeaders = ["X-RateLimit-Remaining"] },
        };

        var problem = Assert.Throws<ResilienceConfigurationException>(options.Validate);

        Assert.Contains(problem.Problems, message => message.Contains("Quota.LegacyHeaders", StringComparison.Ordinal));
    }

    [Fact]
    public void The_default_options_hold_a_tenth_of_the_published_allowance_in_reserve()
    {
        var options = new HttpResilienceOptions();

        Assert.NotNull(options.Quota);
        Assert.Equal(0.1, options.Quota.Reserve);
        Assert.Null(options.Quota.LegacyHeaders);
    }

    // ---- Helpers ----

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static HttpClient Client(HttpMessageHandler transport, TimeProvider time, Quota? quota = null) =>
        new(new HttpResilienceHandler(
            transport,
            TestPolicy.InstantHttp.WithClock(time),
            new HttpResilienceOptions { Quota = quota ?? Quota.Reserving() }));

    private static HttpResponseMessage Standard(int remaining, int quota, int resets)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);

        response.Headers.TryAddWithoutValidation("RateLimit-Policy", $"\"default\";q={quota};w=60");
        response.Headers.TryAddWithoutValidation("RateLimit", $"\"default\";r={remaining};t={resets}");

        return response;
    }

    private static HttpResponseMessage Legacy(int limit, int remaining, string reset)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);

        response.Headers.TryAddWithoutValidation("X-RateLimit-Limit", limit.ToString(CultureInfo.InvariantCulture));
        response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", remaining.ToString(CultureInfo.InvariantCulture));
        response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset);

        return response;
    }

    /// <summary>A transport where one host publishes a spent allowance and the other publishes none.</summary>
    private sealed class SpentForOneHost : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);

            if (request.RequestUri?.Host == "api.test")
            {
                response.Headers.TryAddWithoutValidation("RateLimit-Policy", "\"default\";q=100;w=60");
                response.Headers.TryAddWithoutValidation("RateLimit", "\"default\";r=0;t=30");
            }

            return Task.FromResult(response);
        }
    }
}
