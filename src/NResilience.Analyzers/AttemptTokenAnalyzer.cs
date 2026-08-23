using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace NResilience.Analyzers;

/// <summary>
/// NRES001 and NRES002: the callback's token has to reach the work.
/// <para>
/// This is the failure the two-token shape makes easy to write and impossible to see. The token the
/// callback receives is the one the attempt timeout cancels; the token the caller passed in is not.
/// Handing the wrong one to an HTTP call, or none at all, compiles, reads as correct in review, and
/// silently turns <c>AttemptTimeout</c> off for the only call that mattered.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AttemptTokenAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The name of the property carrying the attempt token's name to the code fix.</summary>
    internal const string AttemptNameProperty = "AttemptName";

    /// <summary>The name of the property carrying the omitted parameter's name to the code fix.</summary>
    internal const string ParameterNameProperty = "ParameterName";

    /// <summary>The name of the property saying whether the omitted argument can be appended positionally.</summary>
    internal const string PositionalProperty = "Positional";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Diagnostics.TokenNotPassed, Diagnostics.WrongTokenPassed);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(static start =>
        {
            if (!KnownSymbols.TryCreate(start.Compilation, out KnownSymbols known))
            {
                return;
            }

            start.RegisterOperationAction(operation => Analyze(operation, known), OperationKind.Invocation);
        });
    }

    private static void Analyze(OperationAnalysisContext context, KnownSymbols known)
    {
        var invocation = (IInvocationOperation)context.Operation;

        if (!Callback.TryGet(invocation, known, out Callback callback)
            || callback.Function.Body is null
            || callback.UsesAttemptToken())
        {
            return;
        }

        foreach (IArgumentOperation argument in TokenArguments(callback.Function.Body, known))
        {
            context.ReportDiagnostic(Diagnose(argument, callback.AttemptToken));
        }
    }

    /// <summary>
    /// Every cancellation token argument in the body: the ones the compiler filled in from a default,
    /// and the ones that were passed explicitly. Since the attempt's token is known to be unused,
    /// each of these is a call the attempt timeout cannot reach.
    /// </summary>
    private static IEnumerable<IArgumentOperation> TokenArguments(IOperation body, KnownSymbols known) =>
        body.Descendants()
            .OfType<IArgumentOperation>()
            .Where(argument => known.IsCancellationToken(argument.Parameter?.Type));

    private static Diagnostic Diagnose(IArgumentOperation argument, IParameterSymbol attemptToken)
    {
        ImmutableDictionary<string, string?>.Builder properties = ImmutableDictionary.CreateBuilder<string, string?>();
        properties.Add(AttemptNameProperty, attemptToken.Name);

        if (argument.ArgumentKind != ArgumentKind.DefaultValue)
        {
            // Passed, but not the attempt's: the caller's token, CancellationToken.None, default.
            return Diagnostic.Create(
                Diagnostics.WrongTokenPassed,
                argument.Value.Syntax.GetLocation(),
                properties.ToImmutable(),
                argument.Value.Syntax.ToString(),
                attemptToken.Name);
        }

        // Omitted, so the location has to be the call rather than an argument that is not written down.
        IOperation call = argument.Parent ?? argument;
        properties.Add(ParameterNameProperty, argument.Parameter?.Name);
        properties.Add(PositionalProperty, CanAppendPositionally(call, argument) ? "true" : "false");

        return Diagnostic.Create(
            Diagnostics.TokenNotPassed,
            call.Syntax.GetLocation(),
            properties.ToImmutable(),
            Describe(call),
            attemptToken.Name);
    }

    /// <summary>
    /// A token can be appended without a name only when it is the last parameter and every parameter
    /// before it was written out. Anything else - a skipped optional in the middle, an argument out
    /// of order - has to be named to compile.
    /// </summary>
    private static bool CanAppendPositionally(IOperation call, IArgumentOperation token)
    {
        if (token.Parameter is null)
        {
            return false;
        }

        ImmutableArray<IArgumentOperation> arguments = call switch
        {
            IInvocationOperation invocation => invocation.Arguments,
            IObjectCreationOperation creation => creation.Arguments,
            _ => ImmutableArray<IArgumentOperation>.Empty,
        };

        if (arguments.IsEmpty)
        {
            return false;
        }

        bool isLastParameter = token.Parameter.Ordinal == arguments.Length - 1;
        bool everythingElseWritten = arguments.All(argument =>
            ReferenceEquals(argument, token)
            || (argument.ArgumentKind == ArgumentKind.Explicit && argument.Syntax is ArgumentSyntax { NameColon: null }));

        return isLastParameter && everythingElseWritten;
    }

    private static string Describe(IOperation call) => call switch
    {
        IInvocationOperation invocation => invocation.TargetMethod.Name,
        IObjectCreationOperation creation => creation.Constructor?.ContainingType.Name ?? "the constructor",
        _ => call.Syntax.ToString(),
    };
}
