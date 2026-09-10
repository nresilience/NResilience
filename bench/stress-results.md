# Stress test: NResilience vs Polly

*Measured 10 September 2026. .NET 10.0.0, macOS 26.6.1, 10 logical cores. NResilience at current `main` against Polly 8.7.0, the version this repo pins as its competitive baseline. All figures come from `artifacts/stress/stress-20260910-111829.json` (and, where two runs are compared, from `artifacts/stress/stress-20260910-104402.json`). Every ratio was recomputed from the raw data.*

## What was tested, in one paragraph

A stress suite ran 90 test configurations, three times each, in fresh processes: 270 runs over 32.4 minutes. Both libraries were driven by the exact same test callbacks, so the only difference between the two sides of any comparison is the library itself. The suite asked four questions with four instruments:

| Question | How it was measured |
| :--- | :--- |
| How fast, and how much memory per call? | Fixed number of workers (1, 10 or 80 concurrent calls) |
| How fast does the memory come back as GC pauses? | Same runs - GC counters collected alongside |
| How quick is an individual call? | A fixed arrival rate (50k and 200k calls/s), callers never wait for each other |
| What does the retry budget do to a struggling dependency? | Same fixed-rate method, with a dependency failing 1 in 4 or 1 in 2 requests |

Two measurement modes matter, and it is worth knowing why. **Closed loop** (fixed workers) is right for throughput and memory per call. It is wrong for latency: with a fixed worker count, an arm's latency is just its throughput in disguise, so a "latency" number from that mode proves nothing. **Open loop** (fixed arrival rate) launches calls on a schedule and never lets a slow call slow the ones behind it, so latency can be measured honestly - and, importantly, a client that pauses cannot "help" the dependency by sending less. That second property is what makes the retry-budget test fair.

## Results at a glance

> **TL;DR: NResilience allocates 1.6x to 2.4x less memory per call than Polly, and the saving
> shows up directly in GC pauses. Throughput: NResilience 1.2x-1.8x faster when nothing retries,
> an exact tie when both libraries actually retry. The retry budget cuts load on a failing
> dependency 4x to 19x, and pays for it by refusing some of your own calls. HTTP: NResilience's
> handler is at parity with a hand-written Polly bridge. Tail latency: not measurable on a
> laptop; not claimed.**

| Question | Answer |
| :--- | :--- |
| **Memory per call** | NResilience allocates 1.6x-2.4x less than Polly across all five matched pairs. Byte-for-byte identical across three repetitions and a second independent run - the most solid result in the suite. |
| **Doing nothing** | NResilience with all features off costs **exactly nothing** - the same 224 bytes/call as raw code with no library. Polly's empty pipeline costs 528 B/call. |
| **Speed** | NResilience is 1.2x-1.8x faster where nothing retries. When both libraries actually retry, it is a **tie**. |
| **Garbage collection** | NResilience's full default policy allocates *memory at the same rate as Polly's empty pipeline* (1.25 GB/s vs 1.28 GB/s at 10 workers) and costs less per collection. Result: **7.6% of time paused in GC vs Polly's 17.3%** - about 2.3x less. |
| **Retry budget** | Against a dependency failing half its requests: **19x fewer retries** sent, at the price of completing 53% as many operations. Against a dependency failing a quarter: 4x fewer retries, completing 81%. The trade is the feature. |
| **HTTP** | NResilience's bare handler costs 1,273 B/request above transport; the minimal Polly bridge costs 1,209 B - a 64 B gap that is inside run-to-run noise. Call it parity, with NResilience marginally behind. |
| **Tail latency (p99)** | Not measurable on this machine. Identical repetitions of the same test produced p99s spanning 1.02 ms to 8.49 ms - an 8x spread caused by the laptop, not the library. **No p99 claim is made.** |

## How to read the rest of this

Every section below states what a number means before who won. The two libraries are compared in **pairs** - each NResilience configuration paired with the closest Polly equivalent:

| Pair | What it isolates |
| :--- | :--- |
| **Floor** - every feature off, vs Polly's empty pipeline | The cost of merely existing. Neither side can retry or time out. |
| **Lightest retry-capable** - 3 attempts, no time bounds, vs Polly retry x2 | The cost of being *able* to retry. |
| **Default machinery** - NResilience `Resilience.Default` vs Polly retry + 10 s timeout | Each library's standard configuration. |
| **Default + caller token** - same as above, plus the `CancellationToken` every real caller passes | What the token costs. |
| **Retry** - 3 attempts, zero delay, on both sides | Retry machinery itself, per attempt. |

