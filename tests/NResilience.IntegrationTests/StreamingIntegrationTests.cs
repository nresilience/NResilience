using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace NResilience.IntegrationTests;

/// <summary>
///     A TCP server that streams lines slowly over a real loopback socket, and can drop the first
///     connections with a reset.
/// </summary>
/// <remarks>
///     The point of this fixture is what a scripted source cannot produce: the surviving attempt's
///     token is registered on a <b>real socket read</b>, and a mid-enumeration cancellation is
///     observed by the server as a broken pipe. The streaming path's contract - that the surviving
///     enumerator, its linked token source, and the disarmed timer all stay alive for as long as
///     the consumer enumerates - is exercised against the runtime's own I/O cancellation rather
///     than against a double.
/// </remarks>
public sealed class LoopbackStreamServer : IAsyncDisposable
{
    private readonly TimeSpan _delay;
    private readonly int _dropFirst;
    private readonly int _lines;
    private readonly TcpListener _listener;
    private readonly Task _pump;
    private readonly CancellationTokenSource _shutdown = new();
    private long _closed;

    private long _connections;
    private long _dropped;
    private long _served;

    private LoopbackStreamServer(TcpListener listener, int lines, TimeSpan delay, int dropFirst)
    {
        _listener = listener;
        _lines = lines;
        _delay = delay;
        _dropFirst = dropFirst;
        _pump = Task.Run(() => PumpAsync(_shutdown.Token));
    }

    /// <summary>How many connections reached the server.</summary>
    public long Connections => Volatile.Read(ref _connections);

    /// <summary>How many connections the server served a stream on.</summary>
    public long Served => Volatile.Read(ref _served);

    /// <summary>How many connections the server reset before serving anything.</summary>
    public long Dropped => Volatile.Read(ref _dropped);

    /// <summary>How many served connections have closed - the observable a mid-stream cancellation produces.</summary>
    public long Closed => Volatile.Read(ref _closed);

    public int Port => ((IPEndPoint)_listener.Server.LocalEndPoint!).Port;

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The pump ends when the listener stops accepting.
        }
    }

    public static async Task<LoopbackStreamServer> StartAsync(int lines, TimeSpan delay, int dropFirst = 0)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LoopbackStreamServer(listener, lines, delay, dropFirst);
    }

    /// <summary>
    ///     A source over a real connection: connects, sends the one request byte that asks the
    ///     server for the stream, then yields one chunk per read. The token is the attempt's own -
    ///     the surviving one - so every pull after the first registers on a live socket and a
    ///     mid-stream cancellation breaks the pipe.
    /// </summary>
    public async IAsyncEnumerable<string> ConnectAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Port, cancellationToken).ConfigureAwait(false);

        var stream = client.GetStream();

        // The request that starts the stream. Real sources have one - a gRPC call, an HTTP GET with
        // ResponseHeadersRead - and it belongs inside the source so every attempt sends it afresh.
        await stream.WriteAsync("GO\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);

        var buffer = new byte[256];

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read == 0)
                yield break;

            yield return Encoding.UTF8.GetString(buffer, 0, read);
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var connection = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            connection++;
            Interlocked.Increment(ref _connections);

            // A dropped connection has to be a <i>reset</i>, not a graceful close: a clean FIN makes
            // the client's first read return zero bytes, which is an empty source - a success, by
            // the streaming contract - and no retry would follow. A reset makes the read throw,
            // which is the transient failure this whole library exists to classify.
            //
            // The close is on the underlying Socket with a zero timeout, never TcpClient.Close():
            // when GetStream() was never called, TcpClient.Dispose issues a graceful
            // InternalShutdown(Both) - a FIN - before closing, and on Linux the client's pending
            // read usually completes on that FIN instead of the reset, which turns the drop into
            // an empty source. Socket.Close(0) is the abortive path: no shutdown, a forced
            // linger-0 close, and an unambiguous RST on every OS.
            if (connection <= _dropFirst)
            {
                client.Client.LingerState = new LingerOption(true, 0);
                client.Client.Close(0);
                Interlocked.Increment(ref _dropped);
                continue;
            }

            Interlocked.Increment(ref _served);
            _ = ServeAsync(client, cancellationToken);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[1];

            // The client's request, read fully through its newline before writing, so the write
            // loop starts only once the client has actually asked. Fully matters: a close with
            // request bytes still unread in the receive buffer is a reset on Windows rather than a
            // graceful FIN, and the winning attempt's next read would throw instead of completing
            // the stream. Draining to the newline leaves nothing behind on every OS.
            int read;

            do
            {
                read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            } while (read > 0 && buffer[0] != (byte)'\n');

            for (var i = 0; i < _lines; i++)
            {
                var line = Encoding.UTF8.GetBytes($"line {i}\n");
                await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
                await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // A client going away mid-stream is the mid-enumeration-cancellation test, not a
            // failure of the server.
        }
        finally
        {
            Interlocked.Increment(ref _closed);
            client.Dispose();
        }
    }
}

