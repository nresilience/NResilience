---
title: Samples
description: Runnable projects in the repository and an explanation of what each sample demonstrates.
order: 10
---

# Samples

The repository includes three standalone console applications in the [`samples/`](../samples) directory.

To run the samples, use the following commands:

```bash
dotnet run --project samples/Samples.QuickStart
dotnet run --project samples/Samples.Http
dotnet run --project samples/Samples.Worker
```

| Sample | Description |
| :--- | :--- |
| `Samples.QuickStart` | Demonstrates policy values, retried calls, `TryRunAsync`, and printing the attempt log. This sample does not use HTTP or dependency injection. |
| `Samples.Http` | Demonstrates `ResilienceHttp.CreateClient`, a simulated 503-200 failure sequence, per-host circuit breakers, and the behavior of non-repeatable `POST` requests. |
| `Samples.Worker` | Demonstrates a host using `AddResilience` from configuration, `AddHttpClient(...).AddResilience()`, injecting `IResiliencePolicies`, and printing the retry fraction via the meter. |

Each sample runs against an in-process fake dependency and does not require network access or external subscriptions.

Additionally, the code snippets used throughout this documentation are maintained as executable tests in [`tests/NResilience.Docs`](../tests/NResilience.Docs). Every code block on these pages is inlined from those tests to ensure that all examples compile and run.
