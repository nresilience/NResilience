using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace NResilience.Analyzers;

/// <summary>The <c>work</c> argument of an execution overload, once it is known to be a lambda.</summary>
internal sealed class Callback
{
    private Callback(IAnonymousFunctionOperation function, IParameterSymbol attemptToken)
    {
        Function = function;
        AttemptToken = attemptToken;
    }

    internal IAnonymousFunctionOperation Function { get; }

    /// <summary>The callback's own token: cancelled by the attempt timeout, and by the caller's token.</summary>
    internal IParameterSymbol AttemptToken { get; }

    /// <summary>
    /// Finds the lambda handed to <c>RunAsync</c> or <c>TryRunAsync</c>, and its cancellation token
    /// parameter. Method groups and delegate-valued locals are deliberately not resolved: the body
    /// may be in another assembly, and a diagnostic that depends on whether the source happens to
    /// be visible is worse than one that is quiet.
    /// </summary>
    internal static bool TryGet(IInvocationOperation invocation, KnownSymbols known, out Callback callback)
    {
        callback = null!;

        if (!known.IsExecution(invocation.TargetMethod))
        {
            return false;
        }

        IArgumentOperation? work = invocation.Arguments
            .FirstOrDefault(argument => argument.Parameter?.Name == "work");

        if (work?.Value is not IDelegateCreationOperation creation
            || creation.Target is not IAnonymousFunctionOperation function)
        {
            return false;
        }

        IParameterSymbol? token = function.Symbol.Parameters
            .LastOrDefault(parameter => known.IsCancellationToken(parameter.Type));

        if (token is null)
        {
            return false;
        }

        callback = new Callback(function, token);
        return true;
    }

    /// <summary>True when the body mentions the attempt's token at all, anywhere.</summary>
    internal bool UsesAttemptToken() =>
        Function.Body is not null
        && Function.Body.Descendants().Any(operation =>
            operation is IParameterReferenceOperation reference
            && SymbolEqualityComparer.Default.Equals(reference.Parameter, AttemptToken));
}
