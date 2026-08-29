using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NResilience.AspNetCore;
using NResilience.Http;

namespace NResilience.IntegrationTests;

/// <summary>
///     The exception handler over a real server: the four exceptions the library invents arrive from
///     a request pipeline, and the responses a real caller gets back are the ones they mean.
///     <para>
///         The exceptions are thrown directly by the endpoint, which is the right level: provoking a
///         real breaker rejection or budget exhaustion over a socket is slow and timing-dependent,
///         the exception types are public, and the handler's job is to map them. The one test that
///         needs a real policy is <see cref="IncludeAttemptDetails_adds_the_count_and_elapsed" /> -
///         a hand-constructed <see cref="AttemptLog.Empty" /> would make its assertion vacuous.
///     </para>
/// </summary>
public sealed class ResilienceExceptionHandlerTests
{
    [Fact]
    public async Task A_deadline_exception_maps_to_504()
    {
        await using var app = await App();

        using var client = new HttpClient();
        using var response = await client.GetAsync(app.Uri);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        await AssertProblemAsync(response, "Deadline Exceeded", "urn:nresilience:deadline-exceeded");
    }

    [Fact]
    public async Task An_attempt_timeout_maps_to_504()
    {
        await using var app = await App(exception: new AttemptTimeoutException(TimeSpan.FromSeconds(10)));

        using var client = new HttpClient();
        using var response = await client.GetAsync(app.Uri);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        await AssertProblemAsync(response, "Attempt Timeout", "urn:nresilience:attempt-timeout");
    }

    [Fact]
    public async Task A_breaker_rejection_maps_to_503_with_retry_after()
    {
        await using var app = await App(exception: new CallRejectedException(
            StopReason.DependencyUnavailable, AttemptLog.Empty, TimeSpan.FromSeconds(15)));

        using var client = new HttpClient();
        using var response = await client.GetAsync(app.Uri);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("15", response.Headers.GetValues("Retry-After").Single());
        await AssertProblemAsync(response, "Dependency Unavailable", "urn:nresilience:dependency-unavailable");
    }

    [Fact]
    public async Task A_budget_rejection_is_titled_separately()
    {
        await using var app = await App(exception: new CallRejectedException(
            StopReason.BudgetExhausted, AttemptLog.Empty, TimeSpan.FromSeconds(15)));

        using var client = new HttpClient();
        using var response = await client.GetAsync(app.Uri);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("15", response.Headers.GetValues("Retry-After").Single());
        await AssertProblemAsync(response, "Retry Budget Exhausted", "urn:nresilience:retry-budget-exhausted");
    }

    [Fact]
    public async Task A_rate_limited_exception_maps_to_503_with_retry_after()
    {
        await using var app = await App(exception: new RateLimitedException("orders", TimeSpan.FromSeconds(1)));

        using var client = new HttpClient();
        using var response = await client.GetAsync(app.Uri);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("1", response.Headers.GetValues("Retry-After").Single());
        await AssertProblemAsync(response, "Rate Limited", "urn:nresilience:rate-limited");
    }

    [Fact]
    public async Task Retry_after_is_whole_seconds_rounded_up()
    {
        await using var app = await App(exception: new RateLimitedException("orders", TimeSpan.FromSeconds(2.5)));

        using var client = new HttpClient();
        using var response = await client.GetAsync(app.Uri);

        Assert.Equal("3", response.Headers.GetValues("Retry-After").Single());
    }