One honest caveat on "fair": Polly has no equivalent of NResilience's deadline, attempt timeout, classifier, or retry budget, so the **Default machinery** pair is NResilience doing *more* work than the Polly side - not the same work. Polly is also running its cheapest possible configuration (no telemetry), which is the fairest reading of its design. Polly's own published figures put telemetry-enabled costs roughly 7x higher.

Everything else in the harness was matched exactly: same test callbacks, same attempt counts, zero-delay retries, no telemetry on either side. Every arm suspends exactly once per attempt through the same shared gate, so overhead comparisons measure the library, not the test rig.

Reliability controls, briefly: every cell ran three times (medians quoted, full spread published); cell order was shuffled with a fixed seed so thermal drift cannot favour one library; each run got a fresh process and verified the GC mode it actually received; a full GC ran between warmup and measurement so warmup's garbage is never counted; the HTTP dependency ran in its **own process**, so its memory and pauses contribute zero to the client's figures.

---

## 1. Memory per call: the strongest result

This is the suite's most solid number. Every value below had **0% spread across three repetitions**, and was **identical to the byte in a second, independent full run**. These are deterministic, not noisy averages.

**Bytes allocated per operation (median of 3 runs, across the whole matrix):**

| Pair | NResilience | Polly | NResilience advantage |
| :--- | ---: | ---: | :--- |
| Floor (everything off) | 224 B | 528 B | **2.36x less** |
| Lightest retry-capable | 560 B | 1,224 B | **2.19x less** |
| Default machinery | 624 B | 1,520 B | **2.43x less** |
| Default + caller token | 640 B | 1,520 B | **2.37x less** |
| Retry (3 attempts/op) | 2,241 B | 3,609 B | **1.61x less** |

Two rows deserve a closer look:

- **The floor row: "off" really means off.** NResilience with every feature turned off allocates **224 B/call - exactly what the raw test callback allocates with no library at all.** The executor hands back the callback's own task and adds nothing. Polly's empty pipeline adds 304 B to the same callback. Turning NResilience off is free; turning Polly off is not.
- **The caller token.** Adding a `CancellationToken` costs NResilience 16 B (624 to 640) and Polly nothing measurable. For both libraries the token's cost is CPU, not memory, which is why both arms' memory *rates* fall in that row even as their throughput does. The token pair is the shape every real ASP.NET Core handler is in.

## 2. Speed: NResilience ahead except when retrying

Throughput is much less reproducible than allocation - spreads up to 10% within a cell, and one DOP 1 cell at 32% - so it is quoted as a range across the whole matrix (DOP 1/10/80, both GC modes) rather than as a single number.

**NResilience throughput advantage (ratio of medians, all cells):**

| Pair | Range |
| :--- | :--- |
| Floor | 1.40x - 1.61x |
| Lightest retry-capable | 1.22x - 1.83x |
| Default machinery | 1.33x - 1.73x |
| Default + caller token | 1.07x - 1.64x |
| Retry (3 attempts/op) | **1.00x - 1.13x - a tie** |

Two honest notes:

- **The retry pair is a tie.** The retry loop is not where the two libraries differ; NResilience's advantage comes from the frame around the call, not from retrying faster.
- **The caller token costs NResilience proportionally more.** At DOP 10 with server GC the ratio narrows to 1.07x. In that cell NResilience keeps 53% of its own untokened throughput while Polly keeps 66% - NResilience stays ahead in absolute terms but gives up more of its headroom.

