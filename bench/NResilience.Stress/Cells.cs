using Lib = NResilience;

namespace NResilience.Stress;

/// <summary>Which arm a result belongs to, and which run of it this is.</summary>
internal readonly record struct CellIdentity(string Section, string Arm, int Repetition);

/// <summary>
///     The cell matrix and the child-mode dispatch: one fresh process per cell. The pairing into
///     sections and the arm catalog live here, so <c>Program</c> stays orchestration only.
/// </summary>
internal static class Cells
{
    /// <summary>
    ///     How many times each cell runs. A single run of a cell has no error bar, and this
    ///     suite's smaller claims - a floor that is "a wash", a handler at "parity" - are
    ///     differences of a few percent, which is inside the run-to-run spread of a laptop.
    ///     Three runs give a median to quote and a spread to publish alongside it.
    /// </summary>
    public const int Repetitions = 3;

    /// <summary>Pacer threads for the open-loop sections. See <see cref="LoadDriver.RunOpenLoopAsync" />.</summary>
    private const int Pacers = 2;

    /// <summary>
    ///     Outstanding-op ceiling for the open-loop sections: a backstop against unbounded memory
    ///     if an arm wedges, set far above what any arm here reaches. An arrival dropped against
    ///     it marks the row untrustworthy rather than silently reducing the offered rate.
    /// </summary>
    private const long MaxOutstanding = 500_000;

    public sealed record CellSpec(
        string Section,
        string Arm,
        LoadMode Mode,
        int Dop,
        double OfferedRate,
        bool ServerGc,
        bool ConcurrentGc,
        TimeSpan Warmup,
        TimeSpan Window,
        int Repetition);

    /// <summary>
    ///     Builds the matrix. Cells are emitted grouped and then shuffled with a fixed seed:
    ///     grouped they would run every NResilience arm before every Polly arm in each block, so
    ///     on a machine that warms up over a 40-minute run the whole thermal drift would land on
    ///     one library. Shuffling turns that bias into noise, and the fixed seed keeps the order
    ///     reproducible between runs.
    /// </summary>
    public static List<CellSpec> Build(bool quick, TimeSpan? warmupOverride = null, TimeSpan? windowOverride = null, int? repetitionsOverride = null)
    {
        var warmup = warmupOverride ?? TimeSpan.FromSeconds(quick ? 1 : 2);
        var window = windowOverride ?? TimeSpan.FromSeconds(quick ? 2 : 5);
        var repetitions = repetitionsOverride ?? (quick ? 1 : Repetitions);
        var cores = Environment.ProcessorCount;

        // One worker (no concurrency at all), one per core (saturated), and eight per core (the
        // thread pool holding far more in flight than it has threads for). The intermediate
        // rungs told the same story as the endpoints, so they are gone: the run-time they cost
        // buys repetitions instead, which the claims need more.
        int[] dops = quick ? [cores] : [1, cores, cores * 8];

        // Absolute rates, offered identically to both arms of a pair, and both well below the
        // slower arm's closed-loop capacity so that a difference is queueing rather than
        // saturation. Capped where it is because the pacers demonstrably hold these schedules
        // and do not hold higher ones: at 800k/s they fell milliseconds behind, which would make
        // every latency figure a measurement of the driver.
        double[] latencyRates = quick ? [200_000] : [50_000, 200_000];
        double[] budgetRates = quick ? [20_000] : [20_000, 100_000];

        // HTTP is transport-bound at every DOP tried, and B/op did not move between DOP 8 and
        // DOP 64, so the second rung bought nothing the first does not already say.
        int[] httpDops = [8];

        var specs = new List<CellSpec>();

        for (var repetition = 1; repetition <= repetitions; repetition++)
        {
            foreach (var dop in dops)
            {
                foreach (var gc in GcModes())
                {
                    foreach (var arm in ExecutorArms)
                        specs.Add(new CellSpec("executor", arm, LoadMode.ClosedLoop, dop, 0, gc.Server, gc.Concurrent, warmup, window, repetition));
                }
            }

            // The open-loop sections run in server/concurrent GC only - the production setting.
            // Their question is latency and delivered load at a stated offered rate, and the GC
            // mode is the executor section's axis, not theirs.
            foreach (var rate in latencyRates)
            {
                foreach (var arm in LatencyArms)
                    specs.Add(new CellSpec("latency", arm, LoadMode.OpenLoop, Pacers, rate, true, true, warmup, window, repetition));
            }

            foreach (var rate in budgetRates)
            {
                foreach (var arm in BudgetArms)
                    specs.Add(new CellSpec("budget", arm, LoadMode.OpenLoop, Pacers, rate, true, true, warmup, window, repetition));
            }

            foreach (var dop in httpDops)
            {
                foreach (var gc in GcModes())
                {
                    foreach (var arm in HttpArms)
                        specs.Add(new CellSpec("http", arm, LoadMode.ClosedLoop, dop, 0, gc.Server, gc.Concurrent, warmup, window, repetition));
                }
            }
        }

        Shuffle(specs);

        return specs;

        static (bool Server, bool Concurrent)[] GcModes() =>
        [
            (Server: false, Concurrent: false),
            (Server: true, Concurrent: true),
        ];

        static void Shuffle(List<CellSpec> specs)
        {
            var random = new Random(20260910);

            for (var i = specs.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (specs[i], specs[j]) = (specs[j], specs[i]);
            }
        }
    }

