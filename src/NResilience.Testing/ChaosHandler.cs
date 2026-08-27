using NResilience.Testing.Internal;

namespace NResilience.Testing;

/// <summary>
///     Injects faults and latency into an <see cref="HttpClient" />'s pipeline.
///     <para>
///         Add it <b>after</b> <c>AddResilience()</c>, which makes it inner to the resilience handler:
///         an injected fault then reaches the classifier, the breaker and the retry budget exactly as a
///         real one would. Added before, it would sit outside the policy and inject faults nothing
///         retries, which tests the opposite of what you wanted.
///     </para>
/// </summary>
/// <example>
///     <code>
/// services.AddHttpClient&lt;OrdersClient&gt;()
///     .AddResilience()
///     .AddHttpMessageHandler(() =&gt; new ChaosHandler(chaos));   // inner to the policy
/// </code>
/// </example>
/// <remarks>
///     Chaos is applied on the asynchronous path only. That is not a gap in practice:
///     <c>ResilienceHandler.Send</c> throws <see cref="NotSupportedException" />, so a pipeline with a
///     policy in it has no synchronous path to inject into.
/// </remarks>
public sealed class ChaosHandler : DelegatingHandler
{
    private readonly ChaosDice _dice;
    private readonly Func<HttpResponseMessage>? _response;
    private int _injected;
    private int _slowed;

    /// <summary>Creates the handler.</summary>
    /// <param name="chaos">The profile.</param>
    /// <param name="response">
    ///     What a failing request returns instead of throwing - a 503, a 429 with a
    ///     <c>Retry-After</c>, a 500 with a body. Null throws <see cref="Chaos.Fault" /> instead.
    ///     <para>
    ///         Called once per injected failure, so it must return a fresh response each time: the
    ///         handler above disposes a response a retry supersedes, and handing out the same instance
    ///         twice returns one that is already disposed.
    ///     </para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="chaos" /> is null.</exception>
    /// <exception cref="ResilienceConfigurationException">The profile cannot be used.</exception>
    public ChaosHandler(Chaos chaos, Func<HttpResponseMessage>? response = null)
    {
        ArgumentNullException.ThrowIfNull(chaos);
        chaos.Validate();

        Chaos = chaos;
        _response = response;
        _dice = new ChaosDice(chaos.Seed);
    }

    /// <summary>The profile this handler was built with.</summary>
    public Chaos Chaos { get; }

    /// <summary>
    ///     How many requests have been failed. The number to assert on in a test that says chaos
    ///     actually fired, rather than inferring it from a retry count.
    /// </summary>
    public int Injected => Volatile.Read(ref _injected);

    /// <summary>How many requests have been slowed.</summary>
    public int Slowed => Volatile.Read(ref _slowed);

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!Chaos.Enabled)
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var roll = ChaosCore.Roll(Chaos, _dice);

        if (roll.Latency > TimeSpan.Zero)
        {
            Interlocked.Increment(ref _slowed);
            await Task.Delay(roll.Latency, Chaos.Time, cancellationToken).ConfigureAwait(false);
        }

        if (!roll.Faults)
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        Interlocked.Increment(ref _injected);

        return _response is not null ? _response() : throw ChaosCore.FaultFor(Chaos);
    }
}
