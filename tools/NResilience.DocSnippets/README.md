# NResilience.DocSnippets

The docs gate. Every C# and JSON sample in `docs/` and the README lives as compiled,
executing code in `tests/NResilience.Docs/`, and this tool inlines it into the markdown.
A sample that stops compiling, stops passing, or drifts from its page fails the build.

## Commands

```bash
# CI: fail if any markdown block has drifted from its source
dotnet run --project tools/NResilience.DocSnippets -- --check

# Author: regenerate every inlined block from the sources
dotnet run --project tools/NResilience.DocSnippets -- --write
```

The same gate runs as a test in `tests/NResilience.Docs/SnippetSyncTests.cs`, so a page
and its source cannot drift apart without a red test.

## Snippet markers

Wrap a region of C# with these markers. The text between them is inlined into any
markdown page that names the snippet in a `<!-- snippet: name -->` ... `<!-- endsnippet -->`
block.

Source:

```csharp
// <snippet:my-snippet>
var api = Resilience.Default with { Attempts = 3 };
// </snippet:my-snippet>
```

Markdown (the gate replaces whatever sits between the markers with the inlined text):

The page opens a block with `<!-- snippet: name -->` on its own line and closes it with
`<!-- endsnippet -->`. Between them sits a fenced ` ```csharp ` block; the gate overwrites
its contents with the inlined text on every `--write` / `--check` run. See any file under
`docs/` for a worked example.

The inlined text is the source between the markers, dedented. Open and close markers
must be balanced within a single file; a close with no matching open throws
`InvalidOperationException: Stack empty` during collection.

## Directives

Two line-level directives live inside a snippet body. Both are dropped from the inlined
output; both are recognised by `StartsWith` on the trimmed line, so indentation does not
matter.

### `// snippet-hide`

Drops the line from the inlined output. Use it to keep scaffolding in the source that
the reader does not need.

```csharp
// <snippet:demo>
var budget = RetryBudget.Shared("payments");
var charge = Resilience.Http with { Budget = budget };
// snippet-hide
var unused = 42;   // test assertion setup the reader does not need
// </snippet:demo>
```

### `// snippet-show:`

Replaces the next source line with the payload that follows the colon. A run of `K`
directives replaces the next `K` source lines, in order.

Use it when the source must compile one way (positional arguments, IDE-cleanup-friendly)
while the docs read another (named arguments, reader-friendly):

```csharp
// <snippet:demo>
// snippet-show: var generous = Resilience.Default with { Budget = RetryBudget.Of(fraction: 0.2, minimumPerSecond: 10) };
var generous = Resilience.Default with { Budget = RetryBudget.Of(0.2, 10) };
// </snippet:demo>
```

The inlined block shows the payload - `RetryBudget.Of(fraction: 0.2, minimumPerSecond: 10)` -
while the source compiles with the positional form the IDE's cleanup produces.

A run of directives replaces a run of lines:

```csharp
// <snippet:demo>
// snippet-show:     Backoff = Backoff.Exponential(
// snippet-show:         transientBase: TimeSpan.FromMilliseconds(200),   // first delay
// snippet-show:         max: TimeSpan.FromSeconds(10)),                  // cap
    Backoff = Backoff.Exponential(
        TimeSpan.FromMilliseconds(200), // first delay
        TimeSpan.FromSeconds(10)), // cap
// </snippet:demo>
```

The payload inherits the source line's leading whitespace, so dedent normalises it
alongside its neighbours regardless of how the directive is indented.

> [!IMPORTANT]
> If `snippet-show` directives outnumber the following source lines, collection throws
> `InvalidOperationException` naming the snippet and source file. A surplus directive is a
> typo the author wants to see immediately, not silent drift.

## How the gate fits together

1. `Collect` walks `tests/NResilience.Docs/`, reads every `.cs` and `.json` file, and
   builds a `name -> Snippet` map from the markers above.
2. `Sync` walks every `*.md` under the repo root. For each `<!-- snippet: name -->`
   block it replaces whatever is between the markers with the snippet's text and reports
   the file as drift if the content changed.
3. `--write` rewrites the drifted files; `--check` (and the test) fails on any drift.