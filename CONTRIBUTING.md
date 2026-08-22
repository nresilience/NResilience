# Contributing to NResilience

This file is for contributors. If you are trying to use the library, start with the
[README](README.md) or the [documentation](docs/index.md).

## Layout

| Path | What it is |
| --- | --- |
| `src/NResilience` | The library, including `Http/` - the `HttpClient` handler: request cloning, idempotency, per-host scope. Zero dependencies, `net8.0;net10.0`, AOT-clean, public API manifest checked in. |
| `src/NResilience.Extensions` | `AddResilience()`, configuration binding, the meter and the activity source. |
| `src/NResilience.Testing` | Scripted callbacks and a recording listener. |
| `src/NResilience.Analyzers` | The seven diagnostics, `netstandard2.0`, no Workspaces reference. Packed into the `NResilience` package under `analyzers/dotnet/cs`. Rule ids are tracked in `AnalyzerReleases.*.md` the way members are in `PublicAPI.*.txt`. |
| `src/NResilience.CodeFixes` | The fixes for NRES001 and NRES002. A separate assembly because RS1038 is right: a code fix references Workspaces, which the command-line compiler does not provide. |
| `bench/NResilience.Probes` | The shared suspension gate, the allocation instrument, the cancellation probes, the shipping-executor arms, and the hand-written fused loop - still measured, as the floor the real executor has to beat. |
| `bench/NResilience.Probes.Polly` | The competitive arms, in Polly's native callback shape. |
| `bench/NResilience.Baseline` | Latency trend harness (NBenchmark). Published, never gated. |
| `tests/NResilience.Tests` | The behavioural suite: what the loop does, and when. |
| `tests/NResilience.Gates` | The hard gate: xunit over allocation counters. Depends on no benchmark harness. |
| `tests/NResilience.AotProbe` | The Native AOT gate: publishes clean, then executes the library and re-checks the budgets. |
| `tests/NResilience.Docs` | The docs gate: every snippet in this README and under `docs/`, as compiled and executing code. |
| `tests/NResilience.Analyzers.Tests` | The analyzer suite: snippets of consumer code compiled in-process and handed to the analyzers. No analyzer test framework - the harness is thirty lines and pins the Roslyn version the analyzers claim to support. |
| `tools/NResilience.DocSnippets` | The inliner that puts those snippets into the markdown, and the check that fails when they drift. |
| `samples/` | Three runnable console applications. See [`docs/samples.md`](docs/samples.md). |
| `docs/` | The published documentation. `AGENTS.md` holds the contributor-facing documentation style guide. |

## Running everything

```bash
# Behaviour.
dotnet test tests/NResilience.Tests -c Release

# Hard gate. Release only - the budgets were measured against tier-1 code.
dotnet test tests/NResilience.Gates -c Release

# The same, printing every measured number.
dotnet test tests/NResilience.Gates -c Release -l "console;verbosity=detailed"

# Native AOT gate.
dotnet publish tests/NResilience.AotProbe -c Release -f net10.0 -warnaserror
./tests/NResilience.AotProbe/bin/Release/net10.0/*/publish/NResilience.AotProbe

# The analyzers.
dotnet test tests/NResilience.Analyzers.Tests -c Release

# Docs gate: the snippets compile, execute, and match the pages.
dotnet test tests/NResilience.Docs -c Release
dotnet run --project tools/NResilience.DocSnippets -- --check

# Re-inline the snippets after editing one.
dotnet run --project tools/NResilience.DocSnippets -- --write

# Latency trend. Not a gate.
dotnet run -c Release -f net10.0 --project bench/NResilience.Baseline -- --exclude-category socket
```

Target frameworks are `net8.0` and `net10.0`, and both run in CI, because "no cliff on net8" is a
claim the project makes.

## The analyzers run on our own consumer code

`tests/NResilience.Docs` and `samples/` reference `src/NResilience.Analyzers` with
`OutputItemType="Analyzer"`, so every published snippet and every sample is analyzed the way a
consumer's project would be - and with `TreatWarningsAsErrors` on, a snippet that teaches the wrong
token cannot be committed. Where a snippet trips a rule on purpose (a policy that is invalid because
the page is about the error message) the suppression goes *outside* the snippet markers, so a
`#pragma` never appears on a page.

`tests/NResilience.Tests` and `tests/NResilience.Gates` are deliberately not analyzed: the
behavioural suite exercises the footguns on purpose, and a rule that fires on a test asserting the
footgun's behavior is measuring the wrong thing.
