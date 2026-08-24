using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace NResilience.Analyzers;

/// <summary>
/// NRES003 and NRES004: what <c>Validate()</c> says on first execution, said at build time instead
/// wherever the values are literals - plus the one combination that is legal, validates, and still
/// cannot do what it looks like it does.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PolicyConfigurationAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Diagnostics.InvalidConfiguration, Diagnostics.AttemptTimeoutExceedsDeadline);

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
            if (!KnownSymbols.TryCreate(start.Compilation, out var known))
            {
                return;
            }

            start.RegisterOperationAction(operation => Analyze(operation, known), OperationKind.ObjectCreation, OperationKind.With);
        });
    }

    private static void Analyze(OperationAnalysisContext context, KnownSymbols known)
    {
        var initializer = context.Operation switch
        {
            IObjectCreationOperation creation when known.IsPolicy(creation.Type) => creation.Initializer,
            IWithOperation with when known.IsPolicy(with.Type) => with.Initializer,
            _ => null,
        };

        if (initializer is null)
        {
            return;
        }

        var settings = Settings(initializer);

        CheckAttempts(context, settings);
        CheckDuration(context, settings, known, "Deadline");
        CheckDuration(context, settings, known, "AttemptTimeout");
        CheckTheTwoBounds(context, settings, known);
    }

    private static Dictionary<string, IOperation> Settings(IObjectOrCollectionInitializerOperation initializer)
    {
        var settings = new Dictionary<string, IOperation>(StringComparer.Ordinal);

        foreach (var assignment in initializer.Initializers)
        {
            if (assignment is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation property } simple)
            {
                settings[property.Property.Name] = simple.Value;
            }
        }

        return settings;
    }

    private static void CheckAttempts(OperationAnalysisContext context, Dictionary<string, IOperation> settings)
    {
        if (!settings.TryGetValue("Attempts", out var attempts)
            || !attempts.ConstantValue.HasValue
            || attempts.ConstantValue.Value is not int count
            || count >= 1)
        {
            return;
        }

        Report(
            context,
            Diagnostics.InvalidConfiguration,
            attempts,
            string.Format(CultureInfo.InvariantCulture, "Attempts must be at least 1; it is {0}", count));
    }

    private static void CheckDuration(
        OperationAnalysisContext context,
        Dictionary<string, IOperation> settings,
        KnownSymbols known,
        string name)
    {
        if (!settings.TryGetValue(name, out var setting)
            || !TimeSpanValue.TryEvaluate(setting, known, out var value)
            || value.IsUnbounded()
            || value > TimeSpan.Zero)
        {
            return;
        }

        Report(
            context,
            Diagnostics.InvalidConfiguration,
            setting,
            string.Format(
                CultureInfo.InvariantCulture,
                "{0} must be positive, or Timeout.InfiniteTimeSpan for no bound; it is {1}",
                name,
                value.Describe()));
    }

    /// <summary>
    /// The deadline covers the whole call and caps every attempt inside it, so an attempt timeout
    /// above the deadline is unreachable. Only reported when both are written in the same
    /// initializer: resolving a base policy's deadline would mean guessing which preset it came
    /// from, and a diagnostic that guesses is worse than none.
    /// </summary>
    private static void CheckTheTwoBounds(
        OperationAnalysisContext context,
        Dictionary<string, IOperation> settings,
        KnownSymbols known)
    {
        if (!settings.TryGetValue("AttemptTimeout", out var attemptTimeout)
            || !settings.TryGetValue("Deadline", out var deadline)
            || !TimeSpanValue.TryEvaluate(attemptTimeout, known, out var attempt)
            || !TimeSpanValue.TryEvaluate(deadline, known, out var whole)
            || attempt.IsUnbounded()
            || whole.IsUnbounded()
            || attempt <= whole)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.AttemptTimeoutExceedsDeadline,
            attemptTimeout.Syntax.GetLocation(),
            attempt.Describe(),
            whole.Describe()));
    }

    private static void Report(
        OperationAnalysisContext context,
        DiagnosticDescriptor descriptor,
        IOperation setting,
        string message) =>
        context.ReportDiagnostic(Diagnostic.Create(descriptor, setting.Syntax.GetLocation(), message));
}
