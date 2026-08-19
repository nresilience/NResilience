# NResilience

A .NET resilience library built around one flat execution engine, a declarative policy value, and
defaults that are correct without configuration.

**Status: Phase 1.** The core ships: `Resilience`, `Verdict`, `Classifier`, `Backoff`,
`CallResult<T>`, `AttemptLog`, the exception types and the fused executor — deadline, attempt
timeout, retry, classification and the cancellation contract. Circuit breaking and the retry budget
are Phase 2; telemetry is Phase 3.

- Design: [`plans/nresilience-design-v3.md`](plans/nresilience-design-v3.md)
- Phase 0a — the baseline, taken before any library code existed: [`plans/phase-0a-results.md`](plans/phase-0a-results.md)
- Phase 1 — what shipped, and the decisions Phase 0a deferred: [`plans/phase-1-results.md`](plans/phase-1-results.md)

## The whole API, in thirty seconds

```csharp
// 1. A policy is a value. Presets are the entry point.
var api = Resilience.Http;

// 2. Derive with `with`. No builder, no Build(), no ordering to get right.
var slow = Resilience.Http with { Attempts = 5, Deadline = TimeSpan.FromSeconds(20) };

// 3. Run anything. One method, any return type, nothing to declare.
var user     = await api.RunAsync(ct => client.GetFromJsonAsync<User>(url, ct), ct);
var response = await api.RunAsync(ct => client.GetAsync(url, ct), ct);
await api.RunAsync(ct => queue.FlushAsync(ct), ct);

// 4. Fallback is not a strategy. It is an `if`.
var result = await api.TryRunAsync(ct => FetchAsync(ct), ct);
return result.TryGetValue(out var value) ? value : cache.LastKnownGood;
```

There is no pipeline, no builder, no strategy, no context, no property bag and no ordering.

## Where the allocations are

Bytes above an identical un-wrapped callback, measured in one process on .NET 8 and .NET 10.
Conditions and the full arm list are in
[`plans/phase-1-results.md`](plans/phase-1-results.md).

| Scenario | Overhead |
|---|---:|
| `Resilience.None`, any callback | **0** |
| Sync-completing, no attempt timeout, static lambda + state | **0** |
| Sync-completing, full policy, static lambda + state | 64 B — one linked cancellation source |
| Suspending, full policy | **384 B** — one state-machine box plus the linked source |
| Suspending, Polly retry + timeout, same harness | 1,292 B |

**3.4× lower overhead than Polly for a realistic policy, and none at all for a trivial one.** The
fused design wins in proportion to how much policy is configured, because composition overhead
scales with layer count and a flat loop's does not. That is the whole claim, and it is measured
rather than asserted.

"Zero allocation" is never claimed unqualified: every `async` frame that *suspends* heap-allocates
its own state-machine box, and no library-side trick removes it.

## Layout

| Path | What it is |
|---|---|
| `src/NResilience` | The library. Zero dependencies, `net8.0;net10.0`, AOT-clean, public API manifest checked in. |
| `bench/NResilience.Probes` | Phase 0a's hand-written fused loop, the shared suspension gate, the allocation instrument and the cancellation probes. |
| `bench/NResilience.Probes.Polly` | The competitive arms, in Polly's native callback shape. |
| `bench/NResilience.Baseline` | Latency trend harness (NBenchmark). Published, never gated. |
| `tests/NResilience.Tests` | The behavioural suite: what the loop does, and when. |
| `tests/NResilience.Gates` | The hard gate: xunit over allocation counters. Depends on no benchmark harness. |
| `tests/NResilience.AotProbe` | The Native AOT gate: publishes clean, then executes the library and re-checks the budgets. |

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

# Latency trend. Not a gate.
dotnet run -c Release -f net10.0 --project bench/NResilience.Baseline -- --exclude-category socket
```

Target frameworks are `net8.0` and `net10.0`, and both run in CI, because "no cliff on net8" is a
claim the project makes.
