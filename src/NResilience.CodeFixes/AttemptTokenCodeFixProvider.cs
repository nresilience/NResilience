using System.Collections.Immutable;
using System.Composition;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace NResilience.Analyzers;

/// <summary>
///     The fix for NRES001 and NRES002: pass the attempt's token. Where the callback declared its
///     parameter as <c>_</c> the fix names it first, because a token you cannot refer to cannot be
///     passed.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AttemptTokenCodeFixProvider))]
[Shared]
public sealed class AttemptTokenCodeFixProvider : CodeFixProvider
{
    private const string Title = "Pass the attempt's cancellation token";
    private const string Discard = "_";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(Diagnostics.TokenNotPassedId, Diagnostics.WrongTokenPassedId);

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

        if (root is null)
            return;

        foreach (var diagnostic in context.Diagnostics)
        {
            var reported = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

            if (reported.FirstAncestorOrSelf<AnonymousFunctionExpressionSyntax>() is null)
                continue;

            context.RegisterCodeFix(
                CodeAction.Create(
                    Title,
                    token => Fix(context.Document, diagnostic, token),
                    nameof(AttemptTokenCodeFixProvider)),
                diagnostic);
        }
    }

    private static async Task<Document> Fix(Document document, Diagnostic diagnostic, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

        if (root is null)
            return document;

        var reported = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

        if (reported.FirstAncestorOrSelf<AnonymousFunctionExpressionSyntax>() is not { } lambda)
            return document;

        diagnostic.Properties.TryGetValue(AttemptTokenAnalyzer.AttemptNameProperty, out var declared);
        var parameter = TokenParameter(lambda, declared);
        var name = declared == Discard || declared is null ? AvailableName(lambda) : declared;

        var edits = new Dictionary<SyntaxNode, SyntaxNode>();

        if (parameter is not null && parameter.Identifier.ValueText != name)
            edits[parameter] = parameter.WithIdentifier(Identifier(name).WithTriviaFrom(parameter.Identifier));

        if (diagnostic.Id == Diagnostics.WrongTokenPassedId)
            edits[reported] = IdentifierName(name).WithTriviaFrom(reported);
        else if (WithTokenArgument(reported, diagnostic, name) is { } rewritten)
            edits[reported] = rewritten;
        else
            return document;

        SyntaxNode fixedLambda = lambda.ReplaceNodes(edits.Keys, (original, _) => edits[original]);
        return document.WithSyntaxRoot(root.ReplaceNode(lambda, fixedLambda));
    }

    /// <summary>Adds the omitted argument, named unless the analyzer found it could go last.</summary>
    private static SyntaxNode? WithTokenArgument(SyntaxNode call, Diagnostic diagnostic, string name)
    {
        var arguments = call switch
        {
            InvocationExpressionSyntax invocation => invocation.ArgumentList,
            ObjectCreationExpressionSyntax creation => creation.ArgumentList,
            ImplicitObjectCreationExpressionSyntax creation => creation.ArgumentList,
            _ => null,
        };

        if (arguments is null)
            return null;

        diagnostic.Properties.TryGetValue(AttemptTokenAnalyzer.PositionalProperty, out var positional);
        diagnostic.Properties.TryGetValue(AttemptTokenAnalyzer.ParameterNameProperty, out var parameterName);

        var token = Argument(IdentifierName(name));

        if (positional != "true" && !string.IsNullOrEmpty(parameterName))
            token = token.WithNameColon(NameColon(IdentifierName(parameterName!)));

        var updated = arguments.WithArguments(arguments.Arguments.Add(token));

        return call switch
        {
            InvocationExpressionSyntax invocation => invocation.WithArgumentList(updated),
            ObjectCreationExpressionSyntax creation => creation.WithArgumentList(updated),
            ImplicitObjectCreationExpressionSyntax creation => creation.WithArgumentList(updated),
            _ => null,
        };
    }

    /// <summary>The lambda's cancellation token parameter: the last one, by the name the analyzer saw.</summary>
    private static ParameterSyntax? TokenParameter(AnonymousFunctionExpressionSyntax lambda, string? declared) => lambda switch
    {
        SimpleLambdaExpressionSyntax simple => simple.Parameter,
        ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters
            .LastOrDefault(parameter => declared is null || parameter.Identifier.ValueText == declared),
        AnonymousMethodExpressionSyntax anonymous => anonymous.ParameterList?.Parameters
            .LastOrDefault(parameter => declared is null || parameter.Identifier.ValueText == declared),
        _ => null,
    };

    /// <summary>
    ///     A name for a parameter that had none. Checked against the whole member rather than the
    ///     lambda, so the new name cannot shadow something the surrounding method is using, and every
    ///     candidate is checked so the name handed back is one nothing in scope has taken.
    /// </summary>
    private static string AvailableName(SyntaxNode lambda)
    {
        var scope = lambda.FirstAncestorOrSelf<MemberDeclarationSyntax>() ?? lambda;

        HashSet<string> taken = new(scope.DescendantTokens()
            .Where(token => token.IsKind(SyntaxKind.IdentifierToken))
            .Select(token => token.ValueText));

        if (!taken.Contains("attempt"))
            return "attempt";

        for (var suffix = 0;; suffix++)
        {
            var candidate = suffix == 0
                ? "attemptToken"
                : "attemptToken" + suffix.ToString(CultureInfo.InvariantCulture);

            if (!taken.Contains(candidate))
                return candidate;
        }
    }
}