    private static readonly string[] ExecutorArms =
    [
        "raw",
        "lib-passthrough", "lib-trivial", "lib-default", "lib-default-cancellable", "lib-retry-twice",
        "polly-empty", "polly-retry-only", "polly-retry-timeout", "polly-retry-timeout-cancellable", "polly-retry-twice",
    ];

    private static readonly string[] LatencyArms = ["raw", "lib-default", "polly-retry-timeout"];

    private static readonly string[] BudgetArms =
    [
        "lib-budgeted-retry", "polly-no-budget",
        "lib-budgeted-retry-harsh", "polly-no-budget-harsh",
    ];

    private static readonly string[] HttpArms = ["http-raw", "http-raw-flaky", "http-lib", "http-lib-minimal", "http-polly"];

    /// <summary>Runs one cell in this process and returns its result, GC-mode self-check included.</summary>
    public static async Task<CellResult> RunAsync(CellSpec spec)
    {
        var identity = new CellIdentity(spec.Section, spec.Arm, spec.Repetition);

        var result = spec.Section switch
        {
            "executor" => await RunExecutorAsync(identity, spec).ConfigureAwait(false),
            "latency" => await RunLatencyAsync(identity, spec).ConfigureAwait(false),
            "budget" => await RunBudgetAsync(identity, spec).ConfigureAwait(false),
            "http" => await RunHttpAsync(identity, spec).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unknown cell section: {spec.Section}"),
        };

        // The self-check: a child asked for a GC mode that did not take reports it rather than
        // trusting the numbers. The env overrides win over runtimeconfig.json, but this makes
        // the suite self-verifying instead of trusted.
        var actual = RuntimeMode.Current();
        string? gcError = null;

        if (actual.ServerGc != spec.ServerGc || actual.ConcurrentGc != spec.ConcurrentGc)
            gcError = $"asked for {(spec.ServerGc ? "server" : "workstation")}/{(spec.ConcurrentGc ? "concurrent" : "non-concurrent")}, got {actual}";

        return result with { GcModeVerified = gcError is null, GcModeError = gcError };
    }

    private static Task<CellResult> RunExecutorAsync(CellIdentity identity, CellSpec spec) =>
        LoadDriver.RunClosedLoopAsync(identity, spec.Dop, spec.Warmup, spec.Window, Factory(spec.Arm));

    private static Task<CellResult> RunLatencyAsync(CellIdentity identity, CellSpec spec) =>
        LoadDriver.RunOpenLoopAsync(identity, spec.Dop, spec.OfferedRate, MaxOutstanding, spec.Warmup, spec.Window, Factory(spec.Arm));

    private static Task<CellResult> RunBudgetAsync(CellIdentity identity, CellSpec spec) =>
        LoadDriver.RunOpenLoopAsync(identity, spec.Dop, spec.OfferedRate, MaxOutstanding, spec.Warmup, spec.Window, Factory(spec.Arm));

