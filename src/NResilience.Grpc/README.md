# NResilience.Grpc

The gRPC integration for [NResilience](https://github.com/nresilience/NResilience): a client interceptor that runs a resilience policy around each gRPC call.

```csharp
services.AddGrpcClient<Orders.OrdersClient>(o => o.Address = new Uri("https://orders.internal:5001"))
    .AddGrpcResilience();
```

That is the whole setup. The client now retries what is worth retrying, stops when the deadline says so, opens a circuit per service, and tells the server how
long each attempt has.

## Why not `AddResilience()`

`AddGrpcClient<T>()` returns an `IHttpClientBuilder`, so calling the HTTP registration on it compiles - and does nothing useful:

- Every gRPC call is an HTTP `POST`, which the HTTP handler refuses to retry by default, so the handler is inert.
- A gRPC failure travels in the `grpc-status` trailer on an HTTP `200`, so the HTTP classifier reads a successful response. The classification is not merely
  absent, it is wrong.

`AddGrpcResilience()` is the gRPC-shaped registration, and the distinct name keeps the two from being confused at a call site.

## What it does

|                                                  |                                                                                                                                                                                                                                             |
|--------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Classifies gRPC statuses**                     | `Unavailable` and `DeadlineExceeded` are transient, `ResourceExhausted` is throttling, and everything else is an answer. Any of it is one line to override.                                                                                 |
| **Repeats unary calls by default**               | The opposite of the HTTP default, because every gRPC call is a `POST` at the transport and most are reads at the application. Mark the ones that must not repeat with `RepeatableWhen`, or wrap a call site in `GrpcResilience.SingleShot()`. |
| **Propagates the attempt deadline**              | Each attempt's ceiling is written into `CallOptions.Deadline`, which grpc-dotnet sends as the standard `grpc-timeout` header. The peer learns the bound; nothing new to parse.                                                              |
| **Scopes guards per service**                    | One breaker, one retry budget, and one latency window per gRPC service by default. `ScopeBy` makes that per method or one per client.                                                                                                       |
| **Retries a server stream to its first message** | A server-streaming call is retried while it is still indistinguishable from a call (before anything is yielded) and never after. Its wire deadline is the whole call's remaining budget rather than the attempt ceiling.                    |
| **Reports nested retries**                       | Under the same `x-nresilience-retrying` marker the HTTP handler uses, so the fact crosses transports.                                                                                                                                       |

Client-streaming and duplex calls pass through untouched: the request stream is a source the caller drives interactively, and repeating one means re-enumerating
something the failed attempt already consumed.

## Links

- [Documentation](https://github.com/nresilience/NResilience#readme)
- [NResilience](https://www.nuget.org/packages/NResilience) - the core package
- [NResilience.Extensions](https://www.nuget.org/packages/NResilience.Extensions) - DI, configuration, and telemetry

MIT licensed.
