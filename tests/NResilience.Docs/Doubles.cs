using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace NResilience.Docs;

/// <summary>
/// The scaffolding the samples stand on: a scripted transport, a queue, a cache. None of it is
/// inside a snippet — a page shows the reader's code, not the harness that makes it run here.
/// </summary>
internal static class Doubles
{
    internal static HttpClient Client(params Func<HttpResponseMessage>[] responses) =>
        new(new ScriptedTransport(responses)) { BaseAddress = new Uri("https://api.example.com") };

    internal static HttpResponseMessage Status(HttpStatusCode status) => new(status);

    internal static HttpResponseMessage Json<T>(T value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };

    internal sealed class Cache<T>(T lastKnownGood)
    {
        internal T LastKnownGood { get; } = lastKnownGood;
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

    /// <summary>A transport that serves a script of responses, then repeats the last one.</summary>
    internal sealed class ScriptedTransport(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _served;

        internal List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            int index = Math.Min(_served++, responses.Length - 1);
            return Task.FromResult(responses[index]());
        }
    }
}
