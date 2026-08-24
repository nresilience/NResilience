using System.Net.Sockets;

namespace NResilience.Probes;

/// <summary>
///     A stand-in for the shipping <c>VerdictKind</c>. The values and semantics match the
///     shipping enum so the fused loop branches identically to the real executor.
/// </summary>
public enum VerdictKind : byte
{
    Ok,
    Transient,
    Throttled,
    Permanent,
}

/// <summary>A stand-in for the shipping <c>Verdict</c>.</summary>
public readonly record struct Verdict(VerdictKind Kind, TimeSpan? RetryAfter = null)
{
    public static Verdict Ok => new(VerdictKind.Ok);

    public static Verdict Transient => new(VerdictKind.Transient);

    public static Verdict Permanent => new(VerdictKind.Permanent);

    public static Verdict Throttled(TimeSpan? retryAfter = null) => new(VerdictKind.Throttled, retryAfter);
}

/// <summary>
///     Implements the classification rules of <c>Classifier.Default</c> using a hard-coded switch.
///     This project measures the executor frame rather than classifier storage, so rule lookup is
///     a synchronous type switch - the same approach the shipping classifier uses on the hot path.
/// </summary>
public static class ProbeClassifier
{
    public static Verdict Classify(Exception exception) => exception switch
    {
        TimeoutException => Verdict.Transient,
        SocketException => Verdict.Transient,
        IOException => Verdict.Transient,
        _ => Verdict.Permanent,
    };
}
