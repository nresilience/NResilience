using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace NResilience.IntegrationTests;

/// <summary>
///     A minimal <see cref="WebApplication" /> started on an OS-assigned loopback port: the harness
///     for middleware tests, which assert what a real server does with a real request.
/// </summary>
public static class TestApp
{
    /// <summary>Starts a server with the given pipeline configuration, on an ephemeral port.</summary>
    public static async Task<TestServer> StartAsync(Action<WebApplication> configure)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();
        configure(app);

        await app.StartAsync();

        return new TestServer(app);
    }
}

/// <summary>A started server and the address it ended up on, so a test can point a client at it.</summary>
public sealed class TestServer(WebApplication app) : IAsyncDisposable
{
    internal Uri Uri => new(app.Urls.First(), UriKind.Absolute);

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}