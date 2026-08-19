# Documentation style guide

Contributor-facing. Not published with the docs site.

The docs serve two audiences at once: a developer who wants retry on their HTTP call in the next two
minutes, and an engineer who wants to know how the retry budget, the breaker and the deadline compose
in a single pass. Both are served by the same set - the rule is that the first audience is never made
to read the second's material to get their call working, and the second audience is never more than
two clicks from theirs.

## 1. The three-rung ladder

Every concept appears at up to three depths. Each rung links *down* to the next; no rung front-loads
the one below it.

| Rung | Answers | Lives in | Budget per concept |
| --- | --- | --- | --- |
| **What** | "What do I do, and what did this call return?" | `getting-started/` | 1-3 sentences |
| **How** | "How does it work, and how do I tune it?" | `features/`, `http/`, `di/`, `testing/`, `reference/` | a section |
| **Why** | "Why is it built this way?" | `deep-dives/` | a page |

**One canonical home per concept.** The circuit breaker's *what* is one section in Key Concepts; its
*how* is `features/circuit-breaker.md`; its *why* is `deep-dives/breaker-internals.md`. Every other
mention links rather than restates. Before adding an explanation, grep for it - if it already exists
at the right rung, link to it.

**Depth is advertised, not hidden.** A trimmed section ends with exactly one "Go deeper" link, so
brevity never reads as the library being shallow.

## 2. Voice

Second person, active, present tense.

- Yes: "Derive a variant with `with`."
- No: "A variant may be derived by means of a `with` expression."

Three registers are in use and each belongs in one place:

| Register | Where | Example |
| --- | --- | --- |
| Instructional | `getting-started/`, `guides/`, `features/`, `http/`, `di/`, `testing/` | "That's it. The call retried twice and the breaker never opened." |
| Neutral reference | `reference/` | "`Attempts` is the total attempt count, including the first." |
| Engineering essay | `deep-dives/` **only** | "The premise is true and the conclusion did not follow." |

The essay register is the right voice for the deep dives and the wrong one everywhere else. If a
user-facing page starts asserting design doctrine ("a fallback is not a strategy, it is an `if`"),
the content belongs in `deep-dives/`.

## 3. Page skeletons

### Guide (`guides/`)

The best-established pattern in the set. Follow it exactly:

1. **Scenario** - the goal the reader arrived with, in their words.
2. **Complete example** - copy-pasteable. `using` statements may be elided.
3. **What's happening** - short callouts on feature interactions, each linking to a feature page.
4. **Run it** - the actual commands.
5. **Handle the outcome** - plain English, linking to `CallResult<T>` reference.
6. **When to go deeper** - links into features and deep dives.

### Feature (`features/`)

1. What it is, in two sentences, and whether it is **on by default** or **opt-in**.
2. How to turn it on / off.
3. How to read what it produces.
4. Go deeper.

### Reference (`reference/`)

Entries in a stable order, each self-contained. A "most-used" table at the top when the page has
more than ~15 entries.

## 4. Above the fold

On any `getting-started/` page, the first screen contains **code, an outcome, and a next step**.
Caveats, rationale, and edge cases go below or into a linked page.

**Admonition budget: at most one callout (`> [!NOTE]`, `> [!TIP]`, `> [!IMPORTANT]`, `> [!CAUTION]`,
or `> [!WARNING]`) above the fold.** An interruption in position two is the fastest way to lose the
reader who wanted a working call.

## 5. Remedial content

Lead with the literal fix, then explain the why. `troubleshooting.md` is the reference
implementation:

```markdown
### Symptom, stated as the reader would state it

> [!CAUTION] Quick fix
> The command or code change, verbatim.

Why it happens, in a paragraph.

See [the deeper page](...) for the mechanism.
```

## 6. Mechanics

### Spelling: US English

`behavior`, `optimize`, `analyze`, `color`, `normalize`, `initialize`.

Not: `behaviour`, `optimise`, `analyse`, `colour`.

(Applies to prose. Never change an API name, CLI flag, or code identifier to match - if the source
says `AttemptTimeout`, the docs say `AttemptTimeout`.)

### Dashes

Use a spaced hyphen ` - ` as the sentence-level separator. Do not use ` — ` (em dash) or ` – ` (en
dash) - they are visually inconsistent with the majority of the set and render unpredictably in
terminals and plaintext exports.

Ranges use an unspaced hyphen: `0.10-0.30`, `net8.0-net10.0`.

### Terminology

One name per thing:

