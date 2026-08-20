using Microsoft.CodeAnalysis;

namespace NResilience.Analyzers;

/// <summary>
/// Every diagnostic the analyzers raise, declared in one place so the ids, the categories and the
/// help links cannot drift from the page that documents them.
/// </summary>
internal static class Diagnostics
{
    private const string Help = "https://github.com/nresilience/NResilience/blob/main/docs/reference/analyzers.md#";

    private const string Reliability = "Reliability";
    private const string Usage = "Usage";
    private const string Performance = "Performance";

    /// <summary>NRES001: a call inside the callback wants a token and was not given the attempt's.</summary>
    internal static readonly DiagnosticDescriptor TokenNotPassed = Rule(
        "NRES001",
        "Pass the attempt's cancellation token to the work",
        "'{0}' takes a cancellation token and was not given one; pass the attempt's token '{1}' so a timed-out attempt can stop",
        Reliability,
        DiagnosticSeverity.Warning,
        "The callback's parameter is the attempt's cancellation token. Work that never sees it cannot be " +
        "stopped by an attempt timeout: the executor is awaiting the very task that ignored its token, so " +
        "the call hangs for as long as the work does.");

    /// <summary>NRES002: the callback forwards some other token - usually the caller's.</summary>
    internal static readonly DiagnosticDescriptor WrongTokenPassed = Rule(
        "NRES002",
        "Pass the attempt's cancellation token, not another one",
        "'{0}' is passed inside the callback instead of the attempt's token '{1}'; the attempt timeout has no effect on this call",
        Reliability,
        DiagnosticSeverity.Warning,
        "Two cancellation tokens are in scope inside a callback and only one of them is cancelled by the " +
        "attempt timeout. Passing the caller's token, or CancellationToken.None, compiles and reads as " +
        "correct while silently disabling AttemptTimeout for the call that matters.");

    /// <summary>NRES003: the literal configuration cannot pass <c>Validate()</c>.</summary>
    internal static readonly DiagnosticDescriptor InvalidConfiguration = Rule(
        "NRES003",
        "This policy will not pass validation",
        "{0}",
        Usage,
        DiagnosticSeverity.Warning,
        "A policy is validated on its first execution and throws ResilienceConfigurationException when it " +
        "cannot run. Where the values are literals the compiler can say so first.");

    /// <summary>NRES004: an attempt timeout longer than the deadline can never be reached.</summary>
    internal static readonly DiagnosticDescriptor AttemptTimeoutExceedsDeadline = Rule(
        "NRES004",
        "AttemptTimeout is longer than Deadline",
        "AttemptTimeout ({0}) is longer than Deadline ({1}); an attempt is capped by whatever is left of the deadline, so this setting can never be reached",
        Usage,
        DiagnosticSeverity.Warning,
        "The two bounds are different things and the deadline wins: it covers the whole call, retries and " +
        "backoff included, and each attempt is capped by what remains of it. An attempt timeout above the " +
        "deadline is dead configuration that reads as a deliberate 30-second attempt.");

    /// <summary>NRES005: per-call breaker or budget state.</summary>
    internal static readonly DiagnosticDescriptor PerCallGuardState = Rule(
        "NRES005",
        "A breaker or retry budget created per call keeps no state",
        "This {0} is created inside '{1}', so every call gets a new one; a {0} whose state is discarded each call can never {2}",
        Reliability,
        DiagnosticSeverity.Warning,
        "A breaker counts consecutive failures and a budget counts deposits over a window. Both are " +
        "mutable state whose whole purpose is to outlive the call. Hold one in a static readonly field, " +
        "in a container-managed singleton, or on the long-lived object it protects.");

    /// <summary>NRES006: per-call resilient client.</summary>
    internal static readonly DiagnosticDescriptor PerCallClient = Rule(
        "NRES006",
        "A resilient HttpClient created per call discards its per-host state",
        "This client is created and disposed inside '{0}'; the handler's per-host breakers and budgets are worth nothing to a client that is rebuilt per call",
        Reliability,
        DiagnosticSeverity.Info,
        "The handler holds a breaker and a budget per host. Both are worth nothing to a client that does " +
        "not outlive the call, and a disposed handler takes its connection pool with it. Hold one client " +
        "for the application's lifetime, or register it with AddResilience().");

    /// <summary>NRES007: an async callback that awaits exactly one call.</summary>
    internal static readonly DiagnosticDescriptor RedundantAsyncCallback = Rule(
        "NRES007",
        "The callback does not need to be async",
        "This callback awaits a single call; returning its task directly saves a state-machine allocation on every attempt",
        Performance,
        DiagnosticSeverity.Info,
        "Every async method that actually awaits allocates a state machine. A callback of the form " +
        "'async attempt => await Work(attempt)' adds one to every attempt for nothing, because the " +
        "execution overloads already take a Task-returning delegate.");

    private static DiagnosticDescriptor Rule(
        string id,
        string title,
        string messageFormat,
        string category,
        DiagnosticSeverity severity,
        string description) =>
        new(
            id,
            title,
            messageFormat,
            category,
            severity,
            isEnabledByDefault: true,
            description: description,
            helpLinkUri: Help + id.ToLowerInvariant());
}
