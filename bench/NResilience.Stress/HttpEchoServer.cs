using System.Diagnostics;
using System.Globalization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace NResilience.Stress;

/// <summary>
///     The HTTP-level cells' dependency: a Kestrel service on loopback with two endpoints.
///     <c>/echo</c> answers 200; <c>/flaky</c> answers 503 on every fourth request since the last
///     counter reset, deterministically, so both clients face the same failure pressure.
///     <para>
///         Both answers carry the same body, so a request costs the same whether it succeeded or
///         failed. Otherwise a retrying arm - whose extra requests are disproportionately the
///         cheap failures - would be credited with a transport saving that has nothing to do with
///         its handler.
///     </para>
/// </summary>
/// <remarks>
///     It runs in its own process. <see cref="GC.GetTotalAllocatedBytes(bool)" /> and
///     <see cref="GC.GetTotalPauseDuration" /> are process-wide, so an in-process server puts its
///     own allocation and its own collections into the client's per-request figures - and into the
///     client's GC pause share, which is the column the whole memory argument rests on. Out of
///     process, the numbers a cell reports are the client's alone. The server is pinned to one GC
///     configuration for every cell so that the transport cost it contributes is a constant the
///     arms share rather than something that moves with the cell's own GC mode.
/// </remarks>
internal sealed class HttpEchoServer : IAsyncDisposable, IServerObservatory
{
    /// <summary>The stdout line the child prints once it is listening.</summary>
    private const string ReadyPrefix = "echo-server-ready ";

    private readonly Process _process;
    private readonly HttpClient _control;

    private HttpEchoServer(Process process, Uri uri)
    {
        _process = process;
        Uri = uri;

        // The control client is deliberately separate from the arms' clients and its calls are
        // not counted by the server, so reading the counter cannot perturb the counter.
        _control = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(10) };
    }

    public Uri Uri { get; }

    public string EchoUrl => new Uri(Uri, "echo").ToString();

    public string FlakyUrl => new Uri(Uri, "flaky").ToString();

    public async Task ResetCountersAsync()
    {
        using var response = await _control.GetAsync("reset").ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<long> ReadRequestsAsync()
    {
        var body = await _control.GetStringAsync("stats").ConfigureAwait(false);

        return long.Parse(body, CultureInfo.InvariantCulture);
    }

    /// <summary>Spawns the server process and waits for it to report the port it got.</summary>
    public static async Task<HttpEchoServer> StartAsync()
    {
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("--echo-server");

        // Pinned, and explicitly rather than by inheritance: the cell child runs with the GC
        // mode under test in its environment, and without these the dependency's own GC mode
        // would change from cell to cell and take the transport cost with it.
        psi.Environment["DOTNET_gcServer"] = "1";
        psi.Environment["DOTNET_gcConcurrent"] = "1";

        var process = Process.Start(psi)!;

        var ready = await ReadReadyLineAsync(process).ConfigureAwait(false);

        if (ready is null)
        {
            process.Kill(entireProcessTree: true);
            process.Dispose();

            throw new InvalidOperationException("The echo server process did not report a listening address.");
        }

        return new HttpEchoServer(process, new Uri(ready, UriKind.Absolute));
    }

    private static async Task<string?> ReadReadyLineAsync(Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        while (!timeout.IsCancellationRequested)
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);

            if (line is null)
                return null;

            if (line.StartsWith(ReadyPrefix, StringComparison.Ordinal))
                return line[ReadyPrefix.Length..].Trim();
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _control.Dispose();

        try
        {
            // Closing stdin is the shutdown signal, so the server gets to stop Kestrel
            // gracefully; the kill is the backstop for a server that has wedged.
            _process.StandardInput.Close();

            if (!_process.WaitForExit(5000))
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }

        _process.Dispose();

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    ///     The child mode: run the service until stdin closes. Everything the arms measure lives
    ///     on the other side of a socket from here.
    /// </summary>
    public static async Task<int> RunServerAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();
        var requests = 0L;

        // One body for both answers: a request's transport cost must not depend on its status.
        // Results.Text rather than Results.Ok so that neither answer pays for JSON.
        app.MapGet("/echo", () =>
        {
            Interlocked.Increment(ref requests);

            return Results.Text("ok");
        });

        app.MapGet("/flaky", () =>
        {
            var n = Interlocked.Increment(ref requests);

            // One failure in four: the deterministic 1-in-4 pattern, so a retry-twice client
            // succeeds on first or second attempt in steady state and the failure pressure is
            // sustained rather than bursty.
            return n % 4 == 0
                ? Results.Text("ok", statusCode: 503)
                : Results.Text("ok");
        });

        // Control endpoints, uncounted: reading the counter must not move it.
        app.MapGet("/stats", () => Results.Text(Interlocked.Read(ref requests).ToString(CultureInfo.InvariantCulture)));

        app.MapGet("/reset", () =>
        {
            Interlocked.Exchange(ref requests, 0);

            return Results.Text("ok");
        });

        await app.StartAsync().ConfigureAwait(false);

        Console.Out.WriteLine(ReadyPrefix + app.Urls.First());
        Console.Out.Flush();

        // Stdin closing is the parent saying the matrix is done with this server.
        await Console.In.ReadToEndAsync().ConfigureAwait(false);

        await app.StopAsync().ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);

        return 0;
    }
}
