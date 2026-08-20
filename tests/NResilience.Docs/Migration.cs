using System.Net;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using NResilience.Http;
using NResilience.Testing;

namespace NResilience.Docs;

/// <summary>The "after" half of every before-and-after pair in the Polly migration guide.</summary>
public sealed class Migration
{
    [Fact]
    public void Retry_with_a_timeout_and_a_breaker()
    {
        // <snippet:migration-pipeline>
        // One value. No pipeline, no builder, no ordering to get right - and the breaker samples
        // attempts whichever way you read it.
        var api = Resilience.Http with
        {
            Attempts = 3,                                 // total, including the first
            AttemptTimeout = TimeSpan.FromSeconds(3),      // per attempt
            Deadline = TimeSpan.FromSeconds(10),           // the whole call
            Breaker = new Breaker { Name = "api" },
        };
        // </snippet:migration-pipeline>

        Assert.Equal(3, api.Attempts);
        Assert.NotNull(api.Breaker);
    }

    [Fact]
    public async Task A_fallback_becomes_an_if()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Sequence<string> calls = Sequence.For<string>().Throws(new IOException(), 3);
        var api = Resilience.Default with { Backoff = Backoff.None };

        // <snippet:migration-fallback>
        CallResult<string> result = await api.TryRunAsync(attempt => calls.NextAsync(attempt), cancellationToken);
        string value = result.TryGetValue(out string? fetched) ? fetched : "cached";
        // </snippet:migration-fallback>

        Assert.Equal("cached", value);
    }

    [Fact]
    public void Registration_is_one_line()
    {
        var services = new ServiceCollection();

        // <snippet:migration-registration>
        services.AddHttpClient<Client>().AddResilience();
        // </snippet:migration-registration>

        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<Client>());
    }

    [Fact]
    public async Task The_original_exception_still_comes_out()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Sequence<int> calls = Sequence.For<int>().Throws(new HttpRequestException("down"), 3);
        var api = Resilience.Http with { Backoff = Backoff.None };

        // <snippet:migration-exceptions>
        // The original exception is rethrown unchanged, with its stack intact, so existing catch
        // blocks keep working. The history rides along on Exception.Data.
        try
        {
            await api.RunAsync(attempt => calls.NextAsync(attempt), cancellationToken);
        }
        catch (HttpRequestException e)
        {
            AttemptLog? attempts = AttemptLog.Of(e);
            Console.WriteLine(attempts);   // 3 attempts over 1.4ms: Transient HttpRequestException (0.5ms), ...
        }
        // </snippet:migration-exceptions>

        Assert.Equal(3, calls.CallCount);
    }

    [Fact]
    public async Task A_status_code_predicate_becomes_a_result_rule()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Sequence<HttpResponseMessage> calls = Sequence.For<HttpResponseMessage>()
            .Returns(new HttpResponseMessage(HttpStatusCode.Conflict))
            .Returns(new HttpResponseMessage(HttpStatusCode.OK));

        // <snippet:migration-predicate>
        // Classifier.Http already knows that a 429 is throttling, a 5xx or 408 is transient and a
        // 404 is an answer. Adding a status of your own is one rule, and retry, the breaker and
        // the budget all read it.
        var api = Resilience.Http with
        {
            Backoff = Backoff.None,
            Classify = Classifier.Http.OnResult<HttpResponseMessage>(r =>
                r.StatusCode == HttpStatusCode.Conflict ? Verdict.Transient : Classifier.Http.ClassifyResult(r)),
        };
        // </snippet:migration-predicate>

        using HttpResponseMessage response = await api.RunAsync(attempt => calls.NextAsync(attempt), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    internal sealed class Client(HttpClient client)
    {
        internal HttpClient Http { get; } = client;
    }
}
