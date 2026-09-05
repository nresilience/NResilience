---
title: Idempotency
description: Why gRPC calls are repeatable by default, and the two ways to say a call is not.
order: 3
---

# Idempotency

The HTTP integration refuses to retry a `POST`, because a repeated `POST` is a duplicate order or a duplicate charge. The gRPC default is the opposite: **every unary method is repeatable unless you say otherwise.**

This is not an inconsistency. Every gRPC call is a `POST` at the transport, but most are reads at the application, so carrying the HTTP rule across would make the interceptor inert.

The direction of the declaration flips instead. In HTTP you name the writes that *are* safe to repeat; in gRPC you name the ones that are not.

## Per client: `RepeatableWhen`

The registration decides, per method, using anything on `IMethod` - the method name, the service name, the full name, or `MethodType`:

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

A method that is not repeatable gets exactly one attempt. The breaker still sees the outcome and the [retry budget](../features/retry-budget.md) still gets its deposit - nothing is sent twice, and the guards see everything.

`ResilienceInterceptor.WillRetry(IMethod)` answers the same question without making a call, which is what a test asserts on.

## Per call: `SingleShot()`

`RepeatableWhen` is a statement about a whole client. When one call site is the exception, wrap it:

<!-- snippet: grpc-single-shot -->
```csharp
// Nothing on the wire, and it reaches a generated client that never exposes CallOptions.
using (GrpcResilience.SingleShot())
{
    await ChargeAsync();
}
```
<!-- endsnippet -->

`GrpcResilience.SingleShot()` is an ambient scope, on the same pattern as [`AmbientDeadline.Begin`](../features/deadlines.md#propagate-the-deadline-across-a-hop) and `NestedRetry.Begin`. It applies to every gRPC call made inside it, including calls made by code you did not write, and it restores the previous value when disposed.

It is deliberately **not** a metadata entry. A header would travel to the server, making this library's internal plumbing part of your wire contract, and it would be unreachable from a generated client that never exposes `CallOptions`. The scope reaches every client.

## Which one to use

| Situation | Use |
| :--- | :--- |
| A method is never safe to repeat | `RepeatableWhen` on the registration |
| Only reads should be retried on this client | `RepeatableWhen = static m => m.Type == MethodType.Unary` plus your own naming rule |
| One call site is the exception | `GrpcResilience.SingleShot()` |
| The server deduplicates on a key you send | Neither - the call is safe to repeat, so leave the default as is |

## Hedging

A [hedge](../features/hedging.md) is a concurrent retry, so it obeys the same rule: a method that may not be repeated may not be hedged either, and a single-attempt policy carries no hedge.