The single-cell picture at DOP 10, workstation GC (the repo's allocation-gate configuration; the `raw` row is the callback with no library at all):

| Configuration | Speed (ops/s) | Memory (B/call) | GC time | Pause per GC |
| :--- | ---: | ---: | ---: | ---: |
| Raw callback, no library | 3,537,829 | 224 | 4.6% | 364 us |
| NResilience, features off | 3,585,384 | 224 | 4.8% | 369 us |
| Polly, empty pipeline | 2,415,075 | 528 | 9.1% | 441 us |
| NResilience, lightest retry | 2,617,656 | 560 | 8.5% | 358 us |
| Polly, retry only | 1,746,897 | 1,223 | 18.2% | 531 us |
| NResilience `Resilience.Default` | 2,005,534 | 624 | 7.6% | 395 us |
| Polly, retry + timeout | 1,320,164 | 1,519 | 17.3% | 549 us |
| NResilience default + token | 1,185,460 | 640 | 4.3% | 351 us |
| Polly retry + timeout + token | 816,503 | 1,516 | 11.8% | 552 us |
| NResilience, fail twice then succeed | 115,618 | 2,241 | 2.2% | 545 us |
| Polly, fail twice then succeed | 114,847 | 3,608 | 3.2% | 482 us |

## 3. Garbage collection: what the memory savings buy

The B/call numbers matter only because they become GC pauses, and the mechanism has two halves that this run separated cleanly. Both halves favour NResilience.

**Half one: collections happen per byte allocated, not per call.** Every arm fills a fixed GC bucket before a collection triggers (6.14-6.27 MB under workstation GC, 76-80 MB under server GC), so collections-per-second is purely a function of allocation *rate*. This makes B/call the wrong column for reasoning about pauses, because the arms don't run at the same speed. The vivid version: at DOP 10, **NResilience's full default policy allocates at 1.25 GB/s - and Polly's *empty* pipeline, which does nothing at all, allocates at 1.28 GB/s.** NResilience's full machinery (deadline, attempt timeout, classifier, retry budget, attempt log) produces garbage at the rate of a Polly pipeline that is switched off.

**Half two: each collection costs more when more objects survive it.** At DOP 10, one collection takes 395 us with NResilience's default policy running vs 549 us with Polly's retry + timeout pipeline - **1.39x more expensive per collection**, because more of what Polly's pipeline allocates is still reachable when the collector runs, so there is more to trace. Notably, Polly's *empty* pipeline costs 441 us, close to NResilience's 395 - the penalty belongs to the retry-and-timeout composition, not to Polly generally.

**The two halves multiply, and the prediction matches what was observed** in every cell of the matrix (alloc-rate ratio x cost-per-collection ratio vs the actually observed pause-share ratio):

| Cell | Alloc-rate ratio | x Cost-per-collection | = Predicted | Observed |
| :--- | ---: | ---: | ---: | ---: |
| DOP 1, workstation | 1.66x | 1.25x | 2.07x | **2.12x** |
| DOP 1, server | 1.66x | 0.95x | 1.58x | **1.48x** |
| DOP 10, workstation | 1.60x | 1.39x | 2.23x | **2.27x** |
| DOP 10, server | 1.83x | 1.05x | 1.93x | **1.87x** |
| DOP 80, workstation | 1.50x | 1.63x | 2.44x | **2.25x** |

In absolute terms, at DOP 10:

- Workstation GC: the process spends **7.6%** of the window stopped in GC with `Resilience.Default`, vs **17.3%** with Polly's retry + timeout.
- Server GC (the usual production setting): **3.7% vs 7.0%**.
- Gen2 never fired for this pair in either mode; exactly one cell in the whole 270-run matrix saw a single Gen2 collection. This is Gen0 churn throughout.

## 4. Latency: what is real and what is not

Open loop at a fixed arrival rate, server GC. Both arms are offered the identical rate, so any difference is queueing. Latency is measured from each arrival's *due* time, so any scheduling delay counts against the arm. At 50,000 calls/s, where the schedule held cleanly:

| Arm | p50 | p50 range (3 runs) | p90 | p90 range | p99 | p99 range |
| :--- | ---: | :--- | ---: | :--- | ---: | :--- |
| Raw callback | 2.9 us | 2.9 - 3.2 us | 13.2 us | 5.8 - 72.3 us | 3.64 ms | 1.02 - 8.49 ms |
| NResilience `Resilience.Default` | **3.5 us** | 3.4 - 3.9 us | 7.6 us | 5.3 - 41.1 us | 5.62 ms | 1.23 - 5.88 ms |
| Polly, retry + timeout | 4.4 us | 4.1 - 4.5 us | 8.1 us | 6.4 - 8.3 us | 3.75 ms | 2.41 - 4.11 ms |

**Read the range columns first - they are the point of this table.**

- **The p50 is a real result.** NResilience's median is 3.5 us vs Polly's 4.4 us, and the three-run ranges do not overlap (3.4-3.9 vs 4.1-4.5). The 1.26x gap is larger than machine noise and larger than the histogram's 8% resolution. At 200,000 ops/s the same comparison reads 4.4 vs 6.1 us, also disjoint - though at that rate the pacers strained (one arm missed 0.41% of arrivals), so it is corroboration, not the headline.
- **The p90 is not a result.** The ranges overlap (5.3-41.1 vs 6.4-8.3 us); no comparison can be drawn.
- **The p99 is not measurable on this machine at all.** Identical repetitions of the *same cell* produced p99s spanning 1.02 to 8.49 ms - a factor of 8 - while GC was under 1% of the window and the schedule was on time. Those are multi-millisecond stalls of the whole process; on a fanless laptop with background activity, that is the environment, not the library. A p99 claim needs a quiet, pinned, thermally stable host and many more repetitions. **This run does not have one, so it makes no p99 claim.**

## 5. The retry budget: 4x to 19x less load on a failing dependency, paid for in availability

This section is behavioral, not a speed test. When a dependency starts failing, every client that retries at full strength adds load to something already struggling. Polly 8 has no mechanism for this - a Polly pipeline retries whatever its strategy allows. NResilience's retry budget caps retries as a fraction of traffic (with a small per-second floor so a quiet service can still retry).

Both clients faced a dependency failing a fixed fraction of what it is *sent*, counted across the whole cell so it is a property of the dependency, not of any one caller. At 20,000 calls/s offered - where every cell held its schedule exactly:

| Client | Dependency fails | Retries per operation | Retries sent/s | Calls refused locally/s | Calls completed/s |
| :--- | :--- | ---: | ---: | ---: | ---: |
| Polly (no budget) | 1 in 4 | 0.333 | 6,663 | 0 | 19,997 |
| NResilience (budget on) | 1 in 4 | **0.081** | 1,625 | 3,781 | 16,218 |
| Polly (no budget) | 1 in 2 | 0.996 | 19,920 | 0 | 19,960 |
| NResilience (budget on) | 1 in 2 | **0.053** | 1,056 | 9,472 | 10,528 |

**Retries per operation is the column that matters**, because unlike a retry *rate* it does not move with how fast the client happens to run. Read the pattern in it:

- Against a dependency failing a quarter of its requests, the budget cuts retry amplification **4.1x**; against one failing half, **18.9x**. The harsher the failure, the more the budget does - exactly the regime where an unbudgeted fleet turns a struggling dependency into a dead one.
- Polly's amplification *rises* with the failure rate (0.333 to 0.996); NResilience's *falls* (0.081 to 0.053). A fixed fraction of traffic buys proportionally fewer retries as more of that traffic needs them.

**The price is on the same table.** The budgeted client completed 16,218 calls/s against Polly's 19,997 at 1-in-4 (**81%**), and 10,528 against 19,960 at 1-in-2 (**53%**). Nearly half the operations it refused outright rather than retrying. That is the trade: client-side availability in exchange for load relief on a shared dependency. It is a good trade across a fleet and a bad one for a single client with no fleet to protect. (The budgeted arm's p99 of about 101 ms in every cell is the deliberate refusal pause - a refusal that returns instantly would invite the caller to spin - and nothing else.)

