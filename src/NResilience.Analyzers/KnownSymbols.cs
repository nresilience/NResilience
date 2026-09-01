using Microsoft.CodeAnalysis;

namespace NResilience.Analyzers;

/// <summary>
///     The symbols every analyzer needs, resolved once per compilation.
///     <para>
///         Resolution doubles as the opt-out: a compilation that does not reference NResilience has no
///         <c>Resilience</c> type, <see cref="TryCreate" /> fails, and no per-node callback is ever
///         registered. An analyzer that ships inside the library package runs in every consumer's build,
///         so costing nothing in a project that does not use it is a requirement rather than a nicety.
///     </para>
/// </summary>
internal sealed class KnownSymbols
{
    private readonly Lazy<IMethodSymbol?> _entryPoint;

    private KnownSymbols(
        INamedTypeSymbol resilience,
        INamedTypeSymbol? resilienceValueTask,
        INamedTypeSymbol cancellationToken,
        INamedTypeSymbol? resilienceHttp,
        INamedTypeSymbol? breaker,
        INamedTypeSymbol? retryBudget,
        INamedTypeSymbol? policyScope,
        INamedTypeSymbol? resilienceInterceptor,
        INamedTypeSymbol? timeSpan,
        INamedTypeSymbol? timeout,
        Compilation compilation)
    {
        Resilience = resilience;
        ResilienceValueTask = resilienceValueTask;
        CancellationToken = cancellationToken;
        ResilienceHttp = resilienceHttp;
        Breaker = breaker;
        RetryBudget = retryBudget;
        PolicyScope = policyScope;
        ResilienceInterceptor = resilienceInterceptor;
        TimeSpan = timeSpan;
        Timeout = timeout;

        // Thread-safe because concurrent execution is on.
        _entryPoint = new Lazy<IMethodSymbol?>(
            () => compilation.GetEntryPoint(System.Threading.CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal INamedTypeSymbol Resilience { get; }

    /// <summary>
    ///     The static class holding the <c>ValueTask</c>-returning execution overloads. They are
    ///     extension methods rather than members of <see cref="Resilience" />, so an analyzer that
    ///     recognized only the record's own methods would fall silent on exactly the callback shape a
    ///     caller reached for to save an allocation - and NRES001 guards a failure that is invisible
    ///     without it.
    /// </summary>
    internal INamedTypeSymbol? ResilienceValueTask { get; }

    internal INamedTypeSymbol CancellationToken { get; }

    internal INamedTypeSymbol? ResilienceHttp { get; }

    internal INamedTypeSymbol? Breaker { get; }

    internal INamedTypeSymbol? RetryBudget { get; }

    /// <summary>
    ///     The unbound generic <c>PolicyScope&lt;TKey&gt;</c>. Held as its definition, because every
    ///     construction a consumer writes is a different constructed type over the same one rule.
    /// </summary>
    internal INamedTypeSymbol? PolicyScope { get; }

    /// <summary>
    ///     <c>NResilience.Grpc.ResilienceInterceptor</c>, from the gRPC package. Nullable like every
    ///     other optional symbol here: a consumer who never references that package resolves null and
    ///     pays nothing, which is the rule this whole assembly is built to.
    /// </summary>
    internal INamedTypeSymbol? ResilienceInterceptor { get; }

    internal INamedTypeSymbol? TimeSpan { get; }

    internal INamedTypeSymbol? Timeout { get; }

    /// <summary>
    ///     The compilation's entry point, if any: startup code is allowed to do once what a called
    ///     method must not do per call. Resolved on demand, because binding <c>Main</c> is work only one
    ///     rule needs and every consumer's build would otherwise pay for it.
    /// </summary>
    internal IMethodSymbol? EntryPoint => _entryPoint.Value;

    internal static bool TryCreate(Compilation compilation, out KnownSymbols known)
    {
        var resilience = compilation.GetTypeByMetadataName("NResilience.Resilience");
        var token = compilation.GetTypeByMetadataName("System.Threading.CancellationToken");

        if (resilience is null || token is null)
        {
            known = null!;
            return false;
        }

        known = new KnownSymbols(
            resilience,
            compilation.GetTypeByMetadataName("NResilience.ResilienceValueTask"),
            token,
            compilation.GetTypeByMetadataName("NResilience.Http.ResilienceHttp"),
            compilation.GetTypeByMetadataName("NResilience.Breaker"),
            compilation.GetTypeByMetadataName("NResilience.RetryBudget"),
            compilation.GetTypeByMetadataName("NResilience.PolicyScope`1"),
            compilation.GetTypeByMetadataName("NResilience.Grpc.ResilienceInterceptor"),
            compilation.GetTypeByMetadataName("System.TimeSpan"),
            compilation.GetTypeByMetadataName("System.Threading.Timeout"),
            compilation);

        return true;
    }

    /// <summary>True when the invocation is one of the execution overloads, in either callback shape.</summary>
    internal bool IsExecution(IMethodSymbol method) =>
        (method.Name == "RunAsync" || method.Name == "TryRunAsync")
        && (SymbolEqualityComparer.Default.Equals(method.ContainingType, Resilience)
            || Is(method.ContainingType, ResilienceValueTask));

    internal bool IsCancellationToken(ITypeSymbol? type) => type is not null && SymbolEqualityComparer.Default.Equals(type, CancellationToken);

    internal bool IsPolicy(ITypeSymbol? type) => Is(type, Resilience);

    internal bool IsBreaker(ITypeSymbol? type) => Is(type, Breaker);

    internal bool IsRetryBudget(ITypeSymbol? type) => Is(type, RetryBudget);

    internal bool IsResilienceHttp(ITypeSymbol? type) => Is(type, ResilienceHttp);

    /// <summary>True for any construction of <c>PolicyScope&lt;TKey&gt;</c>, whatever the key type.</summary>
    internal bool IsPolicyScope(ITypeSymbol? type) => Is((type as INamedTypeSymbol)?.OriginalDefinition, PolicyScope);

    /// <summary>True for the gRPC resilience interceptor, which holds one breaker and budget per scope key.</summary>
    internal bool IsResilienceInterceptor(ITypeSymbol? type) => Is(type, ResilienceInterceptor);

    /// <summary>True when the method is the compilation's entry point.</summary>
    internal bool IsEntryPoint(IMethodSymbol method) => SymbolEqualityComparer.Default.Equals(method, EntryPoint);

    private static bool Is(ITypeSymbol? type, INamedTypeSymbol? known) => known is not null && SymbolEqualityComparer.Default.Equals(type, known);
}
