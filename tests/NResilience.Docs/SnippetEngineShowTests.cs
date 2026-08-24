using System.Text;

namespace NResilience.Docs;

/// <summary>
///     Focused tests for the <c>snippet-show</c> directive. The end-to-end <see cref="SnippetSyncTests" />
///     gate only checks that real snippets match real markdown; these tests exercise the directive
///     itself in isolation - replacement, indentation, and the overflow guard.
///     The snippet markers in the source strings below are assembled at runtime so the literal
///     sequence <c>// &lt;snippet:</c> never appears at the start of a line in this file. The docs
///     gate's <see cref="SnippetSyncTests.Every_snippet_is_referenced_by_a_page" /> collects every
///     <c>&lt;snippet:&gt;</c> marker under the test project and asserts each is referenced by a
///     markdown page; a hand-written marker here would be an unreferenced snippet and fail the gate.
/// </summary>
public sealed class SnippetEngineShowTests
{
    // The collector recognises a line that starts (after trimming) with this exact prefix.
    private const string Open = "// <snippet:";
    private const string Close = "// </snippet:";

    [Fact]
    public void Snippet_show_replaces_the_next_source_line_with_its_payload()
    {
        var source = BuildSource("demo", """
                                             // snippet-show: var x = RetryBudget.Of(fraction: 0.2, minimumPerSecond: 10);
                                             var x = RetryBudget.Of(0.2, 10);
                                         """);

        var snippet = CollectSingle("demo", source);

        Assert.Equal(
            "var x = RetryBudget.Of(fraction: 0.2, minimumPerSecond: 10);",
            snippet.Text);
    }

    [Fact]
    public void A_run_of_snippet_show_directives_replaces_an_equal_run_of_source_lines()
    {
        var source = BuildSource("demo", """
                                             // snippet-show:     Backoff = Backoff.Exponential(
                                             // snippet-show:         transientBase: TimeSpan.FromMilliseconds(200),   // first
                                             // snippet-show:         max: TimeSpan.FromSeconds(10)),                  // cap
                                             Backoff = Backoff.Exponential(
                                                 TimeSpan.FromMilliseconds(200), // first
                                                 TimeSpan.FromSeconds(10)), // cap
                                         """);

        var snippet = CollectSingle("demo", source);

        var expected = string.Join('\n', "Backoff = Backoff.Exponential(", "    transientBase: TimeSpan.FromMilliseconds(200),   // first",
            "    max: TimeSpan.FromSeconds(10)),                  // cap");

        Assert.Equal(expected, snippet.Text);
    }

    [Fact]
    public void A_directive_with_no_following_source_line_is_an_error()
    {
        var source = BuildSource("demo", """
                                             var x = 1;
                                             // snippet-show: var y = 2;
                                         """);

        var ex = Assert.Throws<InvalidOperationException>(() => CollectSingle("demo", source));
        Assert.Contains("demo", ex.Message, StringComparison.Ordinal);
        Assert.Contains("surplus", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_snippet_without_directives_inlines_verbatim_as_before()
    {
        var source = BuildSource("demo", """
                                             var x = RetryBudget.Of(fraction: 0.2, minimumPerSecond: 10);
                                         """);

        var snippet = CollectSingle("demo", source);

        Assert.Equal(
            "var x = RetryBudget.Of(fraction: 0.2, minimumPerSecond: 10);",
            snippet.Text);
    }

    private static string BuildSource(string name, string body) =>

        // Assemble the marker lines at runtime so this file has no literal `// <snippet:` line
        // for the docs gate to collect.
        $$"""
          using System;

          public sealed class Example
          {
              public void Go()
              {
                  {{Open + name + ">"}}
          {{body.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n')}}
                  {{Close + name + ">"}}
              }
          }
          """;

    private static Snippet CollectSingle(string name, string source)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NResilience.SnippetEngineShowTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            File.WriteAllText(Path.Combine(dir, "Sample.cs"), source.Replace("\r\n", "\n", StringComparison.Ordinal), Encoding.UTF8);

            var snippets = SnippetEngine.Collect(dir);
            Assert.True(snippets.TryGetValue(name, out var snippet), $"snippet \"{name}\" was not collected");
            return snippet;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
