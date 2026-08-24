using System.Net.Http;

namespace NResilience.Http.Internal;

/// <summary>
/// One logical HTTP call: the state a retrying send needs and the send itself.
/// <para>
/// It exists because retry re-invokes the callback rather than re-awaiting a task, and an
/// <see cref="HttpRequestMessage"/> may be sent exactly once - "The request message was already
/// sent" is what the second attempt gets otherwise. Each attempt therefore builds a fresh request,
/// and the body is buffered once so that it can.
/// </para>
/// </summary>
internal sealed class HttpCall(
    HttpRequestMessage request,
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
    bool clone)
{
    private byte[]? _body;
    private HttpResponseMessage? _previous;

    /// <summary>
    /// Buffers the request body, so that every attempt can be given its own copy of it.
    /// </summary>
    /// <remarks>
    /// Unconditional rather than "only for content that cannot be re-read": a
    /// <see cref="StringContent"/> is re-readable and a <see cref="StreamContent"/> is not, the
    /// difference is not visible from the outside, and a retry that succeeds for
    /// <see cref="StringContent"/> and throws for <see cref="StreamContent"/> is exactly the bug
    /// that only ever shows up in production. Called only when the request can actually be
    /// retried.
    /// </remarks>
    internal async Task BufferAsync(CancellationToken cancellationToken)
    {
        if (request.Content is { } content)
        {
#if NET8_0_OR_GREATER
            _body = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
#else
            _body = await content.ReadAsByteArrayAsync().ConfigureAwait(false);
#endif
        }
    }

    /// <summary>
    /// One attempt: dispose whatever the previous attempt returned, clone the request, send it.
    /// </summary>
    /// <remarks>
    /// The superseded response is disposed here rather than by the executor, because the executor
    /// does not know that a discarded result owns a socket. Disposing it at the start of the
    /// <i>next</i> attempt rather than at the end of this one is what keeps the final response -
    /// the one handed back to the caller, whether it succeeded or is a 503 the policy ran out of
    /// attempts on - alive.
    /// <para>
    /// A repeatable request is cloned for each attempt, because an <see cref="HttpRequestMessage"/>
    /// may be sent once and the body is buffered so the clone carries it. A non-repeatable request
    /// is sent directly: it has one attempt, the caller's <see cref="HttpClient"/> already marked it
    /// as sent, and cloning without a buffered body would lose the content.
    /// </para>
    /// </remarks>
    internal async Task<HttpResponseMessage> SendAsync(CancellationToken cancellationToken)
    {
        _previous?.Dispose();
        _previous = null;

        var attempt = clone ? Clone() : request;
        try
        {
            var response = await send(attempt, cancellationToken).ConfigureAwait(false);
            _previous = response;
            return response;
        }
        finally
        {
            if (clone)
            {
                attempt.Dispose();
            }
        }
    }

    /// <summary>A fresh request carrying everything the original did.</summary>
    internal HttpRequestMessage Clone()
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in (IDictionary<string, object?>)request.Options)
        {
            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
        }

        if (_body is { } body && request.Content is { } original)
        {
            var content = new ByteArrayContent(body);

            // ByteArrayContent invents a Content-Length and nothing else; the original's headers -
            // Content-Type above all - are the ones the server was going to be told about.
            content.Headers.Clear();
            foreach (var header in original.Headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = content;
        }

        return clone;
    }

    /// <summary>Disposes a response the caller will never see.</summary>
    internal void DisposeLast() => _previous?.Dispose();
}
