using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace NResilience.Analyzers;

/// <summary>
///     NRES005 and NRES006: the guards are mutable state whose whole purpose is to outlive the call.
///     A breaker that is rebuilt per call has never seen a failure, a budget that is rebuilt per call
///     has never seen a deposit, and a client that is rebuilt per call has no per-host anything. All
///     three read as configured resilience and provide none.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PerCallStateAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Diagnostics.PerCallGuardState, Diagnostics.PerCallClient);

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

            start.RegisterOperationAction(operation => AnalyzeCreation(operation, known), OperationKind.ObjectCreation);
            start.RegisterOperationAction(operation => AnalyzeInvocation(operation, known), OperationKind.Invocation);
        });
    }

    private static void AnalyzeCreation(OperationAnalysisContext context, KnownSymbols known)
    {
        var creation = (IObjectCreationOperation)context.Operation;

        if (known.IsBreaker(creation.Type))
            ReportGuardIfPerCall(context, creation, known, "breaker", "open");
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, KnownSymbols known)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;

        // RetryBudget.Shared(name) is looked up by name, so calling it per call is correct: it is
        // Of() - a fresh bucket every time - that throws the window away.
        if (method.Name == "Of" && known.IsRetryBudget(method.ContainingType))
        {
            ReportGuardIfPerCall(context, invocation, known, "retry budget", "refill");
            return;
        }

        if (method.Name == "CreateClient" && known.IsResilienceHttp(method.ContainingType))
            ReportClientIfPerCall(context, invocation, known);
    }

    /// <summary>
    ///     Reported only for a guard written directly into a policy's initializer. A guard that is a
    ///     local first may well be handed to something that keeps it, and a diagnostic on a shape that
    ///     is often correct is a diagnostic people turn off.
    /// </summary>
    private static void ReportGuardIfPerCall(
        OperationAnalysisContext context,
        IOperation guard,
        KnownSymbols known,
        string guardKind,
        string cannotEver)
    {
        if (!IsPolicySetting(guard, known) || !InsideSomethingCalledRepeatedly(context, known, out var container))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.PerCallGuardState,
            guard.Syntax.GetLocation(),
            guardKind,
            container,
            cannotEver));
    }

    private static void ReportClientIfPerCall(OperationAnalysisContext context, IOperation client, KnownSymbols known)
    {
        // The `using` form is the one that is provably per call: the client is disposed where it was
        // made. A plain local may be returned to something that holds it for the process's life.
        if (!IsDisposedWhereItIsMade(client.Syntax) || !InsideSomethingCalledRepeatedly(context, known, out var container))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.PerCallClient,
            client.Syntax.GetLocation(),
            container));
    }

    /// <summary>True when the operation is the value of <c>Breaker =</c> or <c>Budget =</c> in a policy initializer.</summary>
    private static bool IsPolicySetting(IOperation operation, KnownSymbols known)
    {
        var parent = operation.Parent;

        while (parent is IConversionOperation)
        {
            parent = parent.Parent;
        }

        return parent is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation property }
               && (property.Property.Name == "Breaker" || property.Property.Name == "Budget")
               && known.IsPolicy(property.Property.ContainingType);
    }

    /// <summary>
    ///     The enclosing member, unless it is the entry point or a static initializer - startup code is
    ///     allowed to do exactly once what a called method must not do per call.
    /// </summary>
    private static bool InsideSomethingCalledRepeatedly(OperationAnalysisContext context, KnownSymbols known, out string container)
    {
        container = string.Empty;

        if (context.ContainingSymbol is not IMethodSymbol method
            || method.MethodKind == MethodKind.StaticConstructor
            || known.IsEntryPoint(method))
            return false;

        container = method.MethodKind switch
        {
            MethodKind.PropertyGet or MethodKind.PropertySet => method.AssociatedSymbol?.Name ?? method.Name,
            _ => method.Name,
        };

        return true;
    }

    /// <summary>
    ///     True when the expression is the initializer of a <c>using</c> declaration, or of a
    ///     <c>using</c> statement's own declaration - not merely something written inside a using block.
    /// </summary>
    private static bool IsDisposedWhereItIsMade(SyntaxNode node)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case LocalDeclarationStatementSyntax declaration:
                    return declaration.UsingKeyword.RawKind != 0;
                case VariableDeclarationSyntax when current.Parent is UsingStatementSyntax:
                    return true;
                case StatementSyntax:
                case LambdaExpressionSyntax:
                case AnonymousMethodExpressionSyntax:
                    return false;
                default:
                    continue;
            }
        }

        return false;
    }
}
