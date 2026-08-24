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
        var snippets = SnippetEngine.Collect(Path.Combine(root, "tests", "NResilience.Docs"));

        Assert.NotEmpty(snippets);

        var drift = SnippetEngine.Sync(root, snippets, false);

        Assert.Empty(drift.Select(d => $"{Path.GetRelativePath(root, d.File)}: {d.Detail}"));
    }

    [Fact]
    public void Every_snippet_is_referenced_by_a_page()
    {
        var root = RepositoryRoot();
        var snippets = SnippetEngine.Collect(Path.Combine(root, "tests", "NResilience.Docs"));

        var pages = Directory
            .EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToArray();

        // An unreferenced snippet is dead sample code: it still compiles, so nothing else notices.
        var orphans = snippets.Keys
            .Where(name => !pages.Any(page => page.Contains($"<!-- snippet: {name} -->", StringComparison.Ordinal)))
            .ToArray();

        Assert.Equal([], orphans);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NResilience.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
