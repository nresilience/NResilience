using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NResilience.Docs;

/// <summary>The whole-API tour, on the README and on the docs landing page.</summary>
public sealed class Tour
{
    private readonly ILogger _logger = NullLogger.Instance;

    [Fact]
    public async Task The_whole_api()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = Doubles.Client(() => Doubles.Json(new User("ada")));
        var url = new Uri("https://api.example.com/users/1");
        var queue = new Doubles.Queue();
        var cache = new Doubles.Cache<User>(new User("last known good"));

        // <snippet:whole-api>
        // 1. Start from a preset. `Resilience.Http` retries and times out an HTTP call out of the box.
        var api = Resilience.Http;

        // 2. Change one setting, keep the rest: `with` copies everything you did not mention.
        var slow = Resilience.Http with { Attempts = 5, Deadline = TimeSpan.FromSeconds(20) };

        // 3. Run any callback through one method. The token handed to your work is the attempt's own.
        var user = await api.RunAsync(attempt => client.GetFromJsonAsync<User>(url, attempt),
            cancellationToken);

        var response = await api.RunAsync(attempt => client.GetAsync(url, attempt), cancellationToken);
        await slow.RunAsync(attempt => queue.FlushAsync(attempt), cancellationToken);

        // 4. Want the outcome without an exception? `TryRunAsync` hands it back to branch on.
        var result = await api.TryRunAsync(attempt => FetchAsync(attempt), cancellationToken);
        var best = result.TryGetValue(out var fetched) ? fetched : cache.LastKnownGood;

        // </snippet:whole-api>

        response.Dispose();
        Assert.Equal("ada", user?.Name);
        Assert.Equal("last known good", best.Name);
        Assert.Equal(1, queue.Flushes);
        Assert.Equal(5, slow.Attempts);
    }

    [Fact]
    public async Task A_fallback_is_an_if()
    {
        var cache = new UserCache(new User("last known good"));

        var served = await ReadUserAsync(cache, TestContext.Current.CancellationToken);

        Assert.Equal("last known good", served.Name);
    }

    // <snippet:fallback-is-an-if>
    private async Task<User> ReadUserAsync(UserCache cache, CancellationToken cancellationToken)
    {
        var result = await Resilience.Http.TryRunAsync(attempt => FetchAsync(attempt), cancellationToken);

        if (result.TryGetValue(out var user))
            return user;

        _logger.LogWarning("Serving the cached user: {Reason} after {Attempts}", result.StopReason, result.Attempts);
        return cache.LastKnownGood;
    }

    // </snippet:fallback-is-an-if>

    private static Task<User> FetchAsync(CancellationToken cancellationToken) =>
        Task.FromException<User>(new HttpRequestException("the dependency is down"));

    private sealed record User(string Name);

    private sealed record UserCache(User LastKnownGood);
}
