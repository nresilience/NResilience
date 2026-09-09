using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     <see cref="HttpResilienceOptions.ResumeDownloads" />: a stalled body resumes with
///     <c>Range</c>/<c>If-Range</c> when the first response made that safe, and falls back to the
///     ordinary stall - identical to <see cref="HttpResilienceOptions.ResumeDownloads" /> being off -
///     whenever it is not.
/// </summary>
public sealed class HttpResumeTests
{
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_stall_resumes_from_where_the_caller_had_read_to()
    {
        var time = new FakeTimeProvider();
        var transport = new ResumableTransport("0123456789"u8.ToArray(), stallAfter: 4, etag: "v1", acceptsRanges: true);

        using var client = Client(transport, time);
        using var response = await Head(client);

        var read = response.Content.ReadAsByteArrayAsync();

        await transport.FirstStalled;
        time.Advance(Stall);

        var bytes = await read;

        Assert.Equal("0123456789"u8.ToArray(), bytes);
        Assert.Equal(2, transport.Requests);

        // The resumed request asked for what was missing and nothing else.
        Assert.Equal(4, transport.LastRangeFrom);
    }

    [Fact]
    public async Task A_stall_raises_StreamResumed()
    {
        var time = new FakeTimeProvider();
        var transport = new ResumableTransport("0123456789"u8.ToArray(), stallAfter: 4, etag: "v1", acceptsRanges: true);
        var events = new List<CallEvent>();

        using var client = Client(transport, time, policy => policy with { OnEvent = e => events.Add(e) });
        using var response = await Head(client);

        var read = response.Content.ReadAsByteArrayAsync();
        await transport.FirstStalled;
        time.Advance(Stall);
        await read;

        Assert.Contains(events, e => e.Kind == CallEventKind.StreamResumed);
    }

    [Fact]
    public async Task A_weak_ETag_never_resumes()
    {
        var time = new FakeTimeProvider();
        var transport = new ResumableTransport("0123456789"u8.ToArray(), stallAfter: 4, etag: "v1", acceptsRanges: true, weak: true);

        using var client = Client(transport, time);
        using var response = await Head(client);

        var read = response.Content.ReadAsByteArrayAsync();
        await transport.FirstStalled;
        time.Advance(Stall);

        await Assert.ThrowsAsync<AttemptStalledException>(() => read);
        Assert.Equal(1, transport.Requests);
    }

    [Fact]
    public async Task No_AcceptRanges_never_resumes()
    {
        var time = new FakeTimeProvider();
        var transport = new ResumableTransport("0123456789"u8.ToArray(), stallAfter: 4, etag: "v1", acceptsRanges: false);

        using var client = Client(transport, time);
        using var response = await Head(client);

        var read = response.Content.ReadAsByteArrayAsync();
        await transport.FirstStalled;
        time.Advance(Stall);

        await Assert.ThrowsAsync<AttemptStalledException>(() => read);
        Assert.Equal(1, transport.Requests);
    }

    [Fact]
    public async Task A_200_where_a_206_was_expected_falls_back_to_the_stall()
    {
        var time = new FakeTimeProvider();
        var transport = new ResumableTransport("0123456789"u8.ToArray(), stallAfter: 4, etag: "v1", acceptsRanges: true, resumeReturns200: true);

        using var client = Client(transport, time);
        using var response = await Head(client);

        var read = response.Content.ReadAsByteArrayAsync();
        await transport.FirstStalled;
        time.Advance(Stall);

        // The worst case is identical to ResumeDownloads being off: the caller's read ends the same
        // way it would have, not with a spliced or restarted body.
        await Assert.ThrowsAsync<AttemptStalledException>(() => read);
    }

    [Fact]
    public async Task ResumeDownloads_off_never_resumes()
    {
        var time = new FakeTimeProvider();
        var transport = new ResumableTransport("0123456789"u8.ToArray(), stallAfter: 4, etag: "v1", acceptsRanges: true);

        using var client = Client(transport, time, resume: false);
        using var response = await Head(client);

        var read = response.Content.ReadAsByteArrayAsync();
        await transport.FirstStalled;
        time.Advance(Stall);

        await Assert.ThrowsAsync<AttemptStalledException>(() => read);
        Assert.Equal(1, transport.Requests);
    }

    private static Task<HttpResponseMessage> Head(HttpClient client) =>
        client.GetAsync(new Uri("https://api.test/thing"), HttpCompletionOption.ResponseHeadersRead);

    private static HttpClient Client(HttpMessageHandler transport, TimeProvider time, Func<Resilience, Resilience>? shape = null, bool resume = true) =>
        new(new HttpResilienceHandler(
            transport,
            shape is null ? Policy(time) : shape(Policy(time)),
            new HttpResilienceOptions { ResumeDownloads = resume })) { Timeout = Timeout.InfiniteTimeSpan };

    private static Resilience Policy(TimeProvider time) => Resilience.Http with
    {
        Time = time,
        Attempts = 1,
        Deadline = Timeout.InfiniteTimeSpan,
        AttemptTimeout = Stall,
        AttemptCeiling = null,
    };

    /// <summary>
    ///     A transport whose first response streams part of a body and then stalls, and whose second -
    ///     a <c>Range</c> request - serves the rest, or whatever the test asked it to do instead.
    /// </summary>
    private sealed class ResumableTransport(byte[] full, int stallAfter, string etag, bool acceptsRanges, bool weak = false, bool resumeReturns200 = false)
        : HttpMessageHandler
    {
        private readonly TaskCompletionSource _firstStalled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Requests { get; private set; }

        public long? LastRangeFrom { get; private set; }

        internal Task FirstStalled => _firstStalled.Task;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;

            if (request.Headers.Range is { } range)
            {
                var from = (int)range.Ranges.First().From!;
                LastRangeFrom = from;

                if (resumeReturns200)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(full) });

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(full[from..]),
                });
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingAfter(full, stallAfter, _firstStalled)),
            };

            response.Headers.ETag = new EntityTagHeaderValue($"\"{etag}\"", weak);

            if (acceptsRanges)
                response.Headers.AcceptRanges.Add("bytes");

            return Task.FromResult(response);
        }
    }

    /// <summary>Serves the first <paramref name="count" /> bytes of <paramref name="full" /> and then hangs.</summary>
    private sealed class StallingAfter(byte[] full, int count, TaskCompletionSource reached) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < count)
            {
                var take = Math.Min(buffer.Length, count - _position);
                full.AsSpan(_position, take).CopyTo(buffer.Span);
                _position += take;
                return take;
            }

            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
