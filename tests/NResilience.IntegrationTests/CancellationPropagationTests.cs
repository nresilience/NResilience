using System.Net;
using NResilience.Http;

namespace NResilience.IntegrationTests;

/// <summary>
///     Real cancellation reaching a real socket.
///     <para>
///         The behavioural suite's cancellation tests use scripted callbacks that cancel on cue. A
///         scripted <c>HttpMessageHandler</c> cannot reproduce the one thing that matters here: that
///         the cancellation token the policy hands to an attempt is the one the socket actually
///         registers on, and that tearing the connection down is what stops the read. These tests do
///         that over a real loopback socket and the real <c>SocketsHttpHandler</c>.
///     </para>
/// </summary>
public sealed class CancellationPropagationTests
{
    /// <summary>
    ///     The caller's token aborts a slow attempt. The server holds the response for 10s; the
    ///     caller cancels after 100ms. The contract: caller cancellation propagates as
    ///     <see cref="OperationCanceledException" /> (not <see cref="CallRejectedException" />), and the
    ///     socket the server holds is torn down - the server observes the broken pipe.
    /// </summary>
    [Fact]
    public async Task The_callers_token_aborts_a_slow_attempt()
    {
        var serverSawCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await LoopbackHttp.StartAsync((_, ct) =>
        {
            // Hold the response until the client cancels or the test times out. The token is the
            // per-request one linked to the server shutdown, so a client disconnect cancels it.
            try
            {
                return Task.FromResult(new LoopbackResponse(HttpStatusCode.OK, Delay: TimeSpan.FromSeconds(10)));
            }
            finally
            {
                ct.Register(() => serverSawCancellation.TrySetResult());
            }
        });

        using var client = HttpResilience.CreateClient(Resilience.Http with
        {
            Backoff = Backoff.None,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Deadline = Timeout.InfiniteTimeSpan,
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.GetAsync(server.BaseUri, cts.Token));

        // The server observes the broken connection - the socket is torn down, not held.
        await Task.WhenAny(serverSawCancellation.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.True(serverSawCancellation.Task.IsCompleted, "The server did not observe the cancellation.");
    }

    /// <summary>
    ///     The attempt timeout cancels the socket read. The policy has a 200ms attempt ceiling; the
    ///     server holds for 30s. The contract: <see cref="AttemptTimeoutException" /> surfaces, and
    ///     the server's connection is closed by the client.
    /// </summary>
    [Fact]
    public async Task The_attempt_timeout_cancels_the_socket_read()
    {
        var serverSawDisconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await LoopbackHttp.StartAsync((_, ct) =>
        {
            ct.Register(() => serverSawDisconnect.TrySetResult());
            return Task.FromResult(new LoopbackResponse(HttpStatusCode.OK, Delay: TimeSpan.FromSeconds(30)));
        });

        using var client = HttpResilience.CreateClient(Resilience.Http with
        {
            Backoff = Backoff.None,
            Attempts = 1,
            AttemptTimeout = TimeSpan.FromMilliseconds(200),
            Deadline = Timeout.InfiniteTimeSpan,
        });

        // The attempt timeout is real, so the test waits for it. 200ms is short enough to keep the
        // test fast and long enough for the cancellation to propagate through SocketsHttpHandler
        // and tear the connection down.
        await Assert.ThrowsAsync<AttemptTimeoutException>(async () => await client.GetAsync(server.BaseUri));

        // The client cancels the request, which tears the socket down. The server's disconnect
        // watcher fires the per-request token. Give it real time to propagate.
        await Task.WhenAny(serverSawDisconnect.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.True(serverSawDisconnect.Task.IsCompleted, "The server did not observe the disconnect.");
    }

    /// <summary>
    ///     The deadline cancels the in-flight attempt. The policy has a 200ms deadline and no
    ///     per-attempt ceiling; the server holds for 30s. The contract:
    ///     <see cref="DeadlineExceededException" /> is the terminal outcome, and the server's
    ///     connection is torn down.
    /// </summary>
    [Fact]
    public async Task The_deadline_cancels_the_in_flight_attempt()
    {
        var serverSawDisconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await LoopbackHttp.StartAsync((_, ct) =>
        {
            ct.Register(() => serverSawDisconnect.TrySetResult());
            return Task.FromResult(new LoopbackResponse(HttpStatusCode.OK, Delay: TimeSpan.FromSeconds(30)));
        });

        using var client = HttpResilience.CreateClient(Resilience.Http with
        {
            Backoff = Backoff.None,
            Attempts = 3,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Deadline = TimeSpan.FromMilliseconds(200),
        });

        await Assert.ThrowsAsync<DeadlineExceededException>(async () => await client.GetAsync(server.BaseUri));

        await Task.WhenAny(serverSawDisconnect.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.True(serverSawDisconnect.Task.IsCompleted, "The server did not observe the disconnect.");
    }

    /// <summary>
    ///     A response superseded by a retry is disposed. The server serves a 503 with a body, then a
    ///     200. The contract: the first response's content is disposed - a leaked response content is
    ///     the resource bug a scripted double can catch but a real socket makes real. This is the
    ///     real-IO version of <c>HttpHandlerTests.A_cancelled_call_disposes_the_response_nobody_receives</c>.
    /// </summary>
    [Fact]
    public async Task A_response_superseded_by_a_retry_is_disposed()
    {
        await using var server = await LoopbackHttp.StartAsync(
            new LoopbackResponse(HttpStatusCode.ServiceUnavailable, "fail"u8.ToArray()),
            new LoopbackResponse(HttpStatusCode.OK, "ok"u8.ToArray()));

        // The tracking handler wraps the real transport and observes the lifetime of the response
        // content the transport produces.
        var innerTracking = new TrackingHandler(new SocketsHttpHandler());

        using var resilience = new ResilienceHandler(innerTracking, Resilience.Http with
        {
            Backoff = Backoff.None,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Deadline = Timeout.InfiniteTimeSpan,
        });

        using var client = new HttpClient(resilience, true);

        using var response = await client.GetAsync(server.BaseUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, server.RequestCount);

        // The first response's content was disposed when the retry superseded it. Give the
        // disposal a moment to propagate - the handler disposes the superseded response on the
        // retry path, which is synchronous, but the assertion is stated against the tracked flag.
        Assert.True(innerTracking.FirstContentDisposed, "The superseded response content was not disposed.");
    }

    /// <summary>
    ///     A handler that wraps an inner handler and tracks whether the response contents it produced
    ///     were disposed - the real-IO version of <c>HttpHandlerTests</c>' <c>TrackedContent</c>.
    ///     Tracks the first content (the one a retry supersedes).
    /// </summary>
    private sealed class TrackingHandler : DelegatingHandler
    {
        internal bool FirstContentDisposed;
        private bool _seenFirst;

        public TrackingHandler(HttpMessageHandler inner) : base(inner)
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);

            if (response.Content is { } content)
            {
                var wrapped = new TrackingContent(content);
                response.Content = wrapped;

                if (!_seenFirst)
                {
                    _seenFirst = true;
                    wrapped.Disposed += () => FirstContentDisposed = true;
                }
            }

            return response;
        }
    }

    /// <summary>Content that reports when it is disposed.</summary>
    private sealed class TrackingContent : HttpContent
    {
        private readonly HttpContent _inner;
        private Action? _disposed;

        internal TrackingContent(HttpContent inner)
        {
            _inner = inner;

            foreach (var header in inner.Headers)
            {
                Headers.Add(header.Key, header.Value);
            }
        }

        internal event Action Disposed
        {
            add => _disposed += value;
            remove => _disposed -= value;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await _inner.CopyToAsync(stream, context);
        }

        protected override bool TryComputeLength(out long length)
        {
            if (_inner.Headers.ContentLength is { } value)
            {
                length = value;
                return true;
            }

            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _disposed?.Invoke();
            }

            base.Dispose(disposing);
        }
    }
}