| Use | Not |
| --- | --- |
| **policy** (a `Resilience` value) | strategy, pipeline, handler, wrapper |
| **preset** (`Resilience.None`, `Resilience.Default`, `Resilience.Http`) | built-in policy, default policy, stock policy |
| **call** | operation, execution, invocation |
| **attempt** | try, invocation, run |
| **deadline** (`Deadline`) | total timeout, overall timeout - never bare "timeout" |
| **attempt timeout** (`AttemptTimeout`) | per-attempt timeout, attempt ceiling - never bare "timeout" |
| **backoff** (`Backoff`) | delay strategy, retry delay, wait strategy |
| **classifier** (`Classifier`) | result predicate, exception filter, retry predicate |
| **verdict** (`Verdict`) | classification result, decision, outcome |
| **breaker** (first use: "circuit breaker") | circuit, fallback, trip policy |
| **retry budget** (`RetryBudget`) | token bucket, retry throttler, rate limiter |
| **`RunAsync` / `TryRunAsync`** | Execute, Invoke, Run |
| **`with`** (derivation) | builder, `Build()`, fluent chain |
| **`CallResult<T>`** | fallback result, result object, outcome |
| **listener** (an `OnEvent` handler) | callback, hook, subscriber |
| **the executor** | *deep-dives only* - "fused executor" never in `features/` or `getting-started/` |

`Deadline` and `AttemptTimeout` are different things. Never write bare "timeout" where either could
be meant - that ambiguity is what this library exists to remove.

### Code samples

Every snippet compiles as written, or is explicitly marked as elided (`// ...`, or a note that
`using` statements are omitted). Prefer a snippet that a reader can paste into a console app.

### Telemetry samples

Sample `CallEvent` sequences must match what the code above them actually raises. In particular:

- `CallEvent.ToString()` lays out as `[PolicyName] Kind #N VerdictKind ExceptionType (duration) +delay`,
  with the `[PolicyName] ` prefix omitted when `Name` is unset, the ` ExceptionType` segment omitted
  when `Exception` is null, and the ` +delay` suffix omitted when `Delay` is null.
- A terminal event (`Succeeded`, `NotRetried`, `Rejected`, `DeadlineExceeded`, `Exhausted`) is
  exactly one per call - never show two.

When in doubt, run the code and paste the real output.

## 7. Frontmatter

```yaml
---
title: Page Title
description: One sentence, used as the meta description and in section indexes.
order: <n>
---
```

**Ordering convention:** a directory's `index.md` carries the **section's** order in the top-level
nav. Sibling pages number from `1` within the directory. Sibling values must be unique; a collision
between `index.md` and a sibling is expected and harmless.

Current section order:

| Order | Section |
| --- | --- |
| 0 | `index.md` |
| 1 | `getting-started/` |
| 2 | `guides/` |
| 3 | `features/` |
| 4 | `http/` |
| 5 | `di/` |
| 6 | `testing/` |
| 7 | `reference/` |
| 8 | `deep-dives/` |
| 9 | `migrating-from-polly.md` |
| 10 | `samples.md` |
| 11 | `troubleshooting.md` |
| 12 | `faq.md` |

Task sections (Guides, Features, HTTP, DI, Testing) come before methodology sections (Reference),
and Deep Dives sits last so it reads as an appendix rather than a stop on the tour.

## 8. Length budgets

Budget **prose words**, not lines. Code samples, tables, and images are not what makes a page feel
heavy - dense explanation is. Measure with:

```bash
awk 'BEGIN{i=0} /^```/{i=!i; next} i{next} {print}' page.md | wc -w
```

| Page type | Soft ceiling (prose words) |
| --- | --- |
| `getting-started/` page | 900 |
| `guides/` page | 1,200 |
| `features/` page | 1,600 |
| `http/` / `di/` / `testing/` page | 1,800 |
| `reference/` page | no ceiling, but needs top-of-page navigation past ~2,500 |

**The on-ramp budget:** `index.md`, `quick-start.md`, and `key-concepts.md` together stay under
**2,000 prose words**. They are the first impression, and their combined weight is what decides
whether a newcomer keeps reading.

### Jargon gate

These terms must not appear in `index.md`, `getting-started/`, or above the fold in `guides/`. Each
has a home further down the ladder:

`fused executor` · `state-machine box` · `sync-completing` · `suspending path` · `hoisted awaiter` ·
`ConditionalWeakTable` · `AsyncLocal` · `token bucket` · `pay-for-play` · `half-open` · `nested
retry`

Say the plain version and link: "every async method that actually awaits allocates a state machine",
not "the suspending path heap-allocates its state-machine box".

## 9. Links

Relative, with the `.md` extension. Anchor links use the generated slug:

```markdown
[Circuit breaker](../features/circuit-breaker.md)
[the rejection pause](../features/retry-budget.md#rejection-pause)
```

After moving a section between pages, grep for its anchor - several sections are deep-linked from
`troubleshooting.md` and `faq.md`.
