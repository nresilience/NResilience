# NResilience

A .NET resilience library built around one flat execution engine, a declarative policy value, and
defaults that are correct without configuration.

**Status: Phase 7 complete - all seven build phases are done.** The four packages ship:
`NResilience` (the policy value and the fused executor), `NResilience.Http` (the handler),
`NResilience.Extensions` (DI, configuration, metrics and traces) and `NResilience.Testing` (scripted
callbacks and a recording listener). Every allocation gate measures the shipping executor, every
documentation snippet is compiled and executed in CI, and Native AOT publishes clean on both target
frameworks.

**Documentation: [`docs/`](docs/index.md)** - [quick start](docs/getting-started/quick-start.md),
[guides](docs/guides/index.md), [features](docs/features/index.md),
[reference](docs/reference/index.md), [deep dives](docs/deep-dives/index.md),
[migrating from Polly](docs/migrating-from-polly.md),
[troubleshooting](docs/troubleshooting.md) and the [FAQ](docs/faq.md).

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

## The whole API, in thirty seconds

<!-- snippet: whole-api -->
```csharp
// 1. A policy is a value. Presets are the entry point.
var api = Resilience.Http;

// 2. Derive with `with`. No builder, no Build(), no ordering to get right.
var slow = Resilience.Http with { Attempts = 5, Deadline = TimeSpan.FromSeconds(20) };

// 3. Run anything. One method, any return type, nothing to declare.
User? user = await api.RunAsync(ct => client.GetFromJsonAsync<User>(url, ct), cancellationToken);
HttpResponseMessage response = await api.RunAsync(ct => client.GetAsync(url, ct), cancellationToken);
await slow.RunAsync(ct => queue.FlushAsync(ct), cancellationToken);

// 4. Fallback is not a strategy. It is an `if`.
CallResult<User> result = await api.TryRunAsync(ct => FetchAsync(ct), cancellationToken);
User best = result.TryGetValue(out User? fetched) ? fetched : cache.LastKnownGood;
```
<!-- endsnippet -->

There is no pipeline, no builder, no strategy, no context, no property bag and no ordering.

## Where the allocations are

Bytes above an identical un-wrapped callback, measured in one process on .NET 8 and .NET 10.
Conditions and the full arm list are in
[`plans/phase-0b-results.md`](plans/phase-0b-results.md).

| Scenario | Overhead |
|---|---:|
| `Resilience.None`, any callback | **0** |
| Sync-completing, no attempt timeout, static lambda + state | **0** |
| Sync-completing, full policy, static lambda + state | 64 B — one linked cancellation source |
| Suspending, full policy | **384 B** — one state-machine box plus the linked source |
| Suspending, Polly retry + timeout, same harness | 1,291 B |
| Suspending, full policy, over a real loopback socket | 528 B |
| Suspending, Polly retry + timeout, same socket | 1,296 B |

**3.4× lower overhead than Polly for a realistic policy on the yield gate, and 2.5× over a real
socket.** The fused design wins in proportion to how much policy is configured, because composition
overhead scales with layer count and a flat loop's does not. At the other end that cuts the other way,
and the number is published rather than buried: the smallest policy this library can express costs
320 B against 304 B for a Polly pipeline with no strategies in it — the two are not doing the same work,
but at the trivial end there is nothing to win. Every figure here is a test that fails the build.

"Zero allocation" is never claimed unqualified: every `async` frame that *suspends* heap-allocates
its own state-machine box, and no library-side trick removes it.

## Layout

| Path | What it is |
|---|---|
| `src/NResilience` | The library. Zero dependencies, `net8.0;net10.0`, AOT-clean, public API manifest checked in. |
| `src/NResilience.Http` | The `HttpClient` handler: request cloning, idempotency, per-host scope. |
| `src/NResilience.Extensions` | `AddResilience()`, configuration binding, the meter and the activity source. |
| `src/NResilience.Testing` | Scripted callbacks and a recording listener. |
| `bench/NResilience.Probes` | The shared suspension gate, the allocation instrument, the cancellation probes, the shipping-executor arms, and Phase 0a's hand-written fused loop — still measured, as the floor the real executor has to beat. |
| `bench/NResilience.Probes.Polly` | The competitive arms, in Polly's native callback shape. |
| `bench/NResilience.Baseline` | Latency trend harness (NBenchmark). Published, never gated. |
| `tests/NResilience.Tests` | The behavioural suite: what the loop does, and when. |
| `tests/NResilience.Gates` | The hard gate: xunit over allocation counters. Depends on no benchmark harness. |
| `tests/NResilience.AotProbe` | The Native AOT gate: publishes clean, then executes the library and re-checks the budgets. |
| `tests/NResilience.Docs` | The docs gate: every snippet in this README and under `docs/`, as compiled and executing code. |
| `tools/NResilience.DocSnippets` | The inliner that puts those snippets into the markdown, and the check that fails when they drift. |
| `samples/` | Three runnable console applications. See [`docs/samples.md`](docs/samples.md). |
| `docs/` | The published documentation. `docs/STYLE.md` is the contributor-facing style guide. |

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
