---
title: gRPC
description: Use the resilience interceptor to retry gRPC calls, classify status codes, and propagate a per-attempt deadline.
order: 5
---

# gRPC

A [policy](../getting-started/key-concepts.md#what-is-a-policy) manages retries, timeouts, and circuit breaking for any call. gRPC introduces its own constraints, and they are not the same ones HTTP has.

Register the interceptor on the builder that `AddGrpcClient<T>()` returns:

<!-- snippet: grpc-register -->
```csharp
services.AddGrpcClient<OrdersClient>(o => o.Address = new Uri("https://orders.internal:5001"))
    .AddGrpcResilience();
```
<!-- endsnippet -->

That client now makes three attempts with exponential backoff, retries an `Unavailable` but not a `NotFound`, opens a circuit breaker per gRPC service, and tells the server how long each attempt has. See [Classification](classification.md) for status codes.

> [!IMPORTANT]
> `AddResilience()` also compiles on a gRPC client builder, and it does nothing useful. Every gRPC call is an HTTP `POST`, which the resilience handler refuses to retry by default, and a gRPC failure travels in the `grpc-status` trailer on an HTTP `200`, which the HTTP classifier reads as a success. Use `AddGrpcResilience()`.

## Install

```bash
dotnet add package NResilience.Grpc
```

The package depends on `NResilience`, `NResilience.Extensions`, `Grpc.Core.Api`, and `Grpc.Net.ClientFactory`. A gRPC client's dependency graph already pulls in most of that weight.

## Interceptor capabilities

`ResilienceInterceptor` runs a [policy](../reference/resilience.md) around each gRPC call:

- **Status classification**: Reads the `StatusCode` on an `RpcException`, which is where a gRPC failure lives. See [Classification](classification.md).
- **Repeatable by default**: Retries unary calls unless you say otherwise - the opposite of the HTTP default. See [Idempotency](idempotency.md).
- **Attempt deadline propagation**: Writes each attempt's ceiling into `CallOptions.Deadline`, which grpc-dotnet sends as the standard `grpc-timeout` header. See [Deadlines](deadlines.md).
- **Per-service scoping**: Scopes the circuit breaker, the retry budget, and the hedging latency estimate to the gRPC service. See [Per-service scope](per-service-scope.md).
- **Nested retry detection**: Reports when retries are happening in layers, under the same marker the HTTP handler uses. See [Nested retries](../http/nested-retries.md#grpc-carries-the-same-marker).
- **Server streaming**: Retries a server stream until its first message, and hands the rest of the enumeration over untouched. See [Streaming](streaming.md).
- **Call management**: Disposes the gRPC calls that a retry supersedes.

## Configure the interceptor

Pass a policy, options, or both:

<!-- snippet: grpc-register-options -->
```csharp
services.AddGrpcClient<OrdersClient>(o => o.Address = new Uri("https://orders.internal:5001"))
    .AddGrpcResilience(
        GrpcResilience.Default with { Attempts = 4 },
        o =>
        {
            // A charge must not be repeated, whatever the transport says.
            o.RepeatableWhen = static method => method.Name != "ChargeCard";

            // One breaker per method rather than per service.
            o.ScopeBy = static method => method.FullName;
        });
```
<!-- endsnippet -->

| Option | Default | Description | Reference |
| :--- | :--- | :--- | :--- |
| `RepeatableWhen` | every method | Decides whether a method may be repeated. | [Idempotency](idempotency.md) |
| `ScopeBy` | `m => m.ServiceName` | The breaker, budget, and latency-window scope key. `null` is one scope per client. | [Per-service scope](per-service-scope.md) |
| `MaximumScopes` | `1024` | Bounds the scope registry. | [Per-service scope](per-service-scope.md) |
| `BreakerPerScope` | `true` | Gives each scope its own circuit breaker. | [Per-service scope](per-service-scope.md) |
| `BreakerSettings` | `null` | The settings those breakers are built with. | [Breaker](../reference/breaker.md) |
| `BudgetPerScope` | `true` | Gives each scope its own retry budget. | [Per-service scope](per-service-scope.md#where-the-budget-comes-from) |
| `PropagateDeadline` | `true` | Writes the attempt ceiling into `CallOptions.Deadline`. On here and off for HTTP, on purpose. | [Deadlines](deadlines.md) |
| `DeadlineSlack` | `50 ms` | How much longer than the ceiling that deadline is set. | [Deadlines](deadlines.md#why-the-slack-is-not-zero) |
| `OwnTransportTimeout` | `true` | Sets `HttpClient.Timeout` to infinite so it stops competing with the deadline. | [Deadlines](deadlines.md#who-bounds-what) |
| `DetectNestedRetries` | `true` | Stamps and reads the nested-retry marker. | [Nested retries](../http/nested-retries.md) |

## Register the interceptor first

Register `AddGrpcResilience()` before any other interceptor. Interceptors registered after it run **per attempt**, which is where an interceptor that refreshes a token belongs - a token fetched once outside the retry loop can expire during it.

The gRPC client factory does not expose registrations already made, so the order is a rule rather than something the library can enforce.

## Which calls are wrapped

Server-streaming calls are wrapped on the core library's [streaming](../features/streaming.md) semantic: retried until their first message, never after. The one difference from a unary call is the deadline on the wire - for a stream, it is the whole call's remaining budget. See [Streaming](streaming.md).

Client-streaming and duplex calls pass through untouched. The request stream is a source you drive interactively, and repeating one means re-enumerating something the failed attempt has already partially consumed - which produces duplicates or requires buffering everything. Neither is a resilience feature. Wrap the *setup* call instead, the way any other callback is wrapped.

The synchronous `BlockingUnaryCall` throws `NotSupportedException`: passing it through silently would leave one call in the client with no retry, no breaker, and no deadline. Use the generated client's `Async` overload.

## Read what it holds

<!-- snippet: grpc-breakers -->
```csharp
// One breaker and one budget per gRPC service by default, keyed by the service's full name -
// so an operator can be told which dependency opened, not merely that something did.
foreach (var (service, breaker) in interceptor.Breakers())
    Console.WriteLine($"{service}: {breaker.State}");
```
<!-- endsnippet -->

The registration also adds these to [`ResilienceHealthOptions`](../di/health-checks.md), so a health endpoint reports them without any wiring of yours.
