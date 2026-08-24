using System.Net;
using Microsoft.Extensions.DependencyInjection;
using NResilience.Extensions;
using NResilience.Testing;

namespace NResilience.Docs;

/// <summary>The "after" half of every before-and-after pair in the Polly migration guide.</summary>
public sealed class Migration
{
    [Fact]
    public void Retry_with_a_timeout_and_a_breaker()
    {
        // A snippet is not a call path. The reader holds this policy in a static readonly field, which
        // is what NRES005 asks for; here it lives in a test method so that the docs gate can run it.
#pragma warning disable NRES005

        // <snippet:migration-pipeline>
        // One value. No pipeline, no builder, no ordering to get right - and the breaker samples
        // attempts whichever way you read it.
        var api = Resilience.Http with
        {
            Attempts = 3, // total, including the first
            AttemptTimeout = TimeSpan.FromSeconds(value: 3), // per attempt
            Deadline = TimeSpan.FromSeconds(value: 10), // the whole call
            Breaker = new Breaker { Name = "api" },
        };

        // </snippet:migration-pipeline>
#pragma warning restore NRES005

        Assert.Equal(expected: 3, actual: api.Attempts);
        Assert.NotNull(@object: api.Breaker);
    }

    [Fact]
    public async Task A_fallback_becomes_an_if()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var calls = Sequence.For<string>().Throws(exception: new IOException(), count: 3);
        var api = Resilience.Default with { Backoff = Backoff.None };

        // <snippet:migration-fallback>
        var result = await api.TryRunAsync(attempt => calls.NextAsync(cancellationToken: attempt), cancellationToken: cancellationToken);
        var value = result.TryGetValue(value: out var fetched) ? fetched : "cached";

        // </snippet:migration-fallback>

        Assert.Equal(expected: "cached", actual: value);
    }

    [Fact]
    public void Registration_is_one_line()
    {
        var services = new ServiceCollection();

        // <snippet:migration-registration>
        services.AddHttpClient<Client>().AddResilience();

        // </snippet:migration-registration>

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(@object: provider.GetRequiredService<Client>());
    }

    [Fact]
    public async Task The_original_exception_still_comes_out()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var calls = Sequence.For<int>().Throws(exception: new HttpRequestException(message: "down"), count: 3);
        var api = Resilience.Http with { Backoff = Backoff.None };

        // <snippet:migration-exceptions>
        // The original exception is rethrown unchanged, with its stack intact, so existing catch
        // blocks keep working. The history rides along on Exception.Data.
        try
        {
            await api.RunAsync(attempt => calls.NextAsync(cancellationToken: attempt), cancellationToken: cancellationToken);
        }
        catch (HttpRequestException e)
        {
            var attempts = AttemptLog.Of(exception: e);
            Console.WriteLine(value: attempts); // 3 attempts over 1.4ms: Transient HttpRequestException (0.5ms), ...
        }

        // </snippet:migration-exceptions>

        Assert.Equal(expected: 3, actual: calls.CallCount);
    }

    [Fact]
    public async Task A_status_code_predicate_becomes_a_result_rule()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var calls = Sequence.For<HttpResponseMessage>()
            .Returns(result: new HttpResponseMessage(statusCode: HttpStatusCode.Conflict))
            .Returns(result: new HttpResponseMessage(statusCode: HttpStatusCode.OK));

        // <snippet:migration-predicate>
        // Classifier.Http already knows that a 429 is throttling, a 5xx or 408 is transient and a
        // 404 is an answer. Adding a status of your own is one rule, and retry, the breaker and
        // the budget all read it.
        var api = Resilience.Http with
        {
            Backoff = Backoff.None,
            Classify = Classifier.Http.OnResult<HttpResponseMessage>(r =>
                r.StatusCode == HttpStatusCode.Conflict ? Verdict.Transient : Classifier.Http.ClassifyResult(value: r)),
        };

        // </snippet:migration-predicate>

        using var response = await api.RunAsync(attempt => calls.NextAsync(cancellationToken: attempt), cancellationToken: cancellationToken);
        Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);
    }

    [Fact]
    public async Task A_bulkhead_is_a_concurrency_limit()
    {
        var services = new ServiceCollection();
        var cancellationToken = TestContext.Current.CancellationToken;
        var policy = Resilience.Http with { Backoff = Backoff.None };
        var dependency = new Dependency();

        // <snippet:migration-bulkhead>
        // For HTTP clients via dependency injection
        services.AddHttpClient<PaymentClient>()
            .AddResilience()
            .AddRateLimit(options => options.Concurrency = 10);

        // For any other callback
        using var limiter = Limit.Concurrency(permits: 10);

        var result = await policy.RunAsync(async ct =>
        {
            using var lease = await limiter.AcquireOrThrowAsync(cancellationToken: ct);
            return await dependency.CallAsync(cancellationToken: ct);
        }, cancellationToken: cancellationToken);

        // </snippet:migration-bulkhead>

        Assert.Equal(expected: 1, actual: result);
    }

    internal sealed class Client(HttpClient client)
    {
        internal HttpClient Http { get; } = client;
    }

    internal sealed class PaymentClient(HttpClient client)
    {
        internal HttpClient Http { get; } = client;
    }

    internal sealed class Dependency
    {
        internal Task<int> CallAsync(CancellationToken cancellationToken) => Task.FromResult(result: 1);
    }
}
