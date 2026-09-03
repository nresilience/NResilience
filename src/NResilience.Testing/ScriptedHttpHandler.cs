using System.Net;
using System.Net.Http.Headers;

namespace NResilience.Testing;

/// <summary>What one attempt sent, captured before <see cref="HttpClient" /> disposed the message.</summary>
public sealed record SentRequest(HttpMethod Method, Uri? RequestUri, HttpRequestHeaders Headers, string? Body);

/// <summary>
///     An <see cref="HttpMessageHandler" /> that serves a scripted sequence of responses, so the HTTP
///     layer can be tested without a transport. The last entry repeats, so a script does not have to
///     predict how many attempts the policy will make.
/// </summary>
/// <example>
///     <code>
/// var transport = new ScriptedHttpHandler()
///     .Respond(HttpStatusCode.ServiceUnavailable, times: 2)
///     .Respond(HttpStatusCode.OK);
///
/// using var client = HttpResilience.CreateClient(policy, innerHandler: transport);
/// var response = await client.GetAsync(uri, cancellationToken);
///
/// Assert.Equal(3, transport.CallCount);
/// Assert.Equal(HttpMethod.Get, transport.Requests[0].Method);
/// </code>
/// </example>
public sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly List<Func<HttpResponseMessage>> _steps = [];
    private readonly List<SentRequest> _requests = [];
    private readonly object _gate = new();
    private int _index = -1;
    private int _callCount;

    /// <summary>What each attempt sent, in order. A snapshot: the live message is disposed by HttpClient.</summary>
    public IReadOnlyList<SentRequest> Requests
    {
        get
        {
            lock (_gate)
                return [.. _requests];
        }
    }

    /// <summary>How many attempts reached the transport.</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>Whether <see cref="SentRequest.Body" /> is populated. Off by default; reading a body buffers it.</summary>
    public bool CaptureBodies { get; init; }

    /// <summary>Serves one response with this status.</summary>
    /// <param name="status">The status code.</param>
    /// <returns>This handler.</returns>
    public ScriptedHttpHandler Respond(HttpStatusCode status) => Respond(status, times: 1);

    /// <summary>Serves one response with this status.</summary>
    /// <param name="status">The status code.</param>
    /// <param name="times">How many attempts get this response before the script advances.</param>
    /// <returns>This handler.</returns>
    public ScriptedHttpHandler Respond(HttpStatusCode status, int times) =>
        Respond(() => new HttpResponseMessage(status), times);

    /// <summary>Serves one response built afresh per attempt, so its content can be read each time.</summary>
    /// <param name="response">Builds the response. Called once per attempt that consumes this step.</param>
    /// <returns>This handler.</returns>
    public ScriptedHttpHandler Respond(Func<HttpResponseMessage> response) => Respond(response, times: 1);

    /// <summary>Serves one response built afresh per attempt, so its content can be read each time.</summary>
    /// <param name="response">Builds the response. Called once per attempt that consumes this step.</param>
    /// <param name="times">How many attempts get this response before the script advances.</param>
    /// <returns>This handler.</returns>
    public ScriptedHttpHandler Respond(Func<HttpResponseMessage> response, int times)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentOutOfRangeException.ThrowIfLessThan(times, 1);

        for (var i = 0; i < times; i++)
            _steps.Add(response);

        return this;
    }

    /// <summary>Throws, for the transport failures a classifier has to see.</summary>
    /// <param name="exception">Builds the exception. Called once per attempt that consumes this step, so a reused instance never accumulates a shared stack trace or <see cref="Exception.Data" />.</param>
    /// <returns>This handler.</returns>
    public ScriptedHttpHandler Throw(Func<Exception> exception) => Throw(exception, times: 1);

    /// <summary>Throws, for the transport failures a classifier has to see.</summary>
    /// <param name="exception">Builds the exception. Called once per attempt that consumes this step, so a reused instance never accumulates a shared stack trace or <see cref="Exception.Data" />.</param>
    /// <param name="times">How many attempts throw this before the script advances.</param>
    /// <returns>This handler.</returns>
    public ScriptedHttpHandler Throw(Func<Exception> exception, int times)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentOutOfRangeException.ThrowIfLessThan(times, 1);

        for (var i = 0; i < times; i++)
        {
            _steps.Add(() => throw exception());
        }

        return this;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = CaptureBodies && request.Content is not null
            ? await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
            : null;

        var headers = new HttpRequestMessage().Headers;
        foreach (var header in request.Headers)
            headers.TryAddWithoutValidation(header.Key, header.Value);

        var snapshot = new SentRequest(request.Method, request.RequestUri, headers, body);

        lock (_gate)
            _requests.Add(snapshot);

        Interlocked.Increment(ref _callCount);

        if (_steps.Count == 0)
            throw new InvalidOperationException("The handler was given no script. Call Respond or Throw first.");

        var index = Math.Min(Interlocked.Increment(ref _index), _steps.Count - 1);
        return _steps[index]();
    }
}
