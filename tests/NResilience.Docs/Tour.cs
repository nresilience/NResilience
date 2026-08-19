using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NResilience.Docs;

/// <summary>The whole-API tour, on the README and on the docs landing page.</summary>
public sealed class Tour
{
    private readonly ILogger _logger = NullLogger.Instance;

    private sealed record User(string Name);

    [Fact]
    public async Task The_whole_api()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using HttpClient client = Doubles.Client(() => Doubles.Json(new User("ada")));
        var url = new Uri("https://api.example.com/users/1");
        var queue = new Doubles.Queue();
        var cache = new Doubles.Cache<User>(new User("last known good"));

        // <snippet:whole-api>
        // 1. A policy is a value. Presets are the entry point.
        var api = Resilience.Http;

        // 2. Derive with `with`. No builder, no Build(), no ordering to get right.
        var slow = Resilience.Http with { Attempts = 5, Deadline = TimeSpan.FromSeconds(20) };

        // 3. Run anything. One method, any return type, nothing to declare.
        User? user = await api.RunAsync(ct => client.GetFromJsonAsync<User>(url, ct), cancellationToken);
        HttpResponseMessage response = await api.RunAsync(ct => client.GetAsync(url, ct), cancellationToken);
        await slow.RunAsync(ct => queue.FlushAsync(ct), cancellationToken);

        // 4. Fallback is not a strategy. It is an `if`.
        CallResult<User> result = await api.TryRunAsync(ct => FetchAsync(ct), cancellationToken);
        User best = result.TryGetValue(out User? fetched) ? fetched : cache.LastKnownGood;
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

        User served = await ReadUserAsync(cache, TestContext.Current.CancellationToken);

        Assert.Equal("last known good", served.Name);
    }

    // <snippet:fallback-is-an-if>
    private async Task<User> ReadUserAsync(UserCache cache, CancellationToken cancellationToken)
    {
        CallResult<User> result = await Resilience.Http.TryRunAsync(ct => FetchAsync(ct), cancellationToken);

        if (result.TryGetValue(out User? user))
        {
            return user;
        }

        _logger.LogWarning("Serving the cached user: {Reason} after {Attempts}", result.StopReason, result.Attempts);
        return cache.LastKnownGood;
    }
    // </snippet:fallback-is-an-if>

    private static Task<User> FetchAsync(CancellationToken cancellationToken) =>
        Task.FromException<User>(new HttpRequestException("the dependency is down"));

    private sealed record UserCache(User LastKnownGood);
}
