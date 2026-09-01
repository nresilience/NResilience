---
title: Samples
description: Runnable projects in the repository and an explanation of what each sample demonstrates.
order: 11
---

# Samples

The repository includes four standalone console applications in the [`samples/`](https://github.com/nresilience/NResilience/tree/main/samples) directory.

To run a sample, use one of the following commands:

```bash
dotnet run --project samples/Samples.QuickStart
dotnet run --project samples/Samples.Http
dotnet run --project samples/Samples.Grpc
dotnet run --project samples/Samples.Worker
```

| Sample | Description |
| :--- | :--- |
| `Samples.QuickStart` | Demonstrates policy values, retried calls, `TryRunAsync`, and printing the attempt log. This sample does not use HTTP or dependency injection. |
| `Samples.Http` | Demonstrates `ResilienceHttp.CreateClient`, a simulated 503-200 failure sequence, per-host circuit breakers, and the behavior of non-repeatable `POST` requests. |
| `Samples.Grpc` | Demonstrates `AddGrpcResilience()` against a gRPC service the sample hosts over real HTTP/2: two `Unavailable` replies retried to success, the per-attempt deadline arriving at the server as `grpc-timeout`, a write held to one attempt by `IsRepeatable`, a server stream retried to its first message, and `GrpcResilience.SingleShot()` at a call site. |
| `Samples.Worker` | Demonstrates a host using `AddResilience` from configuration, `AddHttpClient(...).AddResilience().AddRateLimit(...)`, injecting `IResiliencePolicies`, a limiter refusal that is not charged to the retry budget, and printing the retry fraction via the meter. It also prints log records to the console so the vocabulary is readable without a table. |

Each sample runs against an in-process fake dependency and does not require network access or external subscriptions.

Additionally, the code snippets used throughout this documentation are maintained as executable tests in [`tests/NResilience.Docs`](https://github.com/nresilience/NResilience/tree/main/tests/NResilience.Docs). Every code block on these pages is inlined from those tests to ensure that all examples compile and run.