    [Fact]
    public async Task A_rejection_without_a_hint_writes_no_retry_after()
    {
        await using var app = await App(exception: new CallRejectedException(StopReason.DependencyUnavailable, AttemptLog.Empty));

        using var client = new HttpClient();
        using var response = await client.GetAsync(app.Uri);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(response.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task A_status_code_can_be_changed()
    {
        await using var app = await App(
            configure: o => o.RateLimitedStatusCode = StatusCodes.Status429TooManyRequests,
            exception: new RateLimitedException());

        using var client = new HttpClient();
        using var response = await client.GetAsync(app.Uri);

        Assert.Equal((HttpStatusCode)429, response.StatusCode);
    }

    [Fact]
    public async Task An_invalid_status_code_is_refused()
    {
        // The options validation fires when the handler is first resolved - which is at startup, in
        // UseExceptionHandler's middleware construction, rather than on the first request. Either
        // way it fires loudly: a status that is not a status is refused rather than written.
        await Assert.ThrowsAsync<OptionsValidationException>(async () =>
            await App(configure: o => o.TimeoutStatusCode = 42));
    }

    [Fact]
    public async Task Attempt_details_are_absent_by_default()
    {
        await using var app = await App();

        using var client = new HttpClient();
        using var response = await client.GetAsync(app.Uri);

        var problem = await ReadProblemAsync(response);
        Assert.False(problem.RootElement.TryGetProperty("resilience", out _));
    }

    [Fact]
    public async Task IncludeAttemptDetails_adds_the_count_and_elapsed()
    {
        // The one test that provokes a real exception from a real policy: a hand-constructed empty
        // log would make the assertions below vacuous, and the attempt count is the whole point of
        // the extension member. The downstream stalls every response; each attempt is stopped by
        // its own ceiling and retried until the deadline runs out, so the call ends on a genuinely
        // populated log - attempts that really ran, over time that really passed.
        await using var downstream = await LoopbackHttp.StartAsync(
            (_, _) => Task.FromResult(new LoopbackResponse(HttpStatusCode.OK, Delay: TimeSpan.FromSeconds(30))));

        var policy = Resilience.Http with
        {
            Attempts = 10,
            Backoff = Backoff.None,
            Deadline = TimeSpan.FromSeconds(1),
            AttemptTimeout = TimeSpan.FromMilliseconds(100),

            // The preset's automatic budget would refuse the retries and its breaker would refuse
            // the call outright - both before the deadline had a say, and the test is about what
            // the deadline reports.
            Budget = null,
            Breaker = null,
        };

        await using var app = await App(configure: o => o.IncludeAttemptDetails = true, policy: policy, downstream: downstream);

        using var client = new HttpClient();
        using var response = await client.GetAsync(app.Uri);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);

        var problem = await ReadProblemAsync(response);
        var details = problem.RootElement.GetProperty("resilience");
        Assert.True(details.GetProperty("attempts").GetInt32() >= 1);
        Assert.True(details.GetProperty("elapsedMs").GetDouble() > 0);
    }

    [Fact]
    public async Task The_body_is_a_valid_problem_document()
    {
        await using var app = await App();

        using var client = new HttpClient();
        using var response = await client.GetAsync(app.Uri);

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await ReadProblemAsync(response);
        Assert.Equal(504, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("/", problem.RootElement.GetProperty("instance").GetString());
        Assert.NotEmpty(problem.RootElement.GetProperty("detail").GetString()!);
    }

    [Fact]
    public async Task An_unrecognized_exception_is_not_handled()
    {
        await using var app = await App(exception: new InvalidOperationException("unrecognized"));

        using var client = new HttpClient();
        using var response = await client.GetAsync(app.Uri);

        // The framework's own handling takes over - a 500 whose body is its problem document, not
        // this handler's: none of our type URIs appear in it.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("urn:nresilience", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_configuration_exception_is_not_handled()
    {
        await using var app = await App(exception: new ResilienceConfigurationException(["bad policy"]));

        using var client = new HttpClient();
        using var response = await client.GetAsync(app.Uri);

        // Deliberately unmapped: a bug in the application's own setup deserves the 500 the
        // framework already gives it.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("urn:nresilience", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_handler_composes_with_another_handler()
    {
        // The chain of responsibility: the resilience handler passes an unrecognized exception to
        // the handler registered after it, and still takes the exceptions that are its own.
        void ConfigureBuilder(WebApplicationBuilder builder)
        {
            builder.Services.AddResilienceExceptionHandler();
            builder.Services.AddProblemDetails();
            builder.Services.AddExceptionHandler<ThrowingHandler>();
        }

        await using var unrecognized = await TestApp.StartAsync(
            ConfigureBuilder,
            app =>
            {
                app.UseExceptionHandler();
                app.Run(_ => throw new InvalidOperationException("unrecognized"));
            });

        await using var resilience = await TestApp.StartAsync(
            ConfigureBuilder,
            app =>
            {
                app.UseExceptionHandler();
                app.Run(_ => throw new DeadlineExceededException());
            });

        using var client = new HttpClient();

        using (var response = await client.GetAsync(unrecognized.Uri))
        {
            // The second handler wins: the resilience handler reported the exception unhandled,
            // and the one that recognized it answered.
            Assert.Equal((HttpStatusCode)418, response.StatusCode);
        }

        using (var response = await client.GetAsync(resilience.Uri))
        {
            // And a DeadlineExceededException is still taken by the first.
            Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_response_that_has_already_started_is_left_alone()
    {
        // The framework's middleware rethrows before consulting any handler once the response has
        // started, so the guard can only be exercised by invoking the handler directly - which is
        // also the contract it makes: return false, throw nothing, touch nothing. The response
        // feature says started the only honest way a DefaultHttpContext can: by saying it.
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddResilienceExceptionHandler();

        await using var app = builder.Build();
        var handler = app.Services.GetRequiredService<IExceptionHandler>();

        var features = new FeatureCollection();
        features.Set<IHttpRequestFeature>(new HttpRequestFeature());
        features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        var context = new DefaultHttpContext(features);
        context.Response.StatusCode = StatusCodes.Status200OK;

        var handled = await handler.TryHandleAsync(context, new DeadlineExceededException(), CancellationToken.None);

        Assert.False(handled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    private static async Task<TestServer> App(
        Action<ResilienceExceptionHandlerOptions>? configure = null,
        Exception? exception = null,
        Resilience? policy = null,
        LoopbackHttp? downstream = null)
    {
        return await TestApp.StartAsync(
            builder =>
            {
                builder.Services.AddResilienceExceptionHandler(configure);

                // Parameterless UseExceptionHandler() needs the framework's problem-details
                // service; without it the middleware refuses to construct.
                builder.Services.AddProblemDetails();
            },
            app =>
            {
                app.UseExceptionHandler();

                app.Run(async context =>
                {
                    if (policy is { } live)
                    {
                        // The real-policy path: run the policy against the downstream and let the
                        // exception it throws propagate to the middleware. The handler options turn
                        // the per-host guards off - this one client serves one host, and the call
                        // is meant to end on the deadline, not on a guard the handler scoped.
                        var handlerOptions = new HttpResilienceOptions { BreakerPerHost = false, BudgetPerHost = false };

                        using var client = new HttpClient(new ResilienceHandler(new SocketsHttpHandler(), live, handlerOptions))
                        {
                            Timeout = Timeout.InfiniteTimeSpan,
                        };

                        using var hop = await client.GetAsync(downstream!.BaseUri, context.RequestAborted);
                        return;
                    }

                    throw exception ?? new DeadlineExceededException();
                });
            });
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, string title, string type)
    {
        var problem = await ReadProblemAsync(response);
        Assert.Equal(title, problem.RootElement.GetProperty("title").GetString());
        Assert.Equal(type, problem.RootElement.GetProperty("type").GetString());
    }

    private static async Task<JsonDocument> ReadProblemAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.Content.Headers.ContentType?.MediaType == "application/problem+json",
            $"Expected a problem document, got: {body}");

        return JsonDocument.Parse(body);
    }

    /// <summary>Answers 418, and only for the exception the resilience handler does not want.</summary>
    private sealed class ThrowingHandler : IExceptionHandler
    {
        public ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
        {
            if (exception is not InvalidOperationException)
                return ValueTask.FromResult(false);

            context.Response.StatusCode = StatusCodes.Status418ImATeapot;
            return ValueTask.FromResult(true);
        }
    }

    /// <summary>
    ///     A response feature whose <c>HasStarted</c> is true, which is the one thing a
    ///     <see cref="DefaultHttpContext" /> cannot be told to say any other way.
    /// </summary>
    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }
}