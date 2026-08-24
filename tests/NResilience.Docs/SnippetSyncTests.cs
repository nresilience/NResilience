using NResilience.DocSnippets;

namespace NResilience.Docs;

/// <summary>
///     The docs gate itself: every snippet block in the markdown is the current text of the snippet it
///     names. The same check runs in CI as a build step; having it here as well means a page and its
///     source cannot drift apart without a red test.
/// </summary>
public sealed class SnippetSyncTests
{
    [Fact]
    public void Every_snippet_block_in_the_docs_matches_its_source()
    {
        var root = RepositoryRoot();
        var snippets = SnippetEngine.Collect(sourceRoot: Path.Combine(path1: root, path2: "tests", path3: "NResilience.Docs"));

        Assert.NotEmpty(collection: snippets);

        var drift = SnippetEngine.Sync(docsRoot: root, snippets: snippets, write: false);

        Assert.Empty(collection: drift.Select(d => $"{Path.GetRelativePath(relativeTo: root, path: d.File)}: {d.Detail}"));
    }

    [Fact]
    public void Every_snippet_is_referenced_by_a_page()
    {
        var root = RepositoryRoot();
        var snippets = SnippetEngine.Collect(sourceRoot: Path.Combine(path1: root, path2: "tests", path3: "NResilience.Docs"));

        var pages = Directory
            .EnumerateFiles(path: root, searchPattern: "*.md", searchOption: SearchOption.AllDirectories)
            .Where(file => !file.Contains(value: $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", comparisonType: StringComparison.Ordinal))
            .Select(selector: File.ReadAllText)
            .ToArray();

        // An unreferenced snippet is dead sample code: it still compiles, so nothing else notices.
        var orphans = snippets.Keys
            .Where(name => !pages.Any(page => page.Contains(value: $"<!-- snippet: {name} -->", comparisonType: StringComparison.Ordinal)))
            .ToArray();

        Assert.Equal(expectedSpan: [], actualArray: orphans);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(path: Path.Combine(path1: directory.FullName, path2: "NResilience.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(@object: directory);
        return directory.FullName;
    }
}