/// <summary>
///     The streaming path over real I/O: the surviving attempt's token drives a real socket, so
///     mid-enumeration cancellation breaks a real pipe and a lost first attempt is a real reset.
/// </summary>
public sealed class StreamingIntegrationTests
{
    private static Resilience Policy() => Resilience.Default with
    {
        Backoff = Backoff.None,
        Deadline = Timeout.InfiniteTimeSpan,
        AttemptTimeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>
    ///     A stream that survives to the end: every line arrives, the enumeration drains until the
    ///     server closes, and the server sees exactly one connection - no attempt was abandoned.
    /// </summary>
    [Fact]
    public async Task A_stream_drains_fully_over_a_real_socket()
    {
        await using var server = await LoopbackStreamServer.StartAsync(5, TimeSpan.FromMilliseconds(10));

        var text = new StringBuilder();

        await foreach (var chunk in Policy().RunAsync(server.ConnectAsync))
        {
            text.Append(chunk);
        }

        Assert.Contains("line 0", text.ToString(), StringComparison.Ordinal);
        Assert.Contains("line 4", text.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, server.Connections);
        Assert.Equal(1, server.Served);
    }

    /// <summary>
    ///     Mid-enumeration cancellation over real I/O. The consumer cancels after the first chunk;
    ///     the surviving attempt's token - still alive, still registered on the socket - breaks
    ///     the connection, and the server observes it. This is the contract the surviving-source
    ///     lifetime exists to keep: the loop is long gone by this point, and its teardown must not
    ///     have disposed the token the socket still holds a registration on.
    /// </summary>
    [Fact]
    public async Task Mid_enumeration_cancellation_breaks_the_real_connection()
    {
        await using var server = await LoopbackStreamServer.StartAsync(100, TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource();
        var chunks = 0;

        try
        {
            await foreach (var _ in Policy().RunAsync(server.ConnectAsync, cts.Token))
            {
                chunks++;

                // Cancel while elements are still arriving - the middle of the enumeration, after
                // the first element is in hand and the loop is long gone.
                if (chunks == 1)
                    await cts.CancelAsync();
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // The caller's cancellation, and never a failure.
        }

        Assert.True(chunks >= 1);

        // The server observed the broken pipe: the socket registered on the surviving token tore
        // down, which is the whole claim. The serve loop's finally is the deterministic signal, so
        // poll briefly for the disposal to land.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        var observed = false;

        while (DateTime.UtcNow < deadline)
        {
            if (server.Closed > 0)
            {
                observed = true;
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        Assert.True(observed, "The server did not observe the mid-stream disconnect.");
    }

    /// <summary>
    ///     Retry before the first element over real I/O: the server resets the first connection, so
    ///     the first attempt's read throws and the policy pulls a fresh connection for the second -
    ///     which is what the abandoned-enumerator disposal exists to make safe (the losing
    ///     connection is torn down before the winner is handed over).
    /// </summary>
    [Fact]
    public async Task A_reset_first_connection_is_retried_over_a_real_socket()
    {
        await using var server =
            await LoopbackStreamServer.StartAsync(3, TimeSpan.FromMilliseconds(10), 1);

        var text = new StringBuilder();

        await foreach (var chunk in Policy().RunAsync(server.ConnectAsync))
        {
            text.Append(chunk);
        }

        Assert.Contains("line 0", text.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, server.Connections);
        Assert.Equal(1, server.Dropped);
        Assert.Equal(1, server.Served);
    }
}
