using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace NResilience.Analyzers;

/// <summary>
///     NRES007: <c>async attempt =&gt; await Work(attempt)</c> pays for a state machine that the
///     execution overloads never needed, because they already take a <c>Task</c>-returning delegate.
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

        // The rewrite is only legal when the awaited task can stand in for the delegate's return
        // type: Task<T> for Task<T>, or Task<T> for a callback that returns Task. A ValueTask, or
        // an awaiter that has been configured, is a different type and has to keep the machine.
        if (!CanReturnDirectly(context.Compilation, single.Operation.Type, callback.Function.Symbol.ReturnType))
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
