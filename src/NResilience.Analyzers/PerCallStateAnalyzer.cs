using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace NResilience.Analyzers;

/// <summary>
///     NRES005, NRES006 and NRES008: the guards are mutable state whose whole purpose is to outlive the
///     call. A breaker that is rebuilt per call has never seen a failure, a budget that is rebuilt per
///     call has never seen a deposit, a policy scope or gRPC interceptor that is rebuilt per call has one
///     of each per key and keeps none of them, and a client that is rebuilt per call has no per-host
///     anything. All of them read as configured resilience and provide none.
///     <para>
///         NRES008 is the same failure one level in. <c>Hedge</c> and <c>Timeouts</c> hold no state of
///         their own - they are values - but the latency estimate they measure against is keyed by the
///         policy <i>instance</i>, so a policy rebuilt per call is a feature that never fires. It is a
///         rule of its own rather than a third case of NRES005 because the subject is the policy rather
///         than a guard inside it, and because it is worth suppressing separately.
///     </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PerCallStateAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Diagnostics.PerCallGuardState, Diagnostics.PerCallClient, Diagnostics.PerCallEstimator);

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

            // `policy with { ... }` is the shape the library teaches, so it is the shape NRES008 has to
            // see. `new Resilience { ... }` reaches the same check through AnalyzeCreation.
            start.RegisterOperationAction(operation => AnalyzeWith(operation, known), OperationKind.With);
        });
    }

    private static void AnalyzeCreation(OperationAnalysisContext context, KnownSymbols known)
    {
        var creation = (IObjectCreationOperation)context.Operation;

        if (known.IsPolicy(creation.Type))
        {
            ReportEstimatorIfPerCall(context, creation, creation.Initializer, known);
            return;
        }

        if (known.IsBreaker(creation.Type))
            ReportGuardIfPerCall(context, creation, known, "breaker", "open");
        else if (known.IsPolicyScope(creation.Type))
            ReportContainerIfPerCall(context, creation, known, "policy scope");
        else if (known.IsResilienceInterceptor(creation.Type))
            ReportContainerIfPerCall(context, creation, known, "gRPC resilience interceptor");
    }

    private static void AnalyzeWith(OperationAnalysisContext context, KnownSymbols known)
    {
        var with = (IWithOperation)context.Operation;

        if (known.IsPolicy(with.Type))
            ReportEstimatorIfPerCall(context, with, with.Initializer, known);
    }

    /// <summary>
    ///     Reported for a policy that <i>sets</i> <c>Hedge</c> or <c>Timeouts</c> inside a method, which
    ///     is the case the compiler can be sure about.
    /// </summary>
    /// <remarks>
    ///     The commoner and more dangerous shape is invisible from here:
    ///     <c>Policies.Api with { Deadline = ... }</c>, where the estimator was configured on
    ///     <c>Policies.Api</c> and this expression only narrows the deadline. Establishing that would mean
    ///     following the referenced symbol's own initializer, and a rule that is right most of the time
    ///     about a shape this common is a rule people turn off. So the diagnostic covers what is written
    ///     here, and the deadline docs cover the rest.
    /// </remarks>
    private static void ReportEstimatorIfPerCall(
        OperationAnalysisContext context,
        IOperation policy,
        IObjectOrCollectionInitializerOperation? initializer,
        KnownSymbols known)
    {
        if (initializer is null || !InsideSomethingCalledRepeatedly(context, known, out var container))
            return;

        foreach (var assignment in initializer.Initializers.OfType<ISimpleAssignmentOperation>())
        {
            if (assignment.Target is not IPropertyReferenceOperation property
                || !known.IsPolicy(property.Property.ContainingType))
                continue;

            var name = property.Property.Name;

            if (name != "Hedge" && name != "Timeouts")
                continue;

            // `Hedge = null` removes the feature rather than configuring one, and the HTTP handler's own
            // single-shot policy is written exactly that way.
            if (IsNull(assignment.Value))
                continue;

            context.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.PerCallEstimator,
                policy.Syntax.GetLocation(),
                name,
                container));

            return;
        }
    }

    /// <summary>True for <c>null</c>, through however many conversions the nullable target added.</summary>
    private static bool IsNull(IOperation value)
    {
        var current = value;

        while (current is IConversionOperation conversion)
        {
            current = conversion.Operand;
        }

        return current.ConstantValue is { HasValue: true, Value: null };
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

    /// <summary>
    ///     A policy container - a <c>PolicyScope</c>, a gRPC <c>ResilienceInterceptor</c> - is a
    ///     dictionary of breakers and budgets, so building one per call is NRES005 one level up: every
    ///     key starts again with a closed breaker and an empty budget.
    /// </summary>
    /// <remarks>
    ///     Reported only for a container that provably dies with the call - one used straight away as
    ///     a receiver, or one held in a local that never leaves the method. A container handed to
    ///     something else is not followed there: <c>channel.Intercept(new ResilienceInterceptor())</c>
    ///     and <c>services.AddSingleton(new PolicyScope&lt;string&gt;(t))</c> are the same syntax and
    ///     opposite verdicts, and a diagnostic on a shape that is often correct is a diagnostic people
    ///     turn off.
    ///     <para>
    ///         <c>ResilienceHandler</c> is deliberately not in this set, and the reason is worth
    ///         recording rather than leaving as an accident of which type came up first. It is built by
    ///         the client factory from a registration callback, never by the caller per request, and a
    ///         hand-built one is always an argument to an <c>HttpClient</c> whose own lifetime is what
    ///         actually matters - which is NRES006's question, not this one.
    ///     </para>
    /// </remarks>
    private static void ReportContainerIfPerCall(OperationAnalysisContext context, IOperation container, KnownSymbols known, string kind)
    {
        if (!DiesWithTheCall(container) || !InsideSomethingCalledRepeatedly(context, known, out var method))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.PerCallGuardState,
            container.Syntax.GetLocation(),
            kind,
            method,
            "open a breaker or throttle a retry storm"));
    }

    /// <summary>
    ///     True when the created object cannot outlive the method: it is dereferenced on the spot, or
    ///     it initializes a local whose every use is a member access on it.
    /// </summary>
    private static bool DiesWithTheCall(IOperation creation)
    {
        var parent = creation.Parent;

        while (parent is IConversionOperation)
        {
            parent = parent.Parent;
        }

        // new PolicyScope<string>(policy).For(key) - nothing holds it at all.
        if (parent is IInvocationOperation or IPropertyReferenceOperation or IFieldReferenceOperation)
            return true;

        if (parent is not IVariableInitializerOperation { Parent: IVariableDeclaratorOperation declarator })
            return false;

        var local = declarator.Symbol;
        var body = Root(creation);

        foreach (var reference in body.Descendants().OfType<ILocalReferenceOperation>())
        {
            if (!SymbolEqualityComparer.Default.Equals(reference.Local, local))
                continue;

            // Anything other than "call something on it" could hand it to a field, a caller, or a
            // collection that outlives the method, and the analyzer does not follow it there.
            if (reference.Parent is not (IInvocationOperation or IPropertyReferenceOperation or IFieldReferenceOperation))
                return false;
        }

        return true;
    }

    private static IOperation Root(IOperation operation)
    {
        var current = operation;

        while (current.Parent is { } parent)
        {
            current = parent;
        }

        return current;
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
