using System.Net;
using System.Text;
using System.Text.Json;

namespace NResilience.Docs;

/// <summary>
///     The scaffolding the samples stand on: a scripted transport, a queue, a cache. None of it is
///     inside a snippet - a page shows the reader's code, not the harness that makes it run here.
/// </summary>
internal static class Doubles
{
    internal static HttpResponseMessage Status(HttpStatusCode status) => new(statusCode: status);

    internal static HttpResponseMessage Json<T>(T value) =>
        new(statusCode: HttpStatusCode.OK)
        {
            Content = new StringContent(content: JsonSerializer.Serialize(value: value), encoding: Encoding.UTF8, mediaType: "application/json"),
        };

    internal sealed class Cache<T>(T lastKnownGood)
    {
        internal T LastKnownGood { get; } = lastKnownGood;
    }

    /// <summary>A read that drops its connection once, then answers.</summary>
    internal sealed class Database(string name)
    {
        internal int Reads { get; private set; }

        internal Task<string> ReadNameAsync(int id, CancellationToken cancellationToken) =>
            Reads++ == 0
                ? Task.FromException<string>(exception: new IOException(message: $"the connection dropped reading {id}"))
                : Task.FromResult(result: name);
    }

    internal sealed class Queue
    {
        internal int Flushes { get; private set; }

        internal Task FlushAsync(CancellationToken cancellationToken)
        {
            Flushes++;
            return Task.CompletedTask;
        }
    }
}
