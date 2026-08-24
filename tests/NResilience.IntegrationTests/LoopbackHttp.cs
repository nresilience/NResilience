using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NResilience.IntegrationTests;

/// <summary>
///     A minimal HTTP/1.1 server over a loopback TCP socket - the HTTP analogue of
///     <c>LoopbackEcho</c>. The client side uses the real <c>SocketsHttpHandler</c>, so a test
///     exercises the real connection pool, the real cancellation registration on a socket, and
///     real response-stream disposal - the things a scripted <c>HttpMessageHandler</c> cannot
///     produce.
/// </summary>
/// <remarks>
///     Framing is intentionally minimal: one request line, headers up to the blank line, an
///     optional body read by <c>Content-Length</c>, and a <c>Connection: close</c> response so the
///     next request opens a fresh connection. <c>SocketsHttpHandler</c> handles that fine, and the
///     framing stays simple enough to read.
/// </remarks>
public sealed class LoopbackHttp : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly TcpListener _listener;
    private readonly Task _pump;
    private readonly List<LoopbackRequest> _requests = [];
    private readonly Func<LoopbackRequest, CancellationToken, Task<LoopbackResponse>> _respond;
    private readonly CancellationTokenSource _shutdown = new();

    private LoopbackHttp(TcpListener listener, Func<LoopbackRequest, CancellationToken, Task<LoopbackResponse>> respond)
    {
        _listener = listener;
        _respond = respond;
        _pump = Task.Run(() => PumpAsync(_shutdown.Token));
    }

    /// <summary>The port the server is listening on. Assigned by the OS.</summary>
    public int Port => ((IPEndPoint)_listener.Server.LocalEndPoint!).Port;

    /// <summary>The base URI clients should point at.</summary>
    public Uri BaseUri => new($"http://127.0.0.1:{Port}/", UriKind.Absolute);

    /// <summary>Every request that reached the server, in arrival order.</summary>
    public IReadOnlyList<LoopbackRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>How many requests reached the server.</summary>
    public int RequestCount
    {
        get
        {
            lock (_gate)
            {
                return _requests.Count;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);

        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
        }

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _shutdown.Dispose();
    }

    /// <summary>Starts a server that calls <paramref name="respond" /> for each request.</summary>
    public static async Task<LoopbackHttp> StartAsync(Func<LoopbackRequest, CancellationToken, Task<LoopbackResponse>> respond)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LoopbackHttp(listener, respond);
    }

    /// <summary>Starts a server that serves <paramref name="responses" /> in order, repeating the last.</summary>
    public static Task<LoopbackHttp> StartAsync(params LoopbackResponse[] responses)
    {
        var index = -1;

        return StartAsync((_, ct) =>
        {
            var i = Interlocked.Increment(ref index);
            var response = responses[Math.Min(i, responses.Length - 1)];
            return Task.FromResult(response);
        });
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            // Each connection is handled on its own task so the pump can keep accepting. The
            // connection's own token is the server shutdown token; per-request cancellation is
            // the token the handler returns from RespondAsync.
            _ = Task.Run(() => HandleAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken shutdownToken)
    {
        try
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var request = await ReadRequestAsync(stream, shutdownToken).ConfigureAwait(false);

                if (request is null)
                    return;

                lock (_gate)
                {
                    _requests.Add(request);
                }

                // The per-request token is linked to the shutdown so a slow handler does not outlive
                // the server. It is also cancelled when the client disconnects: a background read on
                // the stream completes with 0 bytes or throws when the client tears the connection
                // down, which is how a handler that is holding a response (e.g. in Task.Delay) finds
                // out that nobody is listening any more.
                using var perRequest = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
                using var disconnectWatcher = WatchDisconnectAsync(stream, perRequest);

                LoopbackResponse response;

                try
                {
                    response = await _respond(request, perRequest.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                await WriteResponseAsync(stream, response, perRequest.Token).ConfigureAwait(false);
            }
        }
        catch (SocketException)
        {
            // The client tore the connection down - expected when a test cancels.
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // Broken pipe - same cause.
        }
    }

    /// <summary>
    ///     Reads from the stream in the background. A read that returns 0 or throws means the client
    ///     has disconnected, so the per-request token is cancelled - which is how a handler holding a
    ///     response learns that the caller went away.
    /// </summary>
    private static Task WatchDisconnectAsync(NetworkStream stream, CancellationTokenSource perRequest)
    {
        if (perRequest.Token.IsCancellationRequested)
            return Task.CompletedTask;

        return Task.Run(async () =>
        {
            var buffer = new byte[1];

            try
            {
                var read = await stream.ReadAsync(buffer, perRequest.Token).ConfigureAwait(false);

                if (read == 0)
                    perRequest.Cancel();
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
                perRequest.Cancel();
            }
            catch (SocketException)
            {
                perRequest.Cancel();
            }
        }, perRequest.Token);
    }

    private static async Task<LoopbackRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        // Read the request line and headers up to the blank line. HTTP/1.1 headers are ASCII;
        // the body is read separately by Content-Length.
        var headerBuffer = new StringBuilder();
        var readBuffer = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);

            if (read == 0)
                return null;

            headerBuffer.Append((char)readBuffer[0]);

            if (headerBuffer.Length >= 4 && headerBuffer.ToString(headerBuffer.Length - 4, 4) == "\r\n\r\n")
                break;
        }

        var headerText = headerBuffer.ToString();
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length == 0)
            return null;

        var requestLine = lines[0].Split(' ');

        if (requestLine.Length < 3)
            return null;

        var method = requestLine[0];
        var path = requestLine[1];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');

            if (colon > 0)
            {
                var name = lines[i][..colon].Trim();
                var value = lines[i][(colon + 1)..].Trim();
                headers[name] = value;
            }
        }

        byte[]? body = null;

        if (headers.TryGetValue("Content-Length", out var lengthText) && int.TryParse(lengthText, out var contentLength) && contentLength > 0)
        {
            body = new byte[contentLength];
            var offset = 0;

            while (offset < contentLength)
            {
                var read = await stream.ReadAsync(body.AsMemory(offset, contentLength - offset), cancellationToken).ConfigureAwait(false);

                if (read == 0)
                    break;

                offset += read;
            }
        }

        return new LoopbackRequest(method, path, headers, body);
    }

    private static async Task WriteResponseAsync(NetworkStream stream, LoopbackResponse response, CancellationToken cancellationToken)
    {
        if (response.Delay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(response.Delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        var body = response.Body ?? [];
        var headerBuilder = new StringBuilder();
        headerBuilder.Append($"HTTP/1.1 {(int)response.StatusCode} {response.StatusCode}\r\n");
        headerBuilder.Append($"Content-Length: {body.Length}\r\n");
        headerBuilder.Append("Connection: close\r\n");

        if (response.Headers is not null)
        {
            foreach (var header in response.Headers)
            {
                headerBuilder.Append($"{header.Key}: {header.Value}\r\n");
            }
        }

        headerBuilder.Append("\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(headerBuilder.ToString());
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);

        if (body.Length > 0)
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>A request that reached the loopback server.</summary>
public sealed record LoopbackRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, byte[]? Body);

/// <summary>A response the loopback server sends back.</summary>
public sealed record LoopbackResponse(
    HttpStatusCode StatusCode,
    byte[]? Body = null,
    TimeSpan Delay = default,
    IReadOnlyDictionary<string, string>? Headers = null)
{
    /// <summary>Convenience overload for a body-less response with no headers.</summary>
    public LoopbackResponse(HttpStatusCode statusCode) : this(statusCode, null)
    {
    }

    /// <summary>Convenience: a plain text body.</summary>
    public static LoopbackResponse Text(HttpStatusCode statusCode, string body, TimeSpan delay = default) =>
        new(statusCode, Encoding.UTF8.GetBytes(body), delay, new Dictionary<string, string> { ["Content-Type"] = "text/plain" });

    /// <summary>Convenience: a response with a <c>Retry-After</c> header, in seconds.</summary>
    public static LoopbackResponse WithRetryAfter(HttpStatusCode statusCode, int seconds, byte[]? body = null) =>
        new(statusCode, body, default, new Dictionary<string, string> { ["Retry-After"] = seconds.ToString() });
}
