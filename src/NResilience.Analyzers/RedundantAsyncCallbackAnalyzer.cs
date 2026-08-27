using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace NResilience.Analyzers;

/// <summary>
///     NRES007: <c>async attempt =&gt; await Work(attempt)</c> pays for a state machine that the
///     execution overloads never needed, because they take the callback's task directly - in either
///     shape, since there is a <c>ValueTask</c> overload set beside the <c>Task</c> one.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantAsyncCallbackAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Diagnostics.RedundantAsyncCallback);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
            throw new ArgumentNullException(nameof(context));

        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(static start =>
        {
            if (!KnownSymbols.TryCreate(start.Compilation, out var known))
                return;

            start.RegisterOperationAction(operation => Analyze(operation, known), OperationKind.Invocation);
        });
    }

    private static void Analyze(OperationAnalysisContext context, KnownSymbols known)
    {
        var invocation = (IInvocationOperation)context.Operation;

        if (!Callback.TryGet(invocation, known, out var callback)
            || !callback.Function.Symbol.IsAsync
            || callback.Function.Syntax is not AnonymousFunctionExpressionSyntax { AsyncKeyword.RawKind: not 0 } syntax)
            return;

        if (SingleAwaitedCall(callback) is not { } single)
            return;

        // The rewrite is legal on either of two grounds. Directly: the awaited task can stand in for
        // the delegate's return type, so the lambda binds to the overload it already bound to. Or by
        // rebinding: the awaited value is a ValueTask, which drops the lambda onto the ValueTask
        // overload set instead. An awaiter that has been configured qualifies under neither, and has
        // to keep the machine.
        if (!CanReturnDirectly(context.Compilation, single.Operation.Type, callback.Function.Symbol.ReturnType)
            && !CanRebindToValueTask(known, syntax, single.Operation.Type, callback.Function.Symbol.ReturnType))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.RedundantAsyncCallback,
            syntax.AsyncKeyword.GetLocation()));
    }

    /// <summary>
    ///     The awaited task is returnable as-is when the conversion to the delegate's return type is
    ///     identity or a reference conversion - the two the compiler would apply to the expression body
    ///     of a lambda that never awaited.
    /// </summary>
    private static bool CanReturnDirectly(Compilation compilation, ITypeSymbol? awaited, ITypeSymbol returned)
    {
        if (awaited is null)
            return false;

        var conversion = compilation.ClassifyCommonConversion(awaited, returned);
        return conversion.IsImplicit && (conversion.IsIdentity || conversion.IsReference);
    }

    /// <summary>
    ///     Whether dropping <c>async</c> lands the callback on the <c>ValueTask</c> overload set.
    ///     <para>
    ///         Those overloads are extension methods, so the async lambda in front of us bound to the
    ///         <c>Task</c> instance overload - an async lambda is applicable to it, and C# never looks
    ///         for an extension method while an instance method applies. Take the <c>async</c> away and
    ///         the lambda returns a <c>ValueTask</c>, which is applicable to no instance overload, so
    ///         resolution reaches the extension. The call keeps its name, its arguments and its
    ///         behavior, and stops building a state machine and a task for a value the callback already
    ///         had.
    ///     </para>
    /// </summary>
    /// <param name="known">Resolved symbols. The rewrite needs the <c>ValueTask</c> overloads to exist.</param>
    /// <param name="syntax">The lambda. One whose return type is written down cannot be re-bound: the
    ///     compiler would hold the rewritten body to that same type rather than resolving again.</param>
    /// <param name="awaited">The type of the awaited expression.</param>
    /// <param name="returned">The target delegate's return type.</param>
    /// <returns>True when the rewrite is legal and lands on the counterpart overload.</returns>
    /// <remarks>
    ///     The shapes have to correspond exactly - <c>ValueTask&lt;T&gt;</c> for <c>Task&lt;T&gt;</c> and
    ///     the same <c>T</c>, or <c>ValueTask</c> for <c>Task</c>. This is stricter than
    ///     <see cref="CanReturnDirectly" />, which admits <c>Task&lt;T&gt;</c> for a callback returning
    ///     <c>Task</c> on the strength of the reference conversion. There is no such conversion here, so
    ///     a <c>ValueTask&lt;T&gt;</c> awaited inside a callback that returns <c>Task</c> would rebind
    ///     from the void overload to the generic one - which compiles, and quietly starts handing the
    ///     result to the classifier. A diagnostic does not get to make that decision.
    /// </remarks>
    private static bool CanRebindToValueTask(KnownSymbols known, AnonymousFunctionExpressionSyntax syntax, ITypeSymbol? awaited, ITypeSymbol returned)
    {
        if (known.ResilienceValueTask is null
            || awaited is null
            || syntax is ParenthesizedLambdaExpressionSyntax { ReturnType: not null })
            return false;

        if (awaited is not INamedTypeSymbol { ContainingNamespace: not null } value
            || returned is not INamedTypeSymbol task
            || value.Name != "ValueTask"
            || task.Name != "Task"
            || value.ContainingNamespace.ToDisplayString() != "System.Threading.Tasks"
            || !SymbolEqualityComparer.Default.Equals(value.ContainingNamespace, task.ContainingNamespace))
            return false;

        if (value.TypeArguments.Length != task.TypeArguments.Length)
            return false;

        return value.TypeArguments.Length == 0
               || SymbolEqualityComparer.Default.Equals(value.TypeArguments[0], task.TypeArguments[0]);
    }

    /// <summary>
    ///     The one await, when the whole body is one await: <c>async a =&gt; await X(a)</c> in either the
    ///     value-returning or the void-returning shape. Anything longer might need the machine, and is
    ///     reported as null.
    /// </summary>
    private static IAwaitOperation? SingleAwaitedCall(Callback callback)
    {
        if (callback.Function.Body is not IBlockOperation body)
            return null;

        // A block body ends in a synthesized `return` that was never written down. Counting it
        // would mean the statement form of the same lambda looks like two statements.
        var written = body.Operations
            .Where(static operation => operation is not IReturnOperation { ReturnedValue: null, IsImplicit: true })
            .ToImmutableArray();

        if (written.Length != 1)
            return null;

        return written[0] switch
        {
            IReturnOperation { ReturnedValue: IAwaitOperation await } => await,
            IExpressionStatementOperation { Operation: IAwaitOperation await } => await,
            _ => null,
        };
    }
}
