using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Grpc.Core;
using Grpc.Core.Interceptors;
using NResilience.Extensions;
using NResilience.Grpc;
using NResilience.Http;
using NResilience.Probes;
using NResilience.Testing;

namespace NResilience.AotProbe;

/// <summary>
///     The Native AOT gate: a published binary that actually executes a policy and asserts the
///     result, then re-runs the allocation budgets under AOT.
///     Publishing without warnings proves the code is AOT-clean. It does not prove there is no AOT
///     allocation cliff, and that is the claim worth defending - Polly boxes state per layer per
///     execution under Native AOT, so its zero-allocation claim is false there. A gate that only
///     checked for warnings would never have caught that.
///     Exit code 0 means every budget held. Anything else fails the build.
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        Log($"framework    : {RuntimeInformation.FrameworkDescription}");
        Log($"architecture : {RuntimeInformation.ProcessArchitecture}");
        Log($"server GC    : {GCSettings.IsServerGC}");
        Console.WriteLine();

        var failures = 0;

        failures += await CorrectnessAsync().ConfigureAwait(false);
        failures += await ShippingLibraryAsync().ConfigureAwait(false);
        failures += await ExtensionsAsync().ConfigureAwait(false);
        failures += await GrpcAsync().ConfigureAwait(false);
        failures += await BudgetsAsync().ConfigureAwait(false);

        Console.WriteLine();
        Log(failures == 0 ? "AOT gate: PASS" : $"AOT gate: FAIL ({failures} check(s))");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>The published binary has to do the thing, not merely start.</summary>
    private static async Task<int> CorrectnessAsync()
    {
        var failures = 0;

        var executor = new FusedExecutor(FusedPolicy.Default);

        var value = await executor.RunAsync(Gate.SuspendAsync).ConfigureAwait(false);
        failures += Check("suspending call returns the callback's value", value == Gate.Value);

        value = await executor.RunAsync(static (_, ct) => Gate.CompleteAsync(ct), 0).ConfigureAwait(false);
        failures += Check("stateful overload returns the callback's value", value == Gate.Value);

        var counter = new Gate.FailCounter(2);
        value = await executor.RunAsync(Gate.SuspendThenFailAsync, counter).ConfigureAwait(false);
        failures += Check("two transient failures are retried to success", value == Gate.Value);

        var permanent = new FusedExecutor(FusedPolicy.NoTimeout with { Attempts = 3 });
        var threw = false;

        try
        {
            await permanent.RunAsync(static _ => Task.FromException<int>(new InvalidOperationException("permanent"))).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        failures += Check("an unrecognized exception is not retried and propagates", threw);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().ConfigureAwait(false);
        var cancelledCorrectly = false;

        try
        {
            await executor.RunAsync(Gate.SuspendAsync, cancelled.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelledCorrectly = true;
        }

        failures += Check("caller cancellation propagates untouched", cancelledCorrectly);

        return failures;
    }

    /// <summary>
    ///     The shipping library, published Native AOT.
    ///     "No reflection anywhere in core" is a claim, and the only thing that can check it is a
    ///     trimmed, AOT-compiled binary running the real executor. Publishing with the trim and AOT
    ///     analyzers on and warnings as errors proves the code is clean; running it proves the
    ///     per-result-type judge cache and the generic-struct invoker survive whole-program
    ///     compilation, which is where an implementation that reached for reflection would break.
    /// </summary>
    private static async Task<int> ShippingLibraryAsync()
    {
        Console.WriteLine();
        var failures = 0;

        var value = await Resilience.Default.RunAsync(Gate.SuspendAsync).ConfigureAwait(false);
        failures += Check("library: a suspending call returns the callback's value", value == Gate.Value);

        value = await Resilience.Default.RunAsync(static (_, ct) => Gate.CompleteAsync(ct), 0).ConfigureAwait(false);
        failures += Check("library: the stateful overload returns the callback's value", value == Gate.Value);

        var instant = Resilience.Default with { Backoff = Backoff.None, Attempts = 3 };
        var counter = new Gate.FailCounter(2);
        value = await instant.RunAsync(Gate.SuspendThenFailAsync, counter).ConfigureAwait(false);
        failures += Check("library: two transient failures are retried to success", value == Gate.Value);

        var threw = false;

        try
        {
            await instant.RunAsync(static _ => Task.FromException<int>(new InvalidOperationException("permanent"))).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        failures += Check("library: an unrecognized exception is not retried and propagates", threw);

        // The result-classification cache is the one place a naive implementation would reach for
        // reflection, so it is exercised over two distinct result types in one process.
        var classified = instant with
        {
            Classify = Classifier.Default.OnResult<int>(static v => v == 503 ? Verdict.Transient : Verdict.Ok),
        };

        var failing = await classified.TryRunAsync(static ct => Task.FromResult(503)).ConfigureAwait(false);
        failures += Check("library: a result rule fires under AOT", !failing.IsSuccess && failing.Attempts.Count == 3);

        var unjudged = await classified.TryRunAsync(static ct => Task.FromResult("fine")).ConfigureAwait(false);
        failures += Check("library: an unjudged result type is a success under AOT", unjudged.IsSuccess);

        // The hedged loop is the third execution path, and the only one that type-tests a value at
        // runtime - `loser is IAsyncDisposable` - which is exactly the shape a trimmer can break. No
        // hedge fires here (the callback is instant and the floor is 10 ms); what is being checked is
        // that the path compiles, runs, and returns the callback's answer.
        var hedged = instant with { Hedge = Hedge.At(0.95) };
        value = await hedged.RunAsync(Gate.SuspendAsync).ConfigureAwait(false);
        failures += Check("library: the hedged execution path runs under AOT", value == Gate.Value);

        var disposable = await hedged.TryRunAsync(static _ => Task.FromResult(new MemoryStream())).ConfigureAwait(false);
        disposable.Value?.Dispose();
        failures += Check("library: a hedged call over a disposable result type succeeds under AOT", disposable.IsSuccess);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().ConfigureAwait(false);
        var cancelledCorrectly = false;

        try
        {
            await Resilience.Default.RunAsync(Gate.SuspendAsync, cancelled.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelledCorrectly = true;
        }

        failures += Check("library: caller cancellation propagates untouched", cancelledCorrectly);

        // The streaming path: an async iterator over the suspending gate, retried nothing, drained
        // in full. It is the one execution path whose frame the AOT toolchain could treat
        // differently - iterators are the runtime's own state machines - so it is executed here
        // rather than merely published past.
        var streamSum = 0;
        var streamed = Resilience.Default.RunAsync(static ct => StreamGate.SuspendAsync(ct));

        await foreach (var item in streamed.ConfigureAwait(false))
            streamSum += item;

        failures += Check("library: the streaming path runs under AOT", streamSum == StreamGate.Items * StreamGate.Value);

        // A first element the classifier refuses on the final attempt throws rather than yields: the
        // fall-through constructor in FailureException.Build is load-bearing for streaming, so it executes
        // here rather than being merely compiled past.
        var rejecting = Resilience.Default with
        {
            Classify = Classifier.Default.OnResult<int>(static v => v < 0 ? Verdict.Permanent : Verdict.Ok),
        };

        var refused = false;
        var refusedCorrectly = false;

        try
        {
            await foreach (var _ in rejecting.RunAsync(StreamGate.RejectedAsync).ConfigureAwait(false))
            {
            }
        }
        catch (CallRejectedException rejected)
        {
            refused = true;
            refusedCorrectly = rejected.Reason == StopReason.Permanent;
        }

        failures += Check("library: a refused first element throws rather than yields under AOT", refused && refusedCorrectly);

        failures += await GuardsAsync().ConfigureAwait(false);
        failures += await TelemetryAsync().ConfigureAwait(false);
        failures += await TestingPackageAsync().ConfigureAwait(false);
        failures += await HttpPackageAsync().ConfigureAwait(false);

        return failures;
    }

    /// <summary>
    ///     The HTTP handler under AOT. It ships, so it is published and run rather than merely
    ///     compiled - and the request clone is the part worth running, because building a fresh
    ///     message and copying its headers is the closest the library gets to the kind of dynamic work
    ///     whole-program compilation is entitled to break.
    /// </summary>
    private static async Task<int> HttpPackageAsync()
    {
        Console.WriteLine();
        var failures = 0;

        var events = new EventRecorder();

        var transport = new ScriptedHttpHandler { CaptureBodies = true }
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.OK);

        var policy = Resilience.Http with
        {
            Backoff = Backoff.None,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            OnEvent = events.Record,
        };

        using var handler = new ResilienceHandler(transport, policy);
        using var client = new HttpClient(handler);

        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri("https://api.invalid/thing"))
        {
            Content = new StringContent("body"),
        };

        request.Headers.Add("X-Trace", "abc");

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        var lastRequest = transport.Requests[^1];
        var lastTrace = lastRequest.Headers.TryGetValues("X-Trace", out var trace) ? trace.FirstOrDefault() : null;

        failures += Check("http: a 503 is retried to a 200", response.StatusCode == HttpStatusCode.OK);
        failures += Check("http: each attempt got its own request", transport.CallCount == 2);
        failures += Check("http: the clone carried the headers and the body", lastTrace == "abc" && lastRequest.Body == "body");
        failures += Check("http: the breaker was scoped to the host", handler.BreakersByHost().ContainsKey("api.invalid"));
        failures += Check("http: the nested-retry header was stamped", lastRequest.Headers.Contains(ResilienceHttp.NestedRetryHeader));

        failures += Check(
            "http: the events came back in order",
            events.Kinds is [CallEventKind.Attempt, CallEventKind.Retrying, CallEventKind.Attempt, CallEventKind.Succeeded]);

        return failures;
    }

    /// <summary>
    ///     The testing package under AOT. It ships, so it is gated like everything else that
    ///     ships: a scripted sequence driving a real policy, and a recorder reading the events back.
    /// </summary>
    private static async Task<int> TestingPackageAsync()
    {
        Console.WriteLine();
        var failures = 0;

        var events = new EventRecorder();
        var instant = Resilience.Default with { Backoff = Backoff.None, OnEvent = events.Record };

        var calls = Sequence.For<int>()
            .Throws(new TimeoutException())
            .Returns(Gate.Value);

        var result = await instant.TryRunAsync(ct => calls.NextAsync(ct)).ConfigureAwait(false);

        failures += Check("testing: a scripted sequence retries to success", result.IsSuccess && result.Value == Gate.Value);
        failures += Check("testing: the sequence served every scripted step", calls.CallCount == 2 && calls.Remaining == 0);

        failures += Check(
            "testing: the recorder captured the whole event sequence",
            events.Kinds is [CallEventKind.Attempt, CallEventKind.Retrying, CallEventKind.Attempt, CallEventKind.Succeeded]);

        failures += Check(
            "testing: the recorder reads the boxed result back",
            Equals(events.Single(CallEventKind.Succeeded).Result, Gate.Value));

        return failures;
    }

    /// <summary>
    ///     Telemetry under AOT. The one thing here that whole-program compilation could plausibly break
    ///     is the boxed result on <see cref="CallEvent.Result" />: it is the only place the executor
    ///     converts a generic <c>T</c> to <see cref="object" />, and the <c>typeof(T)</c> test that
    ///     keeps the void entry points from handing out a box of an internal type is folded by the
    ///     compiler rather than evaluated.
    /// </summary>
    private static async Task<int> TelemetryAsync()
    {
        var failures = 0;

        var kinds = new List<CallEventKind>();
        var results = new List<object?>();

        var watched = Resilience.Default with
        {
            Attempts = 2,
            Backoff = Backoff.None,
            Deadline = Timeout.InfiniteTimeSpan,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Name = "aot",
            OnEvent = e =>
            {
                kinds.Add(e.Kind);
                results.Add(e.Result);
            },
        };

        var value = await watched.RunAsync(static _ => Task.FromResult(41)).ConfigureAwait(false);

        failures += Check("library: a successful call raises Attempt then Succeeded under AOT",
            value == 41 && kinds is [CallEventKind.Attempt, CallEventKind.Succeeded]);

        failures += Check("library: the boxed result survives AOT", results is [41, 41]);

        kinds.Clear();
        await watched.TryRunAsync(static _ => Task.FromException<int>(new IOException("aot"))).ConfigureAwait(false);
        failures += Check("library: a retried call raises Retrying under AOT", kinds.Contains(CallEventKind.Retrying));

        kinds.Clear();
        results.Clear();
        await watched.TryRunAsync(static _ => Task.CompletedTask).ConfigureAwait(false);
        failures += Check("library: a void call reports no result under AOT", results.TrueForAll(static r => r is null));

        return failures;
    }

    /// <summary>
    ///     The breaker and budget guards under AOT. Both hold mutable state behind a lock and both feed the
    ///     executor's rejection path, so what this checks is that the state machine and the guarded
    ///     rejection survive whole-program compilation - including <c>Task.Delay</c> on a
    ///     <see cref="TimeProvider" />, which the guard uses and which nothing else in the probe does.
    /// </summary>
    private static async Task<int> GuardsAsync()
    {
        var failures = 0;

        var instant = Resilience.Default with
        {
            Backoff = Backoff.None,
            Deadline = Timeout.InfiniteTimeSpan,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
        };

        var breaker = new Breaker(new BreakerSettings { ConsecutiveFailures = 2 }) { Name = "aot" };
        var guarded = instant with { Breaker = breaker, Attempts = 2, Budget = RetryBudget.None };

        var ran = false;

        var tripped = await guarded.TryRunAsync(static _ => Task.FromException<int>(new IOException("aot")))
            .ConfigureAwait(false);

        failures += Check("library: two transient attempts open the breaker", breaker.State == BreakerState.Open);
        failures += Check("library: the breaker records an opening time", breaker.OpenedAt is not null);
        failures += Check("library: the tripped operation still reports its own failure", !tripped.IsSuccess);

        var refused = await guarded.TryRunAsync(_ =>
        {
            ran = true;
            return Task.FromResult(1);
        }).ConfigureAwait(false);

        failures += Check("library: an open breaker refuses the call without running it", !ran);

        failures += Check(
            "library: a refusal reports DependencyUnavailable",
            refused.StopReason == StopReason.DependencyUnavailable && refused.Exception is CallRejectedException);

        breaker.Reset();
        failures += Check("library: Reset closes the breaker", breaker.State == BreakerState.Closed);

        // A quarter of a token per success and no floor, so the bucket funds exactly one retry and
        // the operation after it is refused at the throttle step rather than at admission.
        var metered = instant with { Attempts = 3, Budget = RetryBudget.Of(0.25, 0) };

        await metered.TryRunAsync(static _ => Task.FromException<int>(new IOException("aot"))).ConfigureAwait(false);

        var throttled = await metered
            .TryRunAsync(static _ => Task.FromException<int>(new IOException("aot")))
            .ConfigureAwait(false);

        failures += Check(
            "library: an exhausted budget refuses the retry",
            throttled.StopReason == StopReason.BudgetExhausted && throttled.Attempts.Count == 1);

        return failures;
    }

    /// <summary>
    ///     The same budgets the JIT gate enforces, against the same shipping executor. The stand-in
    ///     arms stay in the correctness section above, where what they prove is that the harness
    ///     itself survives AOT.
    ///     The numbers are duplicated here rather than shared, because this project must not reference
    ///     the test project, and because an AOT-specific divergence is exactly what this gate exists to
    ///     surface.
    /// </summary>
    /// <summary>
    ///     The DI, configuration and telemetry surface, in the published binary.
    ///     <para>
    ///         This is the surface most entitled to break under whole-program compilation, because
    ///         configuration binding is reflection by default and a container resolves types by
    ///         <c>Type</c>. The binding source generator is what makes it trim-safe, and a generator that
    ///         silently declined to run would show up here as a policy full of defaults rather than as a
    ///         build warning - which is why the assertion is on the projected values.
    ///     </para>
    /// </summary>
    private static async Task<int> ExtensionsAsync()
    {
        Console.WriteLine();
        var failures = 0;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Resilience:api:Preset"] = "Http",
                ["Resilience:api:Attempts"] = "4",
                ["Resilience:api:Deadline"] = "00:00:20",
                ["Resilience:api:Backoff:Max"] = "00:00:01",
                ["Resilience:api:Breaker:ConsecutiveFailures"] = "2",
                ["Resilience:api:Logging"] = "Verbose",
            })
            .Build();

        var records = new RecordingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(records).SetMinimumLevel(LogLevel.Trace));
        services.AddResilience(configuration.GetSection("Resilience"));
        services.AddHttpClient("probe").AddResilience("api", o => o.OwnTransportTimeout = true);

        // The health check registers through an explicit factory rather than AddCheck<T>(), which
        // resolves through ActivatorUtilities. Publishing proves that compiled; running the check
        // and reading a row out of it is what proves nothing on the path needed reflection.
        services.AddHealthChecks().AddResilience();

        using var provider = services.BuildServiceProvider();
        var policies = provider.GetRequiredService<IResiliencePolicies>();

        var api = policies["api"];

        failures += Check("configuration binds under AOT (attempts)", api.Attempts == 4);
        failures += Check("configuration binds under AOT (deadline)", api.Deadline == TimeSpan.FromSeconds(20));
        failures += Check("configuration binds under AOT (backoff cap)", api.Backoff.Max == TimeSpan.FromSeconds(1));
        failures += Check("the preset resolves under AOT", ReferenceEquals(api.Classify, Classifier.Http));
        failures += Check("a configured breaker is live under AOT", api.Breaker is { Settings.ConsecutiveFailures: 2 });
        failures += Check("the policy is named after its registration", api.Name == "api");

        var result = await api.RunAsync(Gate.SuspendAsync).ConfigureAwait(false);
        failures += Check("a resolved policy executes under AOT", result == Gate.Value);

        // Unknown-key detection is the one binder feature that reads property *names* rather than
        // setting them, so it is the one a trimmed or source-generated binder is most likely to drop.
        // Dropping it silently would leave an AOT app with exactly the failure the flag exists to
        // prevent: a renamed key binding nothing, and a policy quietly on its defaults.
        var typo = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Resilience:api:Attemptss"] = "4" })
            .Build();

        var strict = new ServiceCollection();
        strict.AddResilience(typo.GetSection("Resilience"));

        using var strictProvider = strict.BuildServiceProvider();
        var caught = false;

        try
        {
            _ = strictProvider.GetRequiredService<IResiliencePolicies>()["api"];
        }
        catch (ResilienceConfigurationException)
        {
            caught = true;
        }

        failures += Check("an unrecognized configuration key is refused under AOT", caught);

        var health = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync().ConfigureAwait(false);
        var resilience = health.Entries["resilience"];

        failures += Check("the health check runs under AOT", resilience.Status == HealthStatus.Healthy);
        failures += Check("the health check reports the configured breaker", resilience.Data.ContainsKey("breaker:api"));

        // Publishing proves [LoggerMessage] compiled; only running proves the generator ran and the
        // record reached a provider. 1004 is CallSucceeded, raised to Information by the Verbose
        // profile the section asked for.
        failures += Check("the log listener records under AOT", records.Contains(1004));
        failures += Check("the effective policy is recorded under AOT", records.Contains(1020));

        failures += Check(
            "the log category is the policy's own",
            records.Categories.Contains(ResilienceLogging.CategoryFor("api")));

        // The instruments have to actually record, not merely exist: a MeterListener is the only
        // way to tell a working instrument from a silently inert one.
        long calls = 0;

        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name == "nresilience.calls")
                    listener.EnableMeasurementEvents(instrument);
            },
        };

        meterListener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref calls, value));
        meterListener.Start();

        await policies["api"].RunAsync(Gate.SuspendAsync).ConfigureAwait(false);
        meterListener.Dispose();

        failures += Check("the meter records under AOT", calls == 1);

        var transport = new ScriptedHttpHandler()
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.OK);

        var clientServices = new ServiceCollection();
        clientServices.AddResilience("client", Resilience.Http with { Backoff = Backoff.None });
        clientServices.AddHttpClient("probe").AddResilience("client");

        clientServices.ConfigureAll<HttpClientFactoryOptions>(o =>
            o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = transport));

        using var clientProvider = clientServices.BuildServiceProvider();
        using var client = clientProvider.GetRequiredService<IHttpClientFactory>().CreateClient("probe");

        failures += Check("the registration owns the transport timeout", client.Timeout == Timeout.InfiniteTimeSpan);

        using var response = await client
            .GetAsync(new Uri("https://api.test/thing"))
            .ConfigureAwait(false);

        failures += Check("a registered client retries under AOT", response.StatusCode == HttpStatusCode.OK && transport.CallCount == 2);

        failures += await RateLimitAsync().ConfigureAwait(false);

        return failures;
    }

    /// <summary>
    ///     The limiter, under AOT: the options bind through the same source generator, the platform's
    ///     limiters run without reflection, and a refusal is classified by the executor rather than by a
    ///     classifier - which is what keeps it off the retry budget.
    /// </summary>
    private static async Task<int> RateLimitAsync()
    {
        var failures = 0;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimit:Concurrency"] = "1",
                ["RateLimit:PerHost"] = "true",
            })
            .Build();

        var options = new RateLimitOptions();
        configuration.GetSection("RateLimit").Bind(options);

        failures += Check("limiter options bind under AOT", options is { Concurrency: 1, PerHost: true, QueueLimit: 0 });

        using var limiter = options.ToLimiter();

        using (await limiter.AcquireOrThrowAsync("probe").ConfigureAwait(false))
        {
            var refused = false;

            try
            {
                using var _ =
                    await limiter.AcquireOrThrowAsync("probe").ConfigureAwait(false);
            }
            catch (RateLimitedException error)
            {
                refused = error.Limiter == "probe";
            }

            failures += Check("a limiter with no permits left throws under AOT", refused);
        }

        // The one behavior this whole block exists for, re-checked under AOT: the refusal is
        // throttling that the retry budget is not charged for.
        var budget = RetryBudget.Of(minimumPerSecond: 1);

        var policy = Resilience.Default with
        {
            Attempts = 3,
            Backoff = Backoff.None,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Deadline = Timeout.InfiniteTimeSpan,
            Budget = budget,
        };

        var limited = await policy
            .TryRunAsync(static _ => Task.FromException<int>(new RateLimitedException("probe")))
            .ConfigureAwait(false);

        failures += Check("a refusal is retried to exhaustion under AOT", limited.Attempts.Count == 3);
        failures += Check("a refusal is self-imposed throttling under AOT", limited.Attempts[0].Verdict is { Kind: VerdictKind.Throttled, SelfImposed: true });
        failures += Check("a refusal does not spend retry budget under AOT", budget.Utilization == 0);

        return failures;
    }

    /// <summary>
    ///     The gRPC package, published Native AOT.
    ///     <para>
    ///         Driven over a scripted continuation rather than a channel: no sockets, no codegen, and
    ///         no protobuf, so what this proves is that <i>our</i> interceptor survives whole-program
    ///         compilation rather than that grpc-dotnet does. The call object it hands back is built
    ///         from delegates and a generic <see cref="TaskCompletionSource{TResult}" />, which is the
    ///         shape an implementation that reached for reflection would break on.
    ///     </para>
    /// </summary>
    private static async Task<int> GrpcAsync()
    {
        Console.WriteLine();
        var failures = 0;

        var unavailable = new RpcException(new Status(StatusCode.Unavailable, "probe"));
        var notFound = new RpcException(new Status(StatusCode.NotFound, "probe"));
        var exhausted = new RpcException(new Status(StatusCode.ResourceExhausted, "probe"));

        failures += Check("the gRPC classifier resolves under AOT", GrpcResilience.Classifier.ClassifyException(unavailable).Kind == VerdictKind.Transient);
        failures += Check("a gRPC answer is permanent under AOT", GrpcResilience.Classifier.ClassifyException(notFound).Kind == VerdictKind.Permanent);
        failures += Check("gRPC resource exhaustion is throttling under AOT", GrpcResilience.Classifier.ClassifyException(exhausted).Kind == VerdictKind.Throttled);
        failures += Check("the gRPC preset validates under AOT", GrpcResilience.Default.Classify.Equals(GrpcResilience.Classifier));

        var method = new Method<string, string>(
            MethodType.Unary, "probe.Probe", "Get", Marshallers.StringMarshaller, Marshallers.StringMarshaller);

        var interceptor = new ResilienceInterceptor(
            GrpcResilience.Default with { Backoff = Backoff.None },
            new GrpcResilienceOptions { DeadlineSlack = TimeSpan.FromMilliseconds(50) },
            "probe");

        var attempts = 0;
        DateTime? deadline = null;

        AsyncUnaryCall<string> Continuation(string request, ClientInterceptorContext<string, string> context)
        {
            attempts++;
            deadline = context.Options.Deadline;

            var response = attempts < 3
                ? Task.FromException<string>(new RpcException(new Status(StatusCode.Unavailable, "probe")))
                : Task.FromResult("ok");

            return new AsyncUnaryCall<string>(
                response,
                Task.FromResult(new Metadata()),
                static () => Status.DefaultSuccess,
                static () => [],
                static () => { });
        }

        using var call = interceptor.AsyncUnaryCall(
            "request", new ClientInterceptorContext<string, string>(method, null, default), Continuation);

        var value = await call.ResponseAsync.ConfigureAwait(false);

        failures += Check("a gRPC call is retried to success under AOT", value == "ok" && attempts == 3);
        failures += Check("the winning attempt's status is readable under AOT", call.GetStatus().StatusCode == StatusCode.OK);
        failures += Check("the attempt deadline reaches the wire under AOT", deadline is not null);
        failures += Check("the breaker is scoped per service under AOT", interceptor.Breakers().ContainsKey("probe.Probe"));
        failures += Check("the budget is scoped per service under AOT", interceptor.Budgets().ContainsKey("probe.Probe"));

        var single = 0;

        AsyncUnaryCall<string> Once(string request, ClientInterceptorContext<string, string> context)
        {
            single++;

            return new AsyncUnaryCall<string>(
                Task.FromException<string>(new RpcException(new Status(StatusCode.Unavailable, "probe"))),
                Task.FromException<Metadata>(new RpcException(new Status(StatusCode.Unavailable, "probe"))),
                static () => new Status(StatusCode.Unavailable, "probe"),
                static () => [],
                static () => { });
        }

        using (GrpcResilience.SingleShot())
        {
            using var shot = interceptor.AsyncUnaryCall(
                "request", new ClientInterceptorContext<string, string>(method, null, default), Once);

            try
            {
                await shot.ResponseAsync.ConfigureAwait(false);
            }
            catch (RpcException)
            {
                // The point is the attempt count, not the exception.
            }
        }

        failures += Check("the single-shot scope holds under AOT", single == 1);

        var streamMethod = new Method<string, string>(
            MethodType.ServerStreaming, "probe.Probe", "Watch", Marshallers.StringMarshaller, Marshallers.StringMarshaller);

        var streams = 0;

        AsyncServerStreamingCall<string> StreamContinuation(string request, ClientInterceptorContext<string, string> context)
        {
            streams++;

            return new AsyncServerStreamingCall<string>(
                new ProbeStream(streams < 2 ? null : ["a", "b"]),
                Task.FromResult(new Metadata()),
                static () => Status.DefaultSuccess,
                static () => [],
                static () => { });
        }

        var received = 0;

        using (var stream = interceptor.AsyncServerStreamingCall(
                   "request", new ClientInterceptorContext<string, string>(streamMethod, null, default), StreamContinuation))
        {
            while (await stream.ResponseStream.MoveNext(CancellationToken.None).ConfigureAwait(false))
                received++;
        }

        // The streaming path is the one that hands a live enumerator across the boundary, and the
        // adapter that carries it is generic over the response type - which is what an implementation
        // reaching for reflection would break on.
        failures += Check("a gRPC server stream is retried to its first message under AOT", streams == 2 && received == 2);

        return failures;
    }

    /// <summary>A scripted response stream for the probe: fails before its first message, or streams.</summary>
    private sealed class ProbeStream(string[]? messages) : IAsyncStreamReader<string>
    {
        private int _index = -1;

        public string Current => messages![_index];

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (messages is null)
                return Task.FromException<bool>(new RpcException(new Status(StatusCode.Unavailable, "probe")));

            if (_index + 1 >= messages.Length)
                return Task.FromResult(false);

            _index++;
            return Task.FromResult(true);
        }
    }

    private static async Task<int> BudgetsAsync()
    {
        Console.WriteLine();
        var failures = 0;

        // .NET 10 / .NET 8, arm64: bytes above an identical un-wrapped callback.
        const double NoiseFloor = 8;
        const double TrivialSuspendingBudget = 368; // measured 328 (320 before the breaker and budget)
        const double DefaultSuspendingBudget = 448; // measured 393 (384 before the breaker and budget)
        const double TryRunSuspendingBudget = 640; // measured 561 (553 before the breaker and budget)
        const double ListenerAllowance = 72; // measured 48: two boxed int results
        const double DefaultStreamingBudget = 1000; // measured 848 B/op above the raw enumeration under the JIT

        var rawSync = await MeasureAsync("raw callback (sync)", Scenarios.RawSync, AllocationCounter.ThreadLocal).ConfigureAwait(false);
        var noneSync = await MeasureAsync("None (sync)", ShippingScenarios.NoneSync, AllocationCounter.ThreadLocal).ConfigureAwait(false);

        var trivialSync = await MeasureAsync("trivial, static+state (sync)", ShippingScenarios.TrivialSyncState, AllocationCounter.ThreadLocal)
            .ConfigureAwait(false);

        var defaultSync = await MeasureAsync("Default, static+state (sync)", ShippingScenarios.DefaultSyncState, AllocationCounter.ThreadLocal)
            .ConfigureAwait(false);

        failures += Check("no AOT cliff: passthrough is free on the synchronous path", noneSync - rawSync <= 0);
        failures += Check("no AOT cliff: static lambda + state is free on the synchronous path", trivialSync - rawSync <= 0);

        // 64 B, and a floor rather than an implementation failure: one linked source per attempt,
        // because the callback needs a token the attempt timeout can cancel and the pooled source's
        // own token must never be handed to user code.
        failures += Check("no AOT cliff: an attempt timeout still costs exactly one linked source", defaultSync - rawSync <= 72);

        // Measure the ValueTask callback surface against its own raw baseline. A Task-returning callback 
        // allocates a task that a ValueTask callback does not; using a Task baseline would incorrectly 
        // attribute the callback's saving to the executor. Native AOT lacks a tiered JIT, so the escape 
        // analysis that optimizes Task allocations under the JIT does not run. This environment 
        // reveals any ValueTask-related allocation cliffs.
        var rawValueSync = await MeasureAsync("raw ValueTask callback (sync)", Scenarios.RawValueSync, AllocationCounter.ThreadLocal)
            .ConfigureAwait(false);

        var trivialValueSync = await MeasureAsync("trivial, ValueTask+state (sync)", ShippingScenarios.TrivialValueSyncState,
            AllocationCounter.ThreadLocal).ConfigureAwait(false);

        var trivialValueAsTaskSync = await MeasureAsync("trivial, ValueTask via AsTask (sync)", ShippingScenarios.TrivialValueAsTaskSyncState,
            AllocationCounter.ThreadLocal).ConfigureAwait(false);

        failures += Check("no AOT cliff: a ValueTask callback is free on the synchronous path", trivialValueSync - rawValueSync <= 0);

        failures += Check("the ValueTask overloads still remove the AsTask() conversion",
            trivialValueAsTaskSync - trivialValueSync >= 64);

        var rawSuspending = await MeasureAsync("raw callback (suspending)", Scenarios.RawSuspending, AllocationCounter.ProcessWide)
            .ConfigureAwait(false);

        var noneSuspending = await MeasureAsync("None (suspending)", ShippingScenarios.NoneSuspending, AllocationCounter.ProcessWide)
            .ConfigureAwait(false);

        var trivialSuspending = await MeasureAsync("trivial (suspending)", ShippingScenarios.TrivialSuspending, AllocationCounter.ProcessWide)
            .ConfigureAwait(false);

        var defaultSuspending = await MeasureAsync("Default (suspending)", ShippingScenarios.DefaultSuspending, AllocationCounter.ProcessWide)
            .ConfigureAwait(false);

        var tryRunSuspending =
            await MeasureAsync("TryRunAsync, Default (suspending)", ShippingScenarios.TryRunDefaultSuspending, AllocationCounter.ProcessWide)
                .ConfigureAwait(false);

        var listenerSuspending =
            await MeasureAsync("Default + listener (suspending)", ShippingScenarios.DefaultListenerSuspending, AllocationCounter.ProcessWide)
                .ConfigureAwait(false);

        failures += Check("no AOT cliff: passthrough is free on the suspending path", noneSuspending - rawSuspending <= NoiseFloor);

        failures += Check(
            "no AOT cliff: the trivial policy stays within its suspending budget",
            trivialSuspending - rawSuspending <= TrivialSuspendingBudget + NoiseFloor);

        failures += Check(
            "no AOT cliff: the real loop stays within its suspending budget",
            defaultSuspending - rawSuspending <= DefaultSuspendingBudget + NoiseFloor);

        // TryRunAsync always materializes the attempt log, and the log is where an AOT-specific
        // divergence would surface: it is the one part of the frame that reaches the heap.
        failures += Check(
            "no AOT cliff: reporting the outcome stays within its suspending budget",
            tryRunSuspending - rawSuspending <= TryRunSuspendingBudget + NoiseFloor);

        // Pay-for-play, under AOT. A listener may cost the results it asked to have boxed and
        // nothing else - 48 B on both target frameworks under the JIT, for the two events on a
        // successful call that carry one.
        failures += Check(
            "no AOT cliff: a listener costs only the results it asked to be boxed",
            listenerSuspending - defaultSuspending <= ListenerAllowance);

        // The streaming path, measured against the identical enumeration with no policy in the
        // middle. Polly boxes state per layer per execution under Native AOT; an async iterator is
        // the shape whose AOT cost would diverge from its JIT cost, so the streaming arm is what
        // makes "IsAotCompatible" a tested claim for the fourth path rather than a hope.
        var rawStreamSuspending = await MeasureAsync("raw stream (suspending)", StreamGate.RawSuspending, AllocationCounter.ProcessWide)
            .ConfigureAwait(false);

        var defaultStreamSuspending = await MeasureAsync("Default, streaming (suspending)", ShippingScenarios.DefaultStreamSuspending,
            AllocationCounter.ProcessWide).ConfigureAwait(false);

        failures += Check(
            "no AOT cliff: the streaming path stays within its suspending budget",
            defaultStreamSuspending - rawStreamSuspending <= DefaultStreamingBudget + NoiseFloor);

        return failures;
    }

    private static async Task<double> MeasureAsync<T>(string name, Func<ValueTask<T>> body, AllocationCounter counter)
    {
        var measurement = await AllocationProbe.MeasureAsync(name, body, counter).ConfigureAwait(false);
        Log($"  {name,-36} {measurement.BytesPerOperation,9:0.0} B/op");
        return measurement.BytesPerOperation;
    }

    private static int Check(string what, bool ok)
    {
        Log($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        return ok ? 0 : 1;
    }

    private static void Log(ref DefaultInterpolatedStringHandler message)
        => Console.WriteLine(string.Create(CultureInfo.InvariantCulture, ref message));

    private static void Log(string message) => Console.WriteLine(message);

    /// <summary>
    ///     Collects event IDs and categories. A real provider rather than a fake logger, because what is
    ///     being checked is that the generated record travels the whole way to one under AOT.
    /// </summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly HashSet<string> _categories = new(StringComparer.Ordinal);
        private readonly HashSet<int> _ids = [];

        internal IReadOnlyCollection<string> Categories
        {
            get
            {
                lock (_categories)
                {
                    return [.. _categories];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);

        public void Dispose()
        {
        }

        internal bool Contains(int id)
        {
            lock (_ids)
            {
                return _ids.Contains(id);
            }
        }

        private void Add(string category, int id)
        {
            lock (_ids)
            {
                _ids.Add(id);
            }

            lock (_categories)
            {
                _categories.Add(category);
            }
        }

        private sealed class Recorder(RecordingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                // Formatted, so a template that cannot render fails here rather than silently.
                _ = formatter(state, exception);
                owner.Add(category, eventId.Id);
            }
        }
    }

}
