using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NResilience.Testing;

namespace NResilience.Docs;

/// <summary>The whole-API tour, on the README and on the docs landing page.</summary>
public sealed class Tour
{
    private readonly ILogger _logger = NullLogger.Instance;

    [Fact]
    public async Task The_whole_api()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var client = new HttpClient(handler: new ScriptedHttpHandler().Responds(() => Doubles.Json(value: new User(Name: "ada"))))
        {
            BaseAddress = new Uri(uriString: "https://api.example.com"),
        };

        var url = new Uri(uriString: "https://api.example.com/users/1");
        var queue = new Doubles.Queue();
        var cache = new Doubles.Cache<User>(lastKnownGood: new User(Name: "last known good"));

        // <snippet:whole-api>
        // 1. Start from a preset. `Resilience.Http` retries and times out an HTTP call out of the box.
        var api = Resilience.Http;

        // 2. Change one setting, keep the rest: `with` copies everything you did not mention.
        var slow = Resilience.Http with { Attempts = 5, Deadline = TimeSpan.FromSeconds(value: 20) };

        // 3. Run any callback through one method. The token handed to your work is the attempt's own.
        var user = await api.RunAsync(attempt => client.GetFromJsonAsync<User>(requestUri: url, cancellationToken: attempt),
            cancellationToken: cancellationToken);

        var response = await api.RunAsync(attempt => client.GetAsync(requestUri: url, cancellationToken: attempt), cancellationToken: cancellationToken);
        await slow.RunAsync(attempt => queue.FlushAsync(cancellationToken: attempt), cancellationToken: cancellationToken);

        // 4. Want the outcome without an exception? `TryRunAsync` hands it back to branch on.
        var result = await api.TryRunAsync(attempt => FetchAsync(cancellationToken: attempt), cancellationToken: cancellationToken);
        var best = result.TryGetValue(value: out var fetched) ? fetched : cache.LastKnownGood;

        // </snippet:whole-api>

        response.Dispose();
        Assert.Equal(expected: "ada", actual: user?.Name);
        Assert.Equal(expected: "last known good", actual: best.Name);
        Assert.Equal(expected: 1, actual: queue.Flushes);
        Assert.Equal(expected: 5, actual: slow.Attempts);
    }

    [Fact]
    public async Task A_fallback_is_an_if()
    {
        var cache = new UserCache(LastKnownGood: new User(Name: "last known good"));

        var served = await ReadUserAsync(cache: cache, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "last known good", actual: served.Name);
    }

    // <snippet:fallback-is-an-if>
    private async Task<User> ReadUserAsync(UserCache cache, CancellationToken cancellationToken)
    {
        var result = await Resilience.Http.TryRunAsync(attempt => FetchAsync(cancellationToken: attempt), cancellationToken: cancellationToken);

        if (result.TryGetValue(value: out var user))
            return user;

        _logger.LogWarning(message: "Serving the cached user: {Reason} after {Attempts}", result.StopReason, result.Attempts);
        return cache.LastKnownGood;
    }

    // </snippet:fallback-is-an-if>

    private static Task<User> FetchAsync(CancellationToken cancellationToken) =>
        Task.FromException<User>(exception: new HttpRequestException(message: "the dependency is down"));

    private sealed record User(string Name);

    private sealed record UserCache(User LastKnownGood);
}
