using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace NResilience.Stress;

/// <summary>
///     The stress suite: NResilience vs Polly under sustained concurrent load.
///     Published, never gated - same doctrine as the latency harness.
///     <para>
///         Matrix mode enumerates cells and spawns a fresh child process for each, so the GC
///         state a cell reports is attributable to that arm alone. A child runs warmup, then the
///         steady window, then prints one JSON line and exits.
///     </para>
///     <para>
///         Two load modes, because they answer different questions and neither answers the
///         other's. The closed-loop sections hold concurrency fixed and measure what the process
///         sustains - the right instrument for throughput and for per-op allocation. The
///         open-loop sections hold the <i>arrival rate</i> fixed and measure latency and
///         delivered load at a stated offered rate, which is the only way to get a latency
///         number that is not just <c>DOP / throughput</c> restated, and the only way to measure
///         how much work a guard keeps off a dependency without the client's own slowdown
///         reducing the load it offers. See <see cref="LoadMode" />.
///     </para>
///     <para>
///         Every cell runs <see cref="Cells.Repetitions" /> times, shuffled through the run, and
///         the report quotes the median with the observed spread beside it. A single run of a
///         cell cannot support a claim about a few percent, and several of this suite's
///         comparisons are a few percent.
///     </para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // --echo-server: the HTTP section's dependency, in its own process so that its
        // allocation and its collections are not charged to the client under test.
        if (args.Length > 0 && args[0] == "--echo-server")
            return await HttpEchoServer.RunServerAsync().ConfigureAwait(false);

        // --cell <json>: the child mode. One cell, one JSON line on stdout, nothing else.
        if (args.Length > 1 && args[0] == "--cell")
        {
            var spec = ParseCellSpec(args[1]);
            var result = await Cells.RunAsync(spec).ConfigureAwait(false);

            Console.Out.Write(JsonSerializer.Serialize(result, JsonOptions));

            return result.GcModeVerified ? 0 : 1;
        }

        return await RunMatrixAsync(args).ConfigureAwait(false);
    }

    private static async Task<int> RunMatrixAsync(string[] args)
    {
        var quick = args.Contains("--quick");
        var specs = Cells.Build(
            quick,
            ParseDuration(args, "--warmup"),
            ParseDuration(args, "--window"),
            ParseInt(args, "--repetitions"));

        Console.WriteLine($"Stress suite: NResilience vs Polly. {(quick ? "quick" : "full")} matrix, {specs.Count} cells.");
        Console.WriteLine(MachineFacts.Describe());
        Console.WriteLine($"Latency resolution: bucket growth {LatencyHistogram.Growth:0.##}x - percentiles closer than that are not separable.");
        Console.WriteLine();

        var started = Stopwatch.StartNew();
        var results = new List<CellResult>();
        var failedCells = 0;

        for (var i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            var label = spec.Mode == LoadMode.OpenLoop
                ? $"rate={spec.OfferedRate:N0}/s"
                : $"dop={spec.Dop}";

            Console.WriteLine(
                $"[{i + 1}/{specs.Count}] {spec.Section} {spec.Arm} {label} " +
                $"{(spec.ServerGc ? "server" : "workstation")}/{(spec.ConcurrentGc ? "concurrent" : "non-concurrent")} rep={spec.Repetition}");

            var child = await RunCellProcessAsync(spec).ConfigureAwait(false);

            if (child is null)
            {
                failedCells++;
                Console.WriteLine("  FAILED: child process produced no result.");
                continue;
            }

            results.Add(child);

            Console.WriteLine(
                $"  {child.OpsPerSecond,12:N0} ops/s   P50 {Format(child.P50),9}  P99 {Format(child.P99),9}  " +
                $"{child.BytesPerOp,8:N0} B/op   pause {child.GcPausePercent,5:N1}%   att/op {child.AttemptsPerOp,4:N2}");

            if (!child.GcModeVerified)
                Console.WriteLine($"  GC MODE MISMATCH: {child.GcModeError}");

            if (child.MissedArrivals > 0 || child.ShedArrivals > 0)
                Console.WriteLine($"  PACING SHORTFALL: {child.MissedArrivals:N0} missed, {child.ShedArrivals:N0} shed - row not quotable.");
        }

        // A cell that produced no result is a failed run, not a missing row: the report would
        // otherwise read as complete while silently missing an arm.
        if (failedCells > 0)
        {
            Console.WriteLine($"{failedCells} of {specs.Count} cells produced no result; not writing a report that would read as complete.");

            return 1;
        }

        if (results.Count == 0)
        {
            Console.WriteLine("No cells ran.");

            return 1;
        }

        StressReport.Write(results, quick, started.Elapsed);

        return results.Any(r => !r.Trustworthy) ? 1 : 0;
    }

    /// <summary>Spawns a child for one cell, with the GC mode as env overrides, and reads its JSON line.</summary>
    private static async Task<CellResult?> RunCellProcessAsync(Cells.CellSpec spec)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("--cell");
        psi.ArgumentList.Add(JsonSerializer.Serialize(new CellSpecJson(spec), JsonOptions));

        // The runtime reads these after the host resolves them but before Main runs, so the
        // child comes up in the requested GC configuration without rebuilding the binary.
        psi.Environment["DOTNET_gcServer"] = spec.ServerGc ? "1" : "0";
        psi.Environment["DOTNET_gcConcurrent"] = spec.ConcurrentGc ? "1" : "0";

        using var process = Process.Start(psi)!;

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync().ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        try
        {
            var result = JsonSerializer.Deserialize<CellResult>(stdout.Trim(), JsonOptions);

            if (result is null)
                ReportChildFailure("child produced no result.", stderr);

            return result;
        }
        catch (JsonException)
        {
            ReportChildFailure("child stdout was not the expected JSON result.", stderr);

            return null;
        }
    }

    private static void ReportChildFailure(string message, string stderr)
    {
        Console.WriteLine($"  {message}");

        var trimmed = stderr.Trim();

        if (trimmed.Length > 0)
            Console.WriteLine($"  child stderr: {trimmed[..Math.Min(1000, trimmed.Length)]}");
    }

    private static string Format(TimeSpan t) => t.TotalMilliseconds >= 1000
        ? $"{t.TotalSeconds:0.00}s"
        : t.TotalMicroseconds >= 1000
            ? $"{t.TotalMilliseconds:0.00}ms"
            : $"{t.TotalMicroseconds:0.##}us";

    private static TimeSpan? ParseDuration(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name && double.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var seconds))
                return TimeSpan.FromSeconds(seconds);
        }

        return null;
    }

    private static int? ParseInt(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var value))
                return value;
        }

        return null;
    }

    private static Cells.CellSpec ParseCellSpec(string json)
    {
        var parsed = JsonSerializer.Deserialize<CellSpecJson>(json, JsonOptions)!;

        return new Cells.CellSpec(
            parsed.Section,
            parsed.Arm,
            Enum.Parse<LoadMode>(parsed.Mode),
            parsed.Dop,
            parsed.OfferedRate,
            parsed.ServerGc,
            parsed.ConcurrentGc,
            TimeSpan.FromSeconds(parsed.WarmupSeconds),
            TimeSpan.FromSeconds(parsed.WindowSeconds),
            parsed.Repetition);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The wire shape of a cell spec: durations as seconds, because JSON has no TimeSpan.</summary>
    private sealed record CellSpecJson(
        string Section,
        string Arm,
        string Mode,
        int Dop,
        double OfferedRate,
        bool ServerGc,
        bool ConcurrentGc,
        double WarmupSeconds,
        double WindowSeconds,
        int Repetition)
    {
        public CellSpecJson()
            : this("", "", nameof(LoadMode.ClosedLoop), 0, 0, false, false, 0, 0, 1)
        {
        }

        public CellSpecJson(Cells.CellSpec spec)
            : this(
                spec.Section,
                spec.Arm,
                spec.Mode.ToString(),
                spec.Dop,
                spec.OfferedRate,
                spec.ServerGc,
                spec.ConcurrentGc,
                spec.Warmup.TotalSeconds,
                spec.Window.TotalSeconds,
                spec.Repetition)
        {
        }
    }
}
