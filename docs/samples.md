---
title: Samples
description: Runnable projects in the repository, and what each one shows.
order: 10
---

# Samples

Three console applications under [`samples/`](../samples), each one runnable on its own.

```bash
dotnet run --project samples/Samples.QuickStart
dotnet run --project samples/Samples.Http
dotnet run --project samples/Samples.Worker
```

| Sample | Shows |
| --- | --- |
| `Samples.QuickStart` | A policy value, a retried call, `TryRunAsync`, and the attempt log printed. No HTTP, no container. |
| `Samples.Http` | `ResilienceHttp.CreateClient`, a scripted 503 → 200, per-host breakers, and a POST that is not retried until it says it is repeatable. |
| `Samples.Worker` | A host with `AddResilience` from configuration, `AddHttpClient(…).AddResilience()`, `IResiliencePolicies` injected, and the meter printing the retry fraction. |

Each one runs against an in-process fake dependency, so none of them needs the network or a
subscription to anything.

The docs' own snippets are a fourth thing to read: they live in
[`tests/NResilience.Docs`](../tests/NResilience.Docs) as executing tests, and every code block on
these pages is inlined from there. If a page shows it, it compiles and runs.

