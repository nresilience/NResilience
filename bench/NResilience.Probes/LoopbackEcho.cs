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
///     Appendix B of the design document reports 112 B per frame over a loopback socket,
///     providing a published figure for verification.
///     The callback passes its cancellation token to the socket calls, mimicking real code.
///     A wrapper that provides a cancellable token incurs the cost of the socket's
///     registration, which is a genuine cost rather than a harness artifact.
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

    /// <summary>Sends one byte and receives one byte back, resulting in two real suspensions.</summary>
    public async Task<int> RoundTripAsync(CancellationToken cancellationToken)
    {
        await _client.SendAsync(_send, SocketFlags.None, cancellationToken).ConfigureAwait(false);
        return await _client.ReceiveAsync(_receive, SocketFlags.None, cancellationToken).ConfigureAwait(false);
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
