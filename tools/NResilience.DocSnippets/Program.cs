using NResilience.DocSnippets;

// The docs gate, as a command. `--check` is what CI runs; `--write` is what an author runs.
//
//   dotnet run --project tools/NResilience.DocSnippets -- --write
//   dotnet run --project tools/NResilience.DocSnippets -- --check
bool write = args.Contains("--write", StringComparer.Ordinal);
string repositoryRoot = Path.GetFullPath(
    args.FirstOrDefault(static a => !a.StartsWith("--", StringComparison.Ordinal))
    ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

string sources = Path.Combine(repositoryRoot, "tests", "NResilience.Docs");
if (!Directory.Exists(sources))
{
    Console.Error.WriteLine($"No snippet project at {sources}.");
    return 2;
}

Dictionary<string, Snippet> snippets = SnippetEngine.Collect(sources);
IReadOnlyList<Drift> drift = SnippetEngine.Sync(repositoryRoot, snippets, write);

Console.WriteLine($"{snippets.Count} snippet(s) from {sources}.");

if (drift.Count == 0)
{
    Console.WriteLine("Every snippet block in the markdown matches its source.");
    return 0;
}

foreach (Drift item in drift)
{
    Console.WriteLine($"{(write ? "updated" : "STALE")}: {Path.GetRelativePath(repositoryRoot, item.File)} - {item.Detail}");
}

if (write)
{
    Console.WriteLine($"{drift.Count} file(s) rewritten.");
    return 0;
}

Console.Error.WriteLine("Run: dotnet run --project tools/NResilience.DocSnippets -- --write");
return 1;
