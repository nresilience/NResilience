using System.Net;
using System.Text;
using NResilience.Http;
using NResilience.Testing;

namespace NResilience.IntegrationTests;

/// <summary>
///     The full <see cref="Resilience.Http" /> preset, end-to-end, over a real loopback socket.
///     <para>
///         The behavioural suite in <c>NResilience.Tests</c> composes the real executor over a scripted
///         in-memory <c>HttpMessageHandler</c>. These tests do the same over a real TCP socket and the
///         real <c>SocketsHttpHandler</c>, so the connection pool, the cancellation registration on a
///         socket, and the response-stream disposal are all exercised - the things a scripted double
///         structurally cannot produce.
///     </para>
/// </summary>
public sealed class RealHttpTests
{
    /// <summary>
    ///     The smoke test: one GET, one 200. If this fails, the loopback server's framing is broken
    ///     against <c>SocketsHttpHandler</c>, and every test below is meaningless until it is fixed.
    /// </summary>
    [Fact]
    public async Task A_single_get_returns_a_200_over_a_real_socket()
    {
        await using var server = await LoopbackHttp.StartAsync(
            new LoopbackResponse(HttpStatusCode.OK, "ok"u8.ToArray()));

        using var client = HttpResilience.CreateClient(TestPolicy.InstantHttp, innerHandler: null);
        using var response = await client.GetAsync(server.BaseUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task A_transient_status_is_retried_to_success_over_a_real_socket()
    {
        await using var server = await LoopbackHttp.StartAsync(
            new LoopbackResponse(HttpStatusCode.ServiceUnavailable),
            new LoopbackResponse(HttpStatusCode.OK, "ok"u8.ToArray()));

        var events = new EventRecorder();
        using var client = HttpResilience.CreateClient(TestPolicy.InstantHttp with { OnEvent = events.Record });

        using var response = await client.GetAsync(server.BaseUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, server.RequestCount);

        Assert.Equal(
            [CallEventKind.Attempt, CallEventKind.Retrying, CallEventKind.Attempt, CallEventKind.Succeeded],
            events.Kinds);
    }

    [Fact]
    public async Task The_last_response_is_returned_when_the_attempts_run_out()
    {
        await using var server = await LoopbackHttp.StartAsync(
            new LoopbackResponse(HttpStatusCode.ServiceUnavailable));

        using var client = HttpResilience.CreateClient(TestPolicy.InstantHttp);
        using var response = await client.GetAsync(server.BaseUri);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, server.RequestCount);
    }

    [Fact]
    public async Task A_404_is_an_answer_not_a_failure()
    {
        await using var server = await LoopbackHttp.StartAsync(
            new LoopbackResponse(HttpStatusCode.NotFound));

        var events = new EventRecorder();
        using var client = HttpResilience.CreateClient(TestPolicy.InstantHttp with { OnEvent = events.Record });
        using var response = await client.GetAsync(server.BaseUri);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, server.RequestCount);
        Assert.Equal(CallEventKind.Succeeded, events.Single(CallEventKind.Succeeded).Kind);
    }

    /// <summary>
    ///     A server-supplied <c>Retry-After</c> header is honored over a real transport. The policy's
    ///     own backoff is set to five minutes, so the only way this test finishes in under than that is
    ///     if the server's one-second hint beat the backoff curve - exactly as it does in
    ///     <c>HttpHandlerTests.Retry_After_on_a_429_beats_the_backoff_curve</c>, but over a real socket.
    /// </summary>
    [Fact]
    public async Task Retry_After_on_a_429_beats_the_backoff_curve_over_a_real_socket()
    {
        await using var server = await LoopbackHttp.StartAsync(
            LoopbackResponse.WithRetryAfter(HttpStatusCode.TooManyRequests, 1),
            new LoopbackResponse(HttpStatusCode.OK));

        using var client = HttpResilience.CreateClient(
            TestPolicy.InstantHttp with { Backoff = Backoff.Constant(TimeSpan.FromMinutes(5)) });

        using var response = await client.GetAsync(server.BaseUri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, server.RequestCount);
    }

    /// <summary>
    ///     A POST is not retried over the real transport, because a duplicate order is the bug the
    ///     idempotency decision exists to prevent. This is the same claim
    ///     <c>HttpRegistrationTests.A_post_is_not_retried_through_the_registration</c> makes, but here
    ///     the transport is a real socket and the request body really crosses it.
    /// </summary>
    [Fact]
    public async Task A_post_is_not_retried_over_a_real_socket()
    {
        var requestBodies = new List<string>();

        await using var server = await LoopbackHttp.StartAsync((request, _) =>
        {
            requestBodies.Add(request.Body is null ? string.Empty : Encoding.UTF8.GetString(request.Body));
            return Task.FromResult(new LoopbackResponse(HttpStatusCode.ServiceUnavailable));
        });

        using var client = HttpResilience.CreateClient(TestPolicy.InstantHttp);
        using var response = await client.PostAsync(server.BaseUri, new StringContent("{}"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Single(requestBodies);
        Assert.Equal("{}", requestBodies[0]);
    }

    [Fact]
    public async Task A_post_can_be_made_repeatable_over_a_real_socket()
    {
        await using var server = await LoopbackHttp.StartAsync(
            new LoopbackResponse(HttpStatusCode.ServiceUnavailable),
            new LoopbackResponse(HttpStatusCode.OK));

        using var client = HttpResilience.CreateClient(
            TestPolicy.InstantHttp,
            new HttpResilienceOptions { RetryUnsafeMethods = true });

        using var response = await client.PostAsync(server.BaseUri, new StringContent("{}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, server.RequestCount);
    }
}
