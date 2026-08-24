namespace NResilience.DocSnippets;

/// <summary>A single snippet extracted from a source file.</summary>
public sealed record Snippet(string Name, string Language, string Text, string Source);

/// <summary>A markdown file where snippet blocks do not match the sources.</summary>
public sealed record Drift(string File, string Detail);

/// <summary>
///     The documentation gate: snippets live in a compiled, executing test project and are
///     inlined into the markdown. This ensures that no C# code in the documentation is
///     maintained by hand.
/// </summary>
public static class SnippetEngine
{
    private const string OpenPrefix = "<!-- snippet:";
    private const string CloseMarker = "<!-- endsnippet -->";
    private const string HidePrefix = "// snippet-hide";
    private const string ShowPrefix = "// snippet-show:";

    /// <summary>Reads all snippets from a source tree.</summary>
    public static Dictionary<string, Snippet> Collect(string sourceRoot)
    {
        var snippets = new Dictionary<string, Snippet>(StringComparer.Ordinal);

        foreach (var file in SourceFiles(sourceRoot))
        {
            var extension = Path.GetExtension(file);

            if (extension is ".json")
            {
                Add(snippets, new Snippet(Path.GetFileName(file), "json", File.ReadAllText(file).TrimEnd(), file));
                continue;
            }

            foreach (var snippet in FromSource(file))
            {
                Add(snippets, snippet);
            }
        }

        return snippets;
    }

    /// <summary>Rewrites all snippet blocks in a markdown tree and reports changes.</summary>
    public static IReadOnlyList<Drift> Sync(string docsRoot, Dictionary<string, Snippet> snippets, bool write)
    {
        var drift = new List<Drift>();

        foreach (var file in MarkdownFiles(docsRoot))
        {
            var original = File.ReadAllText(file);
            var updated = Rewrite(file, original, snippets, drift);

            if (!string.Equals(original, updated, StringComparison.Ordinal))
            {
                drift.Add(new Drift(file, "the inlined snippets are out of date"));

                if (write)
                    File.WriteAllText(file, updated);
            }
        }

        return drift;
    }

    private static string Rewrite(string file, string markdown, Dictionary<string, Snippet> snippets, List<Drift> drift)
    {
        var newline = markdown.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = markdown.Split(["\r\n", "\n"], StringSplitOptions.None);
        var output = new List<string>(lines.Length);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            output.Add(line);

            if (!line.TrimStart().StartsWith(OpenPrefix, StringComparison.Ordinal))
                continue;

            var name = line.Trim()[OpenPrefix.Length..].TrimEnd('>', '-', ' ').Trim();

            // Skip whatever is currently between the markers; it is generated content.
            var end = i + 1;

            while (end < lines.Length && !lines[end].TrimStart().StartsWith(CloseMarker, StringComparison.Ordinal))
            {
                end++;
            }

            if (end == lines.Length)
            {
                drift.Add(new Drift(file, $"snippet \"{name}\" has no {CloseMarker}"));
                continue;
            }

            if (!snippets.TryGetValue(name, out var snippet))
            {
                drift.Add(new Drift(file, $"snippet \"{name}\" does not exist in the snippet project"));
                i = end - 1;
                continue;
            }

            output.Add("```" + snippet.Language);
            output.AddRange(snippet.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
            output.Add("```");
            i = end - 1;
        }

        // Joined rather than appended line by line, so a file that does not end in a newline does not
        // acquire one and a file that does keeps exactly the one it had.
        return string.Join(newline, output);
    }

    private static IEnumerable<Snippet> FromSource(string file)
    {
        var lines = File.ReadAllLines(file);
        var open = new Stack<(string Name, int Start)>();

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.StartsWith("// <snippet:", StringComparison.Ordinal))
            {
                open.Push((trimmed["// <snippet:".Length..].TrimEnd('>'), i + 1));
                continue;
            }

            if (trimmed.StartsWith("// </snippet:", StringComparison.Ordinal))
            {
                var (name, start) = open.Pop();
                yield return new Snippet(name, "csharp", Dedent(name, file, lines[start..i]), file);
            }
        }
    }

    private static string Dedent(string name, string source, string[] body)
    {
        // `snippet-show:` lets the source compile one way (positional args, IDE-friendly) while the
        // inlined docs read another (named args, reader-friendly). Each directive replaces the next
        // non-directive source line with its payload; a run of K directives replaces K source lines.
        var show = new Queue<string>();
        var kept = new List<string>(body.Length);

        foreach (var line in body)
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith(HidePrefix, StringComparison.Ordinal))
                continue;

            if (TryTakeShow(trimmed, out var payload))
            {
                show.Enqueue(payload);
                continue;
            }

            kept.Add(show.Count > 0 ? IndentPayload(line, show.Dequeue()) : line);
        }

        if (show.Count > 0)
        {
            throw new InvalidOperationException(
                $"Snippet \"{name}\" in {source} has {show.Count} surplus `snippet-show:` directive(s) "
                + "with no source line to replace.");
        }

        var indent = kept
            .Where(static line => line.Trim().Length > 0)
            .Select(static line => line.Length - line.TrimStart().Length)
            .DefaultIfEmpty(0)
            .Min();

        return string.Join(
            '\n',
            kept.Select(line => line.Trim().Length == 0 ? string.Empty : line[indent..])).Trim('\n');
    }

    private static bool TryTakeShow(string trimmed, out string payload)
    {
        if (!trimmed.StartsWith(ShowPrefix, StringComparison.Ordinal))
        {
            payload = string.Empty;
            return false;
        }

        // Everything after `// snippet-show:` is the display payload, verbatim. A single leading
        // space is tolerated so `// snippet-show: foo` and `// snippet-show:foo` both yield `foo`.
        payload = trimmed[ShowPrefix.Length..].TrimStart(' ');
        return true;
    }

    private static string IndentPayload(string source, string payload)
    {
        // The payload inherits the source line's leading whitespace so Dedent normalises it
        // alongside its neighbours, regardless of how the author wrote the directive.
        var leading = source.Length - source.TrimStart().Length;
        return (leading > 0 ? source[..leading] : string.Empty) + payload;
    }

    private static void Add(Dictionary<string, Snippet> snippets, Snippet snippet)
    {
        if (!snippets.TryAdd(snippet.Name, snippet))
        {
            throw new InvalidOperationException(
                $"Snippet \"{snippet.Name}\" is defined twice: {snippets[snippet.Name].Source} and {snippet.Source}.");
        }
    }

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(static file => Path.GetExtension(file) is ".cs" or ".json")
            .Where(static file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(static file => file, StringComparer.Ordinal);

    private static IEnumerable<string> MarkdownFiles(string root) =>
        Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .Where(static file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(static file => file, StringComparer.Ordinal);
}
