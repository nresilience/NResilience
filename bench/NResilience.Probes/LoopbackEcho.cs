using System.Net;
using System.Net.Sockets;

namespace NResilience.Probes;

/// <summary>
/// A real loopback TCP round trip, used to cross-check the <c>Task.Yield</c> gate.
///
/// <c>Task.Yield</c> is the right primitive for a gate — it suspends deterministically, every
/// time, with nothing to average away — but it is not I/O, and the design's whole performance
/// argument is about the path real I/O takes. If the two disagree about the ratio between a
/// fused loop and a composed pipeline, the gate is measuring an artefact. Appendix B of the
/// design document reports 112 B per frame over a loopback socket, so there is a published
/// figure to check against.
///
/// The callback passes its cancellation token through to the socket calls, as real code does.
/// That means a wrapper handing down a cancellable token pays for the socket's registration on
/// it, and that cost is genuine rather than an artefact of the harness.
/// </summary>
public sealed class LoopbackEcho : IAsyncDisposable
{
    private readonly Socket _listener;
    private readonly Socket _client;
    private readonly Socket _server;
    private readonly byte[] _send = [1];
    private readonly byte[] _receive = new byte[1];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _pump;

    private LoopbackEcho(Socket listener, Socket client, Socket server)
    {
        _listener = listener;
        _client = client;
        _server = server;
        _pump = Task.Run(() => EchoAsync(server, _shutdown.Token));
    }

    public static async Task<LoopbackEcho> StartAsync()
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Task<Socket> accepting = listener.AcceptAsync();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!).ConfigureAwait(false);
        Socket server = await accepting.ConfigureAwait(false);

        client.NoDelay = true;
        server.NoDelay = true;

        return new LoopbackEcho(listener, client, server);
    }

    /// <summary>One byte out, one byte back. Two real suspensions.</summary>
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
                int read = await server.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

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
}