Every figure in this section reproduced identically at 100,000 calls/s offered (where the repetitions held their schedule), and again in a second independent full run.

## 6. HTTP: parity with a minimal Polly bridge

A real Kestrel service on loopback, **in its own process**, one endpoint always succeeding, one failing every fourth request with a 503. GET requests retried twice on 5xx with a 10-second attempt timeout on both clients. All arms ran over an identically configured `SocketsHttpHandler` with an infinite client timeout, so the resilience handler is the only difference between them. (The 200 and 503 responses carry the same body, so a request costs the same either way. The Polly arm is a minimal handler wrapping its pipeline, because `Polly.Extensions.Http` has no v8 release and Microsoft's v8 integration carries `IHttpClientFactory` machinery this comparison deliberately excludes.)

At DOP 8, workstation GC:

| Client | Speed (ops/s) | Memory (B/op) | Requests sent per op | Memory per request (B) | **Handler's own cost (B/op)** |
| :--- | ---: | ---: | ---: | ---: | ---: |
| Plain client, always-200 endpoint | 31,787 | 2,499 | 1.000 | 2,499 | - |
| Plain client, flaky endpoint | 33,238 | 2,507 | 1.000 | 2,507 | - |
| NResilience handler, extras off | 26,220 | 4,405 | 1.249 | 3,526 | **1,273** |
| Polly bridge | 25,133 | 4,381 | 1.265 | 3,462 | **1,209** |
| NResilience handler, default features | 25,364 | 6,435 | 1.251 | 5,143 | 3,298 |

The **Handler's own cost** column needs explaining. A retrying client sends about 1.25 requests per operation, so naively subtracting a single request's transport cost charges the handler for a quarter-request it did not cause. This column instead subtracts `requests-per-op x 2,507` - the transport each row actually used, measured against a plain client facing the same endpoint and status mix at one request per operation. The correction is not small: the naive subtraction would have read 1,898 B and 1,874 B, so about a third of the apparent handler cost is just transport for retried requests.

Three things follow:

- **Throughput is transport-bound.** Every arm is pinned by the same loopback ceiling, and all three retrying clients sent about 1.25 requests per operation as observed *at the server* - they are behaviorally matched.
- **The bare handler is at parity with the Polly bridge, marginally behind it:** 1,273 B vs 1,209 B - a 64 B difference on ~4,400 B, inside the run-to-run spread.
- **The remaining ~2,030 B on the full configuration is the feature package** - the stall-bound response wrapper with its timer, per-response rate-limit header reads, nested-retry stamping - each individually switchable by the consumer. You pay bytes for features, not for the handler's existence.

One caveat: the 503 pattern is a counter at the server shared by eight concurrent clients, so which operations absorb the failures depends on how the arms' requests interleave; observed failure rates were 6.3%, 5.1% and 6.2% across the three retrying arms rather than exactly equal. The handler-cost column uses each row's own observed requests-per-op, so it corrects for the difference rather than being distorted by it.

## What this report does not claim

- **Tail latency.** The p99 varies by up to 8x between identical repetitions of the same cell (see section 4). No p99 claim, and no claim about behavior under overload. That needs a quiet, pinned host.
- **No overload was tested.** At 80 workers, 80 calls were in flight against a thread pool that peaked at 11-23 threads; the queue peaked at 73-80. That is high concurrency, not saturation. Nothing here measures a saturated thread pool.
- **One machine, one run.** A 10-core Apple Silicon laptop, 5-second windows, three repetitions. Allocation figures are exact and reproduced across two independent full runs. Throughput ratios move 10-30% between runs and are quoted as ranges. Anything not given a range should be read as "on this machine".
- **Executor arms measure machinery on calls that succeed** (or fail deterministically). Backoff delays, breaker trips, and hedging are different experiments.
- **Polly ran its cheapest configuration.** No telemetry, no metrics. Matching its cheapest configuration is the fairest reading of the designs; Polly's own published figures put telemetry-enabled costs far higher.
- **The default-machinery pair is not doing the same work.** `Resilience.Default` arms a deadline, an attempt timeout, a classifier and a retry budget; Polly's pipeline arms a retry strategy and a timeout. NResilience is ahead while doing more - the interesting version of the claim, but not a like-for-like comparison of identical configurations.
- **The budget section is a policy comparison**, trading client-side availability for load relief on a dependency. Both halves are in the table; which one matters depends on whether you have a fleet.
- **The version is pinned.** Polly 8.7.0. A newer Polly may narrow any gap - which is exactly why the repo pins the version and re-measures deliberately on a bump.

## Reproduce

The suite is `bench/NResilience.Stress` - published, never gated, for the same reason the latency harness is not: shared CI runners make a throughput gate either too loose to catch anything or tight enough to flake weekly.

```bash
dotnet run -c Release --project bench/NResilience.Stress -- --quick   # one DOP, one rate, single repetition
dotnet run -c Release --project bench/NResilience.Stress             # the full matrix, ~32 min here
dotnet run -c Release --project bench/NResilience.Stress -- --window 30 --warmup 5 --repetitions 5
```

Reports land under `artifacts/stress/` as timestamped Markdown and JSON. The numbers in this document come from `stress-20260910-111829`; the JSON holds **every repetition** rather than the medians, so anything asserted here can be recomputed from it - the spreads included, which is the point. The suite exits non-zero if any cell failed its own checks, and the auto-generated report names such cells in a warning block rather than letting them into a table.

For this run, individual repetitions in four cells failed their pacer checks and are excluded from the figures above: one repetition in each of the two budget arms at 100,000 ops/s, and one repetition of the NResilience default arm at 200,000 ops/s. In each case the pacer could not hold the schedule and the run missed up to 0.46% of arrivals. The report quotes those sections only from the rates - and, at 100k/200k, only from the repetitions - that held.