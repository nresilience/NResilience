# NResilience.AspNetCore

The inbound half of deadline propagation for [NResilience](https://github.com/nresilience/NResilience).

## Install

Install the package using the .NET CLI:

```bash
dotnet add package NResilience.AspNetCore
```

## What it adds

One middleware. It reads the deadline a caller sent with the request and publishes it for the rest of that request, so any policy configured with
`UseAmbientDeadline = true` is bounded by
`min(its own deadline, the time the caller is still waiting)`.

```csharp
var app = builder.Build();

app.UseResilienceDeadline();
```

This is a separate package because it is the only part of NResilience that requires ASP.NET Core. The outbound half - writing the header on the way out - is in
the core package, on
`HttpResilienceOptions.PropagateDeadline`.

## Documentation

See [deadline propagation](https://github.com/nresilience/NResilience/blob/main/docs/features/deadlines.md)
for both halves, and [the cancellation deep dive](https://github.com/nresilience/NResilience/blob/main/docs/deep-dives/cancellation.md)
for what an inherited deadline costs and why it is opt-in.
