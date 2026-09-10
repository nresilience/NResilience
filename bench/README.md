# bench/

The performance and allocation work for NResilience. Four projects, each with a distinct role.

## Projects

| Project | Role |
|---|---|
| `NResilience.Probes` | The shared harness: the suspension gate, the allocation instrument, the cancellation probes, the shipping-executor arms, and a hand-written fused loop that establishes the floor the real executor has to beat. |
| `NResilience.Probes.Polly` | The competitive arms, using Polly's native callback shape on the same gate and suspension count as every other arm. |
| `NResilience.Baseline` | The latency trend harness (NBenchmark). Published, never gated. |
| `NResilience.Stress` | The sustained-load stress suite: throughput, latency percentiles under load, allocations, GC collections and GC pauses per (arm, DOP, GC mode), NResilience vs Polly. Published, never gated. |

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

## Stress suite

`NResilience.Stress` is the sustained-load harness: the axis neither the allocation gate (sequential, per-call bytes) nor the latency harness (single-threaded, per-op time) covers. It measures what a process actually sustains and what it costs to sustain it - throughput, allocation, GC collection counts and pause cost, latency at a stated offered load, and the amplification a dependency actually feels - with NResilience arms and Polly arms sharing the `Gate` and the fairness rules the gate uses.

### Two load modes

The suite drives arms two ways, because one mode cannot answer both questions.

- **Closed loop** holds concurrency fixed: DOP workers each await one op before starting the next. It measures what the process sustains, which makes it the right instrument for throughput and for per-op allocation. It is the wrong instrument for latency: at fixed DOP, latency is `DOP / throughput` by Little's Law, so a closed-loop percentile carries no information the throughput column does not already have.
- **Open loop** holds the *arrival rate* fixed: ops are launched on a schedule by dedicated pacer threads and never wait for their predecessors, and latency is measured from each arrival's *due* time so queueing counts. This is the only mode that can measure latency independently of throughput, and the only mode in which a client that slows itself down does not thereby reduce the load it offers - which is what makes it the only mode that can measure how much work a guard keeps off a dependency.

The pacers self-report. Every open-loop row carries the 99th percentile of the gap between an arrival's due time and its launch, plus counts of arrivals missed or shed; a row with either count non-zero is marked untrustworthy and the report refuses to quote it.

### Four sections

- **Executor level (closed loop, matched A/B)** - `raw` (the callback with no library at all), then four matched pairs: `lib-passthrough` vs `polly-empty` is bound-for-bound, neither able to retry and neither carrying a time bound; `lib-trivial` vs `polly-retry-only` pairs two three-attempt retry shapes with no time bounds; `lib-default` vs `polly-retry-timeout` pairs each library's default machinery; and the `-cancellable` variants add a caller token that can be cancelled and never is. A `lib-retry-twice`/`polly-retry-twice` pair makes three attempts per op with zero delay. Same gate, same suspension count, budget off where Polly has no equivalent to turn off.
- **Latency (open loop)** - `raw`, `lib-default` and `polly-retry-timeout` offered the identical absolute arrival rate, so a difference is queueing rather than saturation. Rates are capped where the pacers demonstrably hold the schedule.
- **Retry budget under sustained failure (open loop, behavioral)** - both arms face a dependency failing a quarter, then half, of what it is sent, counted across the whole cell so the pattern is a property of the dependency rather than of any one caller. Retries *per op* is the column that matters: it is the amplification the dependency feels, and unlike a retry rate it does not move with how fast the client happens to be running.
- **HTTP level (closed loop)** - a Kestrel host with `/echo` and a deterministic 1-in-4 503 `/flaky`, driven by `HttpResilience.CreateClient()`, the same client with the on-by-default extras off (`http-lib-minimal`), and a minimal Polly handler wrapping the same retry-on-5xx + attempt-timeout pipeline. The server runs **in its own process**, because `GC.GetTotalAllocatedBytes` and `GC.GetTotalPauseDuration` are process-wide and an in-process dependency puts its own allocation and its own collections into the client's figures. Every arm runs over an identically configured transport, so the resilience handler is the only difference between them. Both answers carry the same body, so a request costs the same whichever status it gets.

### What keeps the numbers honest

- **Repetitions.** Every cell runs three times and the report quotes the median with the observed spread beside it. Several of this suite's comparisons are a few percent, which is inside the run-to-run spread of a laptop; a single run of a cell cannot support such a claim.
- **Shuffled order.** Cells are emitted grouped and then shuffled with a fixed seed. Grouped, every NResilience arm would run before every Polly arm in each block, and on a machine that warms up over a long run the whole thermal drift would land on one library.
- **A fresh process per cell**, with the GC mode selected via `DOTNET_gcServer`/`DOTNET_gcConcurrent`, so GC state is attributable to exactly one arm - and each child verifies the mode it actually got before its numbers count.
- **A full GC between warmup and the window**, so the window is not charged for a collection warmup's garbage caused.
- **Published latency resolution.** The histogram's buckets grow by a stated factor and percentiles interpolate within the bucket rather than returning its midpoint. Two percentiles closer than that factor are not separable, and the report says so rather than printing them as though they were.
- **Thread-pool state sampled across the window**, not read once at the end. A single end-of-window reading says nothing about saturation - the queue it would have caught has usually drained by then - so the peak and the mean are what any claim about overload rests on.
- **Attempts counted at the top of every callback invocation**, before the attempt can fail and before any guard can refuse the next one, so the attempts column counts invocations that actually ran.

Run it:

```bash
dotnet run -c Release --project bench/NResilience.Stress -- --quick   # one DOP, one rate, single repetition
dotnet run -c Release --project bench/NResilience.Stress             # the full matrix
dotnet run -c Release --project bench/NResilience.Stress -- --window 30 --warmup 5 --repetitions 5
```

The full matrix prints its own cell count and wall clock, and the report records both. Reports land under `artifacts/stress/` as timestamped Markdown (for reading) and JSON (for diffing two runs, and for recomputing anything the Markdown asserts - the JSON holds every repetition, not the medians). Published, never gated - the doctrine is the latency harness's: shared runners make a throughput gate either loose enough to catch nothing or tight enough to flake weekly. A publishable write-up of one full run, with every figure verified against its JSON, lives in [stress-results.md](stress-results.md).

See [CONTRIBUTING.md](../CONTRIBUTING.md) for how the bench projects fit against the tests and the design.