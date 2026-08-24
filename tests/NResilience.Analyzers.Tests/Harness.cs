using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace NResilience.Analyzers.Tests;

/// <summary>
/// Compiles a snippet the way a consumer's project would and runs the analyzers over it.
/// <para>
/// Every compilation is asserted to be error-free first. A snippet with a typo produces no
/// analyzer diagnostics either, and a test that cannot tell those two outcomes apart passes for
/// the wrong reason.
/// </para>
/// </summary>
internal static class Harness
{
    private static readonly ImmutableArray<MetadataReference> References = ImmutableArray.CreateRange(
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(static path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path)));

    /// <summary>The usings a consumer would have; the snippets are about the calls, not the header.</summary>
    private const string Header = """
        using System;
        using System.Net.Http;
        using System.Net.Http.Json;
        using System.Threading;
        using System.Threading.Tasks;
        using NResilience;
        using NResilience.Http;

        """;

    internal static ImmutableArray<Diagnostic> Run(string source, OutputKind kind = OutputKind.DynamicallyLinkedLibrary)
    {
        var compilation = Compile(source, kind);

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(
                new AttemptTokenAnalyzer(),
                new PolicyConfigurationAnalyzer(),
                new PerCallStateAnalyzer(),
                new RedundantAsyncCallbackAnalyzer()));

        return withAnalyzers.GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
    }

    /// <summary>The ids raised over a snippet, in source order, so a test reads as a claim about the whole snippet.</summary>
    internal static string[] Ids(string source, OutputKind kind = OutputKind.DynamicallyLinkedLibrary) =>
        Run(source, kind)
            .OrderBy(static diagnostic => diagnostic.Location.SourceSpan.Start)
            .Select(static diagnostic => diagnostic.Id)
            .ToArray();

    /// <summary>A snippet wrapped in the method a consumer would have written it in.</summary>
    internal static string InMethod(string body) => Header + $$"""
        internal sealed class Target
        {
            private static readonly HttpClient Client = new();

            internal async Task Run(Uri url, CancellationToken cancellationToken)
            {
                var api = Resilience.Http;
        {{body}}
            }

            internal static Task<int> Helper(CancellationToken cancellationToken = default) => Task.FromResult(1);

            internal static Task<int> Numbered(int value, CancellationToken cancellationToken = default) => Task.FromResult(value);

            internal static Task<int> Optional(int first = 1, int second = 2, CancellationToken cancellationToken = default) => Task.FromResult(first);
        }
        """;

    /// <summary>A whole file, for the rules that are about where a declaration lives.</summary>
    internal static string InFile(string source) => Header + source;

    /// <summary>Applies the one fix offered for the first diagnostic, and returns the resulting text.</summary>
    internal static string ApplyFix(string source)
    {
        using var workspace = new AdhocWorkspace();

        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);

        var solution = workspace.CurrentSolution
            .AddProject(projectId, "Snippet", "Snippet", LanguageNames.CSharp)
            .WithProjectCompilationOptions(projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithProjectParseOptions(projectId, new CSharpParseOptions(LanguageVersion.Latest))
            .AddMetadataReferences(projectId, References)
            .AddDocument(documentId, "Snippet.cs", SourceText.From(source));

        var document = solution.GetDocument(documentId)!;
        var diagnostic = Run(source)
            .OrderBy(static reported => reported.Location.SourceSpan.Start)
            .First();

        var actions = new List<CodeAction>();
        var provider = new AttemptTokenCodeFixProvider();

        provider.RegisterCodeFixesAsync(new CodeFixContext(
                document,
                diagnostic,
                (action, _) => actions.Add(action),
                TestContext.Current.CancellationToken))
            .GetAwaiter()
            .GetResult();

        var fix = Assert.Single(actions);

        var change = fix
            .GetOperationsAsync(TestContext.Current.CancellationToken)
            .GetAwaiter()
            .GetResult()
            .OfType<ApplyChangesOperation>()
            .Single();

        return change.ChangedSolution
            .GetDocument(documentId)!
            .GetTextAsync(TestContext.Current.CancellationToken)
            .GetAwaiter()
            .GetResult()
            .ToString();
    }

    private static Compilation Compile(string source, OutputKind kind)
    {
        var compilation = CSharpCompilation.Create(
            "Snippet",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            References,
            new CSharpCompilationOptions(kind, nullableContextOptions: NullableContextOptions.Enable));

        var errors = compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.ToString())
            .ToArray();

        Assert.Equal([], errors);
        return compilation;
    }
}
