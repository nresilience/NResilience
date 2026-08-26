using System.Net;
using System.Net.Sockets;

namespace NResilience.Probes;

/// <summary>
///     A real loopback TCP round trip used to cross-check the <c>Task.Yield</c> gate.
///     <c>Task.Yield</c> is the appropriate primitive for a gate because it suspends
///     deterministically on every call, without I/O completion ports, synchronization
///     with a second thread, or variance. However, it is not I/O, and the design's performance
///     argument concerns the path real I/O takes. If a fused loop and a composed pipeline
///     disagree on the ratio over a real socket, the gate is measuring an artifact.
///     The original measurement reported 112 B per frame over a loopback socket, which is the
///     published figure this probe exists to verify.
///     The callback passes its cancellation token to the socket calls, mimicking real code.
///     A wrapper that provides a cancellable token incurs the cost of the socket's
///     registration, which is a genuine cost rather than a harness artifact.
///     One round trip suspends exactly once, deterministically, on every platform. That is the
///     property the cross-check rests on and it is not free: see <see cref="RoundTripAsync" /> for
///     why the receive is issued before the send, and <see cref="SynchronousReceives" /> for the
///     counter that proves it held.
/// </summary>
public sealed class LoopbackEcho : IAsyncDisposable
{
    private readonly Socket _client;
    private readonly Socket _listener;
    private readonly Task _pump;
    private readonly byte[] _receive = new byte[1];
    private readonly byte[] _send = [1];
    private readonly Socket _server;
    private readonly CancellationTokenSource _shutdown = new();
    private long _roundTrips;
    private long _synchronousReceives;

    private LoopbackEcho(Socket listener, Socket client, Socket server)
    {
        _listener = listener;
        _client = client;
        _server = server;
        _pump = Task.Run(() => EchoAsync(server, _shutdown.Token));
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _client.Dispose();
        _server.Dispose();
        _listener.Dispose();
        _shutdown.Dispose();
    }

    public static async Task<LoopbackEcho> StartAsync()
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var accepting = listener.AcceptAsync();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!).ConfigureAwait(false);
        var server = await accepting.ConfigureAwait(false);

        client.NoDelay = true;
        server.NoDelay = true;

        return new LoopbackEcho(listener, client, server);
    }

    /// <summary>Round trips one byte, suspending exactly once.</summary>
    /// <remarks>
    ///     The receive is issued <i>before</i> the send, and that ordering is the whole point.
    ///     Each round trip sends one byte and receives one byte, so at the top of a call the
    ///     connection is provably empty: issuing the receive first means no byte can yet be in
    ///     flight and the operation must pend. The send that follows completes synchronously out
    ///     of the socket's buffer on every platform.
    ///     Written the natural way round - send, then receive - the receive's completion is a race
    ///     against the echo pump, and on a platform whose loopback reply frequently beats the
    ///     client back to <c>ReceiveAsync</c> the operation completes synchronously and allocates
    ///     no state machine at all. That does not make the arms wrong individually; it makes them
    ///     incomparable, because the fused executor's per-attempt linked source is paid whether
    ///     the callback suspends or not while a composed pipeline's per-attempt boxes are not. The
    ///     measured advantage then tracks the platform's synchronous-completion rate rather than
    ///     the design, and it drifts with machine load.
    /// </remarks>
    public async Task<int> RoundTripAsync(CancellationToken cancellationToken)
    {
        var receiving = _client.ReceiveAsync(_receive, SocketFlags.None, cancellationToken);

        Interlocked.Increment(ref _roundTrips);

        if (receiving.IsCompleted)
            Interlocked.Increment(ref _synchronousReceives);

        await _client.SendAsync(_send, SocketFlags.None, cancellationToken).ConfigureAwait(false);
        return await receiving.ConfigureAwait(false);
    }

    /// <summary>Round trips issued since the last <see cref="ResetCounters" />.</summary>
    public long RoundTrips => Interlocked.Read(ref _roundTrips);

    /// <summary>
    ///     Round trips since the last <see cref="ResetCounters" /> whose receive completed
    ///     synchronously, and so never suspended. This must be zero: a non-zero count means the
    ///     probe stopped measuring the suspending path and any ratio taken from it is not
    ///     comparable to the yield gate's.
    /// </summary>
    public long SynchronousReceives => Interlocked.Read(ref _synchronousReceives);

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _roundTrips, 0);
        Interlocked.Exchange(ref _synchronousReceives, 0);
    }

    private static async Task EchoAsync(Socket server, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await server.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);

                if (read == 0)
                    return;

                await server.SendAsync(buffer.AsMemory(0, read), SocketFlags.None, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
