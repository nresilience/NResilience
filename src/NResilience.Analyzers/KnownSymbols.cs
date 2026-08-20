using System;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace NResilience.Analyzers;

/// <summary>
/// The symbols every analyzer needs, resolved once per compilation.
/// <para>
/// Resolution doubles as the opt-out: a compilation that does not reference NResilience has no
/// <c>Resilience</c> type, <see cref="TryCreate"/> fails, and no per-node callback is ever
/// registered. An analyzer that ships inside the library package runs in every consumer's build,
/// so costing nothing in a project that does not use it is a requirement rather than a nicety.
/// </para>
/// </summary>
internal sealed class KnownSymbols
{
    private readonly Lazy<IMethodSymbol?> _entryPoint;

    private KnownSymbols(
        INamedTypeSymbol resilience,
        INamedTypeSymbol cancellationToken,
        INamedTypeSymbol? resilienceHttp,
        INamedTypeSymbol? breaker,
        INamedTypeSymbol? retryBudget,
        INamedTypeSymbol? timeSpan,
        INamedTypeSymbol? timeout,
        Compilation compilation)
    {
        Resilience = resilience;
        CancellationToken = cancellationToken;
        ResilienceHttp = resilienceHttp;
        Breaker = breaker;
        RetryBudget = retryBudget;
        TimeSpan = timeSpan;
        Timeout = timeout;

        // Deferred, and thread-safe because concurrent execution is on: binding Main is work that
        // only one rule needs, and every consumer's build would otherwise pay for it.
        _entryPoint = new Lazy<IMethodSymbol?>(
            () => compilation.GetEntryPoint(System.Threading.CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal INamedTypeSymbol Resilience { get; }

    internal INamedTypeSymbol CancellationToken { get; }

    internal INamedTypeSymbol? ResilienceHttp { get; }

    internal INamedTypeSymbol? Breaker { get; }

    internal INamedTypeSymbol? RetryBudget { get; }

    internal INamedTypeSymbol? TimeSpan { get; }

    internal INamedTypeSymbol? Timeout { get; }

    /// <summary>
    /// The compilation's entry point, if any: startup code is allowed to do once what a called
    /// method must not do per call. Resolved on demand, because binding <c>Main</c> is work only one
    /// rule needs and every consumer's build would otherwise pay for it.
    /// </summary>
    internal IMethodSymbol? EntryPoint => _entryPoint.Value;

    internal static bool TryCreate(Compilation compilation, out KnownSymbols known)
    {
        INamedTypeSymbol? resilience = compilation.GetTypeByMetadataName("NResilience.Resilience");
        INamedTypeSymbol? token = compilation.GetTypeByMetadataName("System.Threading.CancellationToken");

        if (resilience is null || token is null)
        {
            known = null!;
            return false;
        }

        known = new KnownSymbols(
            resilience,
            token,
            compilation.GetTypeByMetadataName("NResilience.Http.ResilienceHttp"),
            compilation.GetTypeByMetadataName("NResilience.Breaker"),
            compilation.GetTypeByMetadataName("NResilience.RetryBudget"),
            compilation.GetTypeByMetadataName("System.TimeSpan"),
            compilation.GetTypeByMetadataName("System.Threading.Timeout"),
            compilation);

        return true;
    }

    /// <summary>True when the invocation is one of the eight execution overloads.</summary>
    internal bool IsExecution(IMethodSymbol method) =>
        (method.Name == "RunAsync" || method.Name == "TryRunAsync")
        && SymbolEqualityComparer.Default.Equals(method.ContainingType, Resilience);

    internal bool IsCancellationToken(ITypeSymbol? type) =>
        type is not null && SymbolEqualityComparer.Default.Equals(type, CancellationToken);
}
