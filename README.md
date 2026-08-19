# NResilience

A .NET resilience library built around one flat execution engine, a declarative policy value, and
defaults that are correct without configuration.

**Status: Phase 0a.** There is no library code yet, deliberately. Phase 0a is the falsification
test for the design — it establishes the measurement harness, the competitive baseline and the CI
gates *before* anything is built on the assumptions they check.

- Design: [`plans/nresilience-design-v3.md`](plans/nresilience-design-v3.md)
- Phase 0a results and what they changed: [`plans/phase-0a-results.md`](plans/phase-0a-results.md)

## Layout

| Path | What it is |
|---|---|
| `bench/NResilience.Probes` | The hand-written fused loop that stands in for the executor, the shared suspension gate, the allocation instrument and the cancellation probes. No dependencies. |
| `bench/NResilience.Probes.Polly` | The competitive arms, in Polly's native callback shape. |
| `bench/NResilience.Baseline` | Latency trend harness (NBenchmark). Published, never gated. |
| `tests/NResilience.Gates` | The hard gate: xunit over allocation counters. Depends on no benchmark harness. |
| `tests/NResilience.AotProbe` | The Native AOT gate: publishes clean, then executes a policy and re-checks the budgets. |

## Running the gates

```bash
# Hard gate. Release only - the budgets were measured against tier-1 code.
dotnet test tests/NResilience.Gates -c Release

# The same, printing every measured number.
dotnet test tests/NResilience.Gates -c Release -l "console;verbosity=detailed"

# Native AOT gate.
dotnet publish tests/NResilience.AotProbe -c Release -f net10.0 -warnaserror
./tests/NResilience.AotProbe/bin/Release/net10.0/*/publish/NResilience.AotProbe

# Latency trend. Not a gate.
dotnet run -c Release -f net10.0 --project bench/NResilience.Baseline -- --exclude-category socket
```

Target frameworks are `net8.0` and `net10.0`, and both run in CI, because "no cliff on net8" is a
claim the project makes.