    private static Func<OpCounters, LatencyHistogram, OpDelegate> Factory(string arm) =>
        arm switch
        {
            "raw" => Arms.BuildRaw,
            "lib-passthrough" => Arms.BuildPassthrough,
            "lib-trivial" => Arms.BuildTrivial,
            "lib-default" => Arms.BuildDefault,
            "lib-default-cancellable" => Arms.BuildDefaultCancellable,
            "lib-retry-twice" => Arms.BuildRetryTwice,
            "polly-empty" => Arms.BuildPollyEmpty,
            "polly-retry-only" => Arms.BuildPollyRetryOnly,
            "polly-retry-timeout" => Arms.BuildPollyRetryTimeout,
            "polly-retry-timeout-cancellable" => Arms.BuildPollyRetryTimeoutCancellable,
            "polly-retry-twice" => Arms.BuildPollyRetryTwice,

            // The 1-in-4 pattern is a dependency failing a quarter of what it is sent; the harsh
            // pattern is one failing half. Both are counted across the whole cell, so they are
            // properties of the dependency rather than of any one caller.
            "lib-budgeted-retry" => (c, h) => Arms.BuildBudgetedRetry(c, h, failurePeriod: 4),
            "polly-no-budget" => (c, h) => Arms.BuildPollyUnbudgetedRetry(c, h, failurePeriod: 4),
            "lib-budgeted-retry-harsh" => (c, h) => Arms.BuildBudgetedRetry(c, h, failurePeriod: 2),
            "polly-no-budget-harsh" => (c, h) => Arms.BuildPollyUnbudgetedRetry(c, h, failurePeriod: 2),

            _ => throw new InvalidOperationException($"Unknown arm: {arm}"),
        };

    private static async Task<CellResult> RunHttpAsync(CellIdentity identity, CellSpec spec)
    {
        await using var server = await HttpEchoServer.StartAsync().ConfigureAwait(false);

        // The matched HTTP policies: retry x2 on 5xx + 10 s attempt timeout, GET only. Budget
        // and breaker are off for the matched arms: the budget's refusals are the behavioral
        // section's story, and a 1-in-4 503 pattern never trips the breaker. The server counts
        // every request that lands, so Att/op is observed amplification rather than an
        // inference from retry counters.
        var (client, url) = spec.Arm switch
        {
            // Two baselines, both plain clients over the shared transport. http-raw against
            // /echo is the all-200 reference; http-raw-flaky against /flaky is the one the
            // retrying arms are actually read against, because it faces the same endpoint and
            // the same status mix at exactly one request per op. Without it, "cost over raw
            // transport" silently charges the handler for the extra requests a retrying client
            // sends - about 0.24 of a request per op here, which is most of the apparent gap.
            "http-raw" => (Arms.BuildPlainClient(), server.EchoUrl),
            "http-raw-flaky" => (Arms.BuildPlainClient(), server.FlakyUrl),
            "http-lib" => (BuildLibClient(minimal: false), server.FlakyUrl),
            "http-lib-minimal" => (BuildLibClient(minimal: true), server.FlakyUrl),
            "http-polly" => (PollyHttp.CreateClient(), server.FlakyUrl),
            _ => throw new InvalidOperationException($"Unknown HTTP arm: {spec.Arm}"),
        };

        using (client)
        {
            return await LoadDriver.RunClosedLoopAsync(
                identity,
                spec.Dop,
                spec.Warmup,
                spec.Window,
                (c, h) => Arms.BuildHttpOp(c, h, client, url),
                server).ConfigureAwait(false);
        }
    }

    private static HttpClient BuildLibClient(bool minimal)
    {
        var policy = Lib.Resilience.Http with
        {
            Attempts = 3,
            Backoff = Lib.Backoff.None,
            Budget = Lib.RetryBudget.None,
        };

        var options = new Lib.HttpResilienceOptions
        {
            BreakerPerHost = false,
        };

        if (minimal)
        {
            // The decomposition arm: the handler's on-by-default features off - the stall bound
            // (wrapper plus timer per bodied response), the rate-limit header reads, the
            // nested-retry stamping. Paired with http-lib in the same run, the B/op gap between
            // them is what that feature set costs as a package.
            options.Quota = null;
            options.DetectNestedRetries = false;
            policy = policy with { BoundProgress = false };
        }

        return Lib.HttpResilience.CreateClient(policy, options, Arms.BuildTransport());
    }
}
