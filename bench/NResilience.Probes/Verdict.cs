namespace NResilience.Probes;

/// <summary>
/// Phase 0a stand-in for the shipping <c>VerdictKind</c>. Values and semantics match
/// plans/nresilience-design-v3.md so the fused loop branches the same number of times
/// the real executor will.
/// </summary>
public enum VerdictKind : byte
{
    Ok,
    Transient,
    Throttled,
    Permanent,
}

/// <summary>Phase 0a stand-in for the shipping <c>Verdict</c>.</summary>
public readonly record struct Verdict(VerdictKind Kind, TimeSpan? RetryAfter = null)
{
    public static Verdict Ok => new(VerdictKind.Ok);

    public static Verdict Transient => new(VerdictKind.Transient);

    public static Verdict Permanent => new(VerdictKind.Permanent);

    public static Verdict Throttled(TimeSpan? retryAfter = null) => new(VerdictKind.Throttled, retryAfter);
}

/// <summary>
/// The classification rules of <c>Classifier.Default</c>, hard-coded. Phase 0a measures the
/// executor frame, not the classifier's storage, so the rule lookup is a synchronous type
/// switch — which is what the shipping classifier compiles down to on the hot path anyway.
/// </summary>
public static class ProbeClassifier
{
    public static Verdict Classify(Exception exception) => exception switch
    {
        TimeoutException => Verdict.Transient,
        System.Net.Sockets.SocketException => Verdict.Transient,
        IOException => Verdict.Transient,
        _ => Verdict.Permanent,
    };
}
