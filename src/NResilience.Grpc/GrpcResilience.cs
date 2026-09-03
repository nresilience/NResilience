using Grpc.Core;

namespace NResilience.Grpc;

/// <summary>
///     The gRPC vocabulary: a classifier that knows what a <see cref="StatusCode" /> means, the
///     preset built over it, and the per-call escape hatch from repetition.
///     <para>
///         These are statics on this package's own type rather than members of
///         <see cref="Classifier" /> and <see cref="Resilience" />, and that is not an accident of
///         layering. The core package has zero package dependencies, which is a claim it makes in
///         its own description; a <c>Classifier.Grpc</c> would put <c>Grpc.Core.Api</c> into the
///         reference set of every application that never speaks gRPC. <see cref="Classifier.Data" />
///         is the precedent for the shape.
///     </para>
/// </summary>
/// <example>
///     <code>
/// services.AddGrpcClient&lt;Orders.OrdersClient&gt;(o =&gt; o.Address = new Uri("https://orders.internal:5001"))
///     .AddGrpcResilience();
/// </code>
/// </example>
public static class GrpcResilience
{
    private static readonly AsyncLocal<bool> Current = new();

    /// <summary>
    ///     <see cref="Classifier.Default" /> plus gRPC status knowledge: what an
    ///     <see cref="RpcException" />'s <see cref="StatusCode" /> means for retrying.
    /// </summary>
    /// <remarks>
    ///     Every entry is one line to override -
    ///     <c>GrpcResilience.Classifier.On&lt;RpcException&gt;(e =&gt; …)</c> - and the last rule
    ///     registered for an exception type is the one that runs.
    ///     <para>
    ///         <see cref="StatusCode.Unavailable" /> and <see cref="StatusCode.DeadlineExceeded" /> are
    ///         transient; <see cref="StatusCode.ResourceExhausted" /> is throttled, which buys the long
    ///         backoff curve, no evidence against the breaker and no charge to the budget from one
    ///         verdict; everything else is permanent, including <see cref="StatusCode.Aborted" /> -
    ///         whether a transaction conflict is worth repeating depends on the store, so the
    ///         conservative default ships and the override is documented.
    ///     </para>
    /// </remarks>
    public static Classifier Classifier => ClassifierHolder.Instance;

    /// <summary>
    ///     <see cref="Resilience.Default" /> with the gRPC classifier: three attempts, a 30-second
    ///     deadline, a 10-second attempt timeout, and the automatic retry budget.
    /// </summary>
    /// <remarks>
    ///     The attempt timeout matters more here than elsewhere, because
    ///     <see cref="ResilienceInterceptor" /> writes it onto the wire as a deadline the peer can
    ///     read. With this preset a gRPC call gets a per-attempt ceiling the server learns about,
    ///     which it did not previously have.
    /// </remarks>
    public static Resilience Default => DefaultHolder.Instance;

    /// <summary>
    ///     Whether the current logical call is inside a <see cref="SingleShot" /> scope, and must
    ///     therefore not be repeated.
    /// </summary>
    public static bool IsSingleShot => Current.Value;

    /// <summary>
    ///     Refuses repetition for the calls made inside the scope, whatever
    ///     <see cref="GrpcResilienceOptions.IsRepeatable" /> says.
    ///     <para>
    ///         The per-call hatch is an ambient scope rather than a metadata entry on purpose: a
    ///         header would travel to the server, making this library's internal plumbing part of a
    ///         caller's wire contract, and it would be unreachable from a generated client that never
    ///         exposes <see cref="CallOptions" /> in the first place. This composes with any client.
    ///     </para>
    /// </summary>
    /// <returns>A scope that restores the previous value when disposed.</returns>
    /// <example>
    ///     <code>
    /// using (GrpcResilience.SingleShot())
    ///     await client.ChargeCardAsync(request);
    /// </code>
    /// </example>
    public static SingleShotScope SingleShot()
    {
        var previous = Current.Value;
        Current.Value = true;
        return new SingleShotScope(previous);
    }

    /// <summary>The verdict for one gRPC status code. The table lives here so the docs and the code cannot drift.</summary>
    internal static Verdict Map(StatusCode status) => status switch
    {
        // The transport could not reach the method. The canonical retryable status.
        StatusCode.Unavailable => Verdict.Transient,

        // A ceiling this side or the peer set expired. A fresh attempt gets a fresh ceiling; the
        // interceptor's cancellation ladder is what keeps our own ceilings from arriving here.
        StatusCode.DeadlineExceeded => Verdict.Transient,

        // The dependency is defending itself - out of quota, memory or concurrency. One verdict
        // buys the long backoff curve, no evidence against the breaker, and no charge to the budget.
        StatusCode.ResourceExhausted => Verdict.Throttled(),

        // Everything else falls through to Classifier.Default's Permanent, which is the right
        // answer for each of them for a different reason: Internal is the server's own bug and
        // retrying multiplies load against something already broken; Unauthenticated and
        // PermissionDenied will not fix themselves on a repeat (refresh credentials in
        // BeforeAttempt, which exists for it); InvalidArgument, NotFound, AlreadyExists,
        // FailedPrecondition, OutOfRange, Unimplemented and DataLoss are answers rather than
        // failures, the same line the HTTP classifier takes with a 404; Aborted is a transaction
        // conflict whose repeatability depends on the store; and Cancelled reaching the classifier
        // at all means a peer hung up on us, since our own cancellations are translated before they
        // get here - repeating a call the other end abandoned is a guess.
        _ => Verdict.Permanent,
    };

    private static class ClassifierHolder
    {
        internal static readonly Classifier Instance =
            NResilience.Classifier.Default.On<RpcException>(static e => Map(e.StatusCode));
    }

    private static class DefaultHolder
    {
        // Held rather than initialized inline so that the eager validation below throws
        // ResilienceConfigurationException from the property rather than surfacing as a
        // TypeInitializationException from the static constructor of GrpcResilience itself.
        internal static readonly Resilience Instance = Build();

        private static Resilience Build()
        {
            var policy = Resilience.Default with { Classify = Classifier, Name = "grpc" };
            policy.Validate();
            return policy;
        }
    }

    /// <summary>
    ///     Restores the repeatability flag the scope replaced. A struct, and one field wide, for the
    ///     same reason <see cref="ResilienceNestedRetry.NestedRetryScope" /> is: it is on the path of
    ///     every call that uses it.
    /// </summary>
    public readonly struct SingleShotScope : IDisposable
    {
        private readonly bool _previous;

        internal SingleShotScope(bool previous) => _previous = previous;

        /// <summary>Restores the previous value.</summary>
        public void Dispose() => Current.Value = _previous;
    }
}
