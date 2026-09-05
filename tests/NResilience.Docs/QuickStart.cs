// <snippet:quick-start-usings>
using System.Net.Http.Json;
using NResilience;

// </snippet:quick-start-usings>

namespace Docs.QuickStart;

/// <summary>
///     The quick start's usings, compiled from outside the <c>NResilience</c> namespace so that the
///     page's first snippet is the whole of what a reader has to type. Everything else on the
///     on-ramp needs the same two lines and nothing more.
/// </summary>
internal static class QuickStartUsings
{
    internal static async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        using var client = HttpResilience.CreateClient();

        return await client.GetFromJsonAsync<string>(
            requestUri: new Uri(uriString: "https://api.example.com/users/1"), cancellationToken: cancellationToken);
    }
}
