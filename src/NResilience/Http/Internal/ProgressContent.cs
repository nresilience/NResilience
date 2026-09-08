using System.Net;

namespace NResilience.Internal;

/// <summary>
///     A response body whose reads are bounded by <see cref="ProgressStream" />. Substituted for the
///     transport's own content after the executor has returned, which is the only place it can be:
///     the body is what the attempt did not cover.
/// </summary>
/// <remarks>
///     <para>
///         An <see cref="HttpContent" /> subclass rather than a <see cref="StreamContent" /> over the
///         wrapped stream, for one reason: <see cref="StreamContent" /> needs its stream at
///         construction, and getting it means awaiting
///         <see cref="HttpContent.ReadAsStreamAsync()" /> on the transport's content before the caller
///         has asked for a single byte. That would start the body read inside the handler, which is
///         what <see cref="HttpResilienceOptions.BufferResponses" /> is for and is not what this is.
///         Here the wrap is lazy and the caller's first read is still the first read.
///     </para>
///     <para>
///         The headers are copied rather than shared, because <see cref="HttpContent.Headers" /> is
///         read by callers who never touch the body - <c>Content-Type</c> above all - and a substituted
///         content that lost them would break far more than it fixed.
///     </para>
/// </remarks>
internal sealed class ProgressContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly Action<CallEvent>? _onEvent;
    private readonly string? _policyName;
    private readonly TimeSpan _stall;
    private readonly TimeProvider _time;

    /// <summary>Wraps a body.</summary>
    /// <param name="inner">The transport's content.</param>
    /// <param name="stall">How long one read may be outstanding.</param>
    /// <param name="time">The clock.</param>
    /// <param name="policyName">The policy's name, for the event.</param>
    /// <param name="onEvent">The policy's listener.</param>
    internal ProgressContent(HttpContent inner, TimeSpan stall, TimeProvider time, string? policyName, Action<CallEvent>? onEvent)
    {
        _inner = inner;
        _stall = stall;
        _time = time;
        _policyName = policyName;
        _onEvent = onEvent;

        foreach (var header in inner.Headers)
        {
            Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    /// <inheritdoc />
    protected override async Task<Stream> CreateContentReadStreamAsync()
    {
        var body = await _inner.ReadAsStreamAsync().ConfigureAwait(false);
        return new ProgressStream(body, _stall, _time, _policyName, _onEvent);
    }

    /// <inheritdoc />
    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
        var body = await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new ProgressStream(body, _stall, _time, _policyName, _onEvent);
    }

    /// <inheritdoc />
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    /// <inheritdoc />
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        // Reached by ReadAsStringAsync, ReadAsByteArrayAsync and CopyToAsync. The wrapped stream is
        // disposed here rather than left to the content's own teardown, because this method may be
        // called more than once and each call creates its own.
        var body = await CreateContentReadStreamAsync(cancellationToken).ConfigureAwait(false);

        await using (body.ConfigureAwait(false))
        {
            await body.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override bool TryComputeLength(out long length)
    {
        if (_inner.Headers.ContentLength is { } declared)
        {
            length = declared;
            return true;
        }

        length = 0;
        return false;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();

        base.Dispose(disposing);
    }
}
