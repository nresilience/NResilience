# Contributing to NResilience

This file is for contributors. If you are trying to use the library, start with the
[README](README.md) or the [documentation](docs/index.md).

## Layout

| Path | What it is |
|---|---|
| `src/NResilience` | The library, including `Http/` - the `HttpClient` handler: request cloning, idempotency, per-host scope. Zero dependencies, `net8.0;net10.0`, AOT-clean, public API manifest checked in. |
| `src/NResilience.Extensions` | `AddResilience()`, configuration binding, the meter and the activity source. |
| `src/NResilience.Testing` | Scripted callbacks and a recording listener. |
| `bench/NResilience.Probes` | The shared suspension gate, the allocation instrument, the cancellation probes, the shipping-executor arms, and Phase 0a's hand-written fused loop - still measured, as the floor the real executor has to beat. |
| `bench/NResilience.Probes.Polly` | The competitive arms, in Polly's native callback shape. |
| `bench/NResilience.Baseline` | Latency trend harness (NBenchmark). Published, never gated. |
| `tests/NResilience.Tests` | The behavioural suite: what the loop does, and when. |
| `tests/NResilience.Gates` | The hard gate: xunit over allocation counters. Depends on no benchmark harness. |
| `tests/NResilience.AotProbe` | The Native AOT gate: publishes clean, then executes the library and re-checks the budgets. |
| `tests/NResilience.Docs` | The docs gate: every snippet in this README and under `docs/`, as compiled and executing code. |
| `tools/NResilience.DocSnippets` | The inliner that puts those snippets into the markdown, and the check that fails when they drift. |
| `samples/` | Three runnable console applications. See [`docs/samples.md`](docs/samples.md). |
| `docs/` | The published documentation. `docs/STYLE.md` is the contributor-facing style guide. |
| `plans/` | The design document and per-phase results. Not published with the docs site. |

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

## Design and phase results

- Design: [`plans/nresilience-design-v3.md`](plans/nresilience-design-v3.md)
- Phase 0a - the baseline, taken before any library code existed: [`plans/phase-0a-results.md`](plans/phase-0a-results.md)
- Phase 0b - the same harness, re-run against the real executor: [`plans/phase-0b-results.md`](plans/phase-0b-results.md)
- Phase 1 - the core: [`plans/phase-1-results.md`](plans/phase-1-results.md)
- Phase 2 - the breaker and the retry budget: [`plans/phase-2-results.md`](plans/phase-2-results.md)
- Phase 3 - telemetry, and what a listener costs: [`plans/phase-3-results.md`](plans/phase-3-results.md)
- Phase 4 - the testing package: [`plans/phase-4-results.md`](plans/phase-4-results.md)
- Phase 5 - the HTTP handler: [`plans/phase-5-results.md`](plans/phase-5-results.md)
- Phase 6 - DI, configuration and the meter: [`plans/phase-6-results.md`](plans/phase-6-results.md)
- Phase 7 - the docs, the samples and the docs gate: [`plans/phase-7-results.md`](plans/phase-7-results.md)