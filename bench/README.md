# bench/

The performance and allocation work for NResilience. Three projects, each with a distinct role.

## Projects

| Project | Role |
|---|---|
| `NResilience.Probes` | The shared harness: the suspension gate, the allocation instrument, the cancellation probes, the shipping-executor arms, and a hand-written fused loop that establishes the floor the real executor has to beat. |
| `NResilience.Probes.Polly` | The competitive arms, using Polly's native callback shape on the same gate and suspension count as every other arm. |
| `NResilience.Baseline` | The latency trend harness (NBenchmark). Published, never gated. |

## Probes and the gate

`NResilience.Probes` and `NResilience.Probes.Polly` are not benchmarks. They define the A/B arms - the suspending path, the synchronous fast path, and retry - that the hard allocation gate in `tests/NResilience.Gates` asserts against. Both reference the same `Gate` (a `Task.Yield` primitive that suspends deterministically on every call), so allocation comparisons are meaningful: every arm suspends the same number of times in the same way.

`NResilience.Probes` also carries the stand-in fused loop (`FusedExecutor`, `LeanFusedExecutor`) that was built before the shipping executor existed. The stand-in is still measured because a stand-in-versus-shipping delta is only meaningful if both sides are captured in one process under one GC and one tier state. `ShippingScenarios` runs the same arms against the real `Resilience` value from `src/NResilience`.

`AllocationProbe` is the instrument behind the gate. It selects the counter based on the shape of the body (thread-local for sync-completing, process-wide for suspending), warms to tier 1, and reports the minimum across repeats - because allocation noise is one-sided and a stray timer can only add bytes.

`CtsFacts` re-measures the cancellation and `TimeProvider` facts the executor's timeout arrangement depends on, on both target TFMs. The pooled-source design relies on `TryReset`, whose behavior differs between the system provider and custom providers.

## Latency trends

`NResilience.Baseline` is the only project here that runs a benchmark harness. Latency is published rather than gated: shared CI runners are noisy enough that a latency gate is either loose enough to catch nothing or tight enough to flake weekly. The hard gate lives in `tests/NResilience.Gates` and depends on no benchmark harness - it is xUnit over allocation counters, which is deterministic and fails with a byte count.

Run the latency harness:

```bash
dotnet run -c Release -f net10.0 --project bench/NResilience.Baseline
dotnet run -c Release -f net10.0 --project bench/NResilience.Baseline -- --category socket
dotnet run -c Release -f net10.0 --project bench/NResilience.Baseline -- --reporter json --output baseline.json
```

Reports land under `artifacts/`, which `.gitignore` covers. The categories are `suspending` (the path every real I/O call takes), `sync` (the synchronous fast path, where the 0-byte budgets live), `retry` (per-attempt cost), and `socket` (a real loopback TCP round trip that cross-checks the `Task.Yield` gate against actual I/O).

See [CONTRIBUTING.md](../CONTRIBUTING.md) for how the bench projects fit against the tests and the design.