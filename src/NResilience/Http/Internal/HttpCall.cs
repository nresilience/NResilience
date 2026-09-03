namespace NResilience.Http.Internal;

/// <summary>
///     One logical HTTP call: the state a retrying send needs and the send itself.
///     <para>
///         It exists because retry re-invokes the callback rather than re-awaiting a task, and an
///         <see cref="HttpRequestMessage" /> may be sent exactly once - "The request message was already
///         sent" is what the second attempt gets otherwise. Each attempt therefore builds a fresh request,
///         and the body is buffered once so that it can.
///     </para>
/// </summary>
/// <param name="request">The caller's request.</param>
/// <param name="send">The transport.</param>
/// <param name="clone">Whether each attempt gets its own copy of the request.</param>
/// <param name="concurrent">
///     Whether attempts can overlap - true for a hedged call, false for a sequential one.
///     <para>
///         It decides two things. A sequential call disposes the response an attempt supersedes,
///         because nothing else knows that a discarded result owns a socket; a hedged one must not,
///         where it would be wrong twice over: attempts overlap, so "the previous response" is not a
///         single thing, and a leg starting while a sibling's response is on its way back to the caller
///         would dispose the very response that is about to be returned. A hedged call disposes what it
///         discards in the executor instead, which is the only place that knows which answer won.
///     </para>
///     <para>
///         It also decides whether <see cref="Clone" /> takes its lock; see <c>_gate</c>.
///     </para>
/// </param>
/// <param name="deadline">
///     Tells the peer what this side is waiting for, or null when
///     <see cref="HttpResilienceOptions.PropagateDeadline" /> is off. Written per attempt, as each
///     attempt has less of the deadline remaining.
/// </param>
internal sealed class HttpCall(
    HttpRequestMessage request,
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
    bool clone,
    bool concurrent = false,
    DeadlineStamp? deadline = null)
{
    /// <summary>
    ///     Guards <see cref="Clone" /> on a hedged call. Reading an <c>HttpHeaders</c> collection parses
    ///     its values lazily and caches them, so enumerating one is a mutation - and a hedged call clones
    ///     the same request from two threads at once. A sequential call clones on one thread at a time
    ///     and skips the lock, which is what <c>concurrent</c> is for: one clone path, taken with or
    ///     without the gate.
    /// </summary>
    private readonly object _gate = new();

    private byte[]? _body;
    private HttpResponseMessage? _previous;

    /// <summary>
    ///     Buffers the request body, so that every attempt can be given its own copy of it.
    /// </summary>
    /// <remarks>
    ///     Unconditional rather than "only for content that cannot be re-read": a
    ///     <see cref="StringContent" /> is re-readable and a <see cref="StreamContent" /> is not, the
    ///     difference is not visible from the outside, and a retry that succeeds for
    ///     <see cref="StringContent" /> and throws for <see cref="StreamContent" /> is exactly the bug
    ///     that only ever shows up in production. Called only when the request can actually be
    ///     retried.
    /// </remarks>
    internal async Task BufferAsync(CancellationToken cancellationToken)
    {
        if (request.Content is { } content)
        {
            _body = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     One attempt: dispose whatever the previous attempt returned, clone the request, send it.
    /// </summary>
    /// <remarks>
    ///     The superseded response is disposed here rather than by the executor, because the executor
    ///     does not know that a discarded result owns a socket - except on the hedged path, which does
    ///     and therefore switches this off; see <c>concurrent</c>. Disposing it at the start of the
    ///     <i>next</i> attempt rather than at the end of this one is what keeps the final response -
    ///     the one handed back to the caller, whether it succeeded or is a 503 the policy ran out of
    ///     attempts on - alive.
    ///     <para>
    ///         A repeatable request is cloned for each attempt, because an <see cref="HttpRequestMessage" />
    ///         may be sent once and the body is buffered so the clone carries it. A non-repeatable request
    ///         is sent directly: it has one attempt, the caller's <see cref="HttpClient" /> already marked it
    ///         as sent, and cloning without a buffered body would lose the content.
    ///     </para>
    /// </remarks>
    internal async Task<HttpResponseMessage> SendAsync(CancellationToken cancellationToken)
    {
        if (!concurrent)
        {
            _previous?.Dispose();
            _previous = null;
        }

        var attempt = clone ? Clone() : request;

        if (deadline is { } stamp)
        {
            // Replaced rather than added: a clone carries whatever the caller wrote, and the number
            // this side is actually waiting for is the more accurate of the two.
            attempt.Headers.Remove(stamp.Header);

            if (stamp.Value() is { } left)
                attempt.Headers.TryAddWithoutValidation(stamp.Header, left);
        }

        try
        {
            var response = await send(attempt, cancellationToken).ConfigureAwait(false);

            if (!concurrent)
                _previous = response;

            return response;
        }
        finally
        {
            if (clone)
                attempt.Dispose();
        }
    }

    /// <summary>A fresh request carrying everything the original did.</summary>
    internal HttpRequestMessage Clone()
    {
        if (!concurrent)
            return CloneCore();

        lock (_gate)
        {
            return CloneCore();
        }
    }

    /// <summary>Disposes a response the caller will never see. A no-op on a hedged call, which disposes its own.</summary>
    internal void DisposeLast() => _previous?.Dispose();

    private HttpRequestMessage CloneCore()
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

        foreach (var option in request.Options)
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
}
