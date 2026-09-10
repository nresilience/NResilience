using System.Text;
using System.Text.Json;

namespace NResilience.Stress;

/// <summary>
///     The published report: one Markdown file and one JSON file under <c>artifacts/stress/</c>,
///     timestamped. The JSON holds every repetition of every cell, so a reader can recompute
///     anything the Markdown asserts; the Markdown holds the median of each cell's repetitions
///     with the observed spread beside it, because a median without a spread invites a reader to
///     believe a difference the run cannot actually resolve.
/// </summary>
internal static class StressReport
{
    public static void Write(List<CellResult> results, bool quick, TimeSpan elapsed)
    {
        Directory.CreateDirectory("artifacts/stress");

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var markdownPath = Path.Combine("artifacts", "stress", $"stress-{stamp}.md");
        var jsonPath = Path.Combine("artifacts", "stress", $"stress-{stamp}.json");

        using (var json = File.Create(jsonPath))
        {
            using var writer = new Utf8JsonWriter(json, new JsonWriterOptions { Indented = true });
            writer.WriteStartArray();

            foreach (var result in results)
                result.WriteJson(writer);

            writer.WriteEndArray();
        }

        // From the results rather than the quick flag: the window and warmup are
        // --window/--warmup overridable, and a report that misstates its own method is worse
        // than no report. Every cell of a run shares one configuration by construction.
        var warmup = results.Max(r => r.WarmupSeconds);
        var window = results.Max(r => r.WindowSeconds);
        var repetitions = results.Count == 0 ? 0 : results.Max(r => r.Repetition);

        var cells = Aggregate(results);
        var sb = new StringBuilder();

        sb.AppendLine("# Stress suite: NResilience vs Polly");
        sb.AppendLine();
        sb.AppendLine($"- Runtime: {MachineFacts.Describe()}");
        sb.AppendLine($"- Matrix: {(quick ? "quick" : "full")} - {cells.Count} cells x {repetitions} repetitions = {results.Count} runs, fresh process per run, shuffled order");
        sb.AppendLine($"- Method: a {window:0}-second steady window after a {warmup:0}-second warmup, full GC between them");
        sb.AppendLine($"- Wall clock: {elapsed.TotalMinutes:0.0} minutes");
        sb.AppendLine($"- Latency resolution: histogram buckets grow {LatencyHistogram.Growth:0.##}x, so percentiles closer than that are not separable");
        sb.AppendLine("- Every figure is the median of the repetitions; `+-` is half the observed range as a percentage of the median");
        sb.AppendLine();

        var untrustworthy = cells.Where(c => !c.Trustworthy).ToList();

        if (untrustworthy.Count > 0)
        {
            sb.AppendLine("> [!WARNING]");
            sb.AppendLine("> These cells did not measure what they set out to measure and must not be quoted:");

            foreach (var cell in untrustworthy)
                sb.AppendLine($"> - `{cell.Arm}` {cell.Load}: {cell.Untrustworthy}");

            sb.AppendLine();
        }

        WriteExecutor(sb, cells);
        WriteLatency(sb, cells);
        WriteBudget(sb, cells);
        WriteHttp(sb, cells);

        File.WriteAllText(markdownPath, sb.ToString());

        Console.WriteLine();
        Console.WriteLine($"Markdown: {markdownPath}");
        Console.WriteLine($"JSON:     {jsonPath}");
    }

    // ---- Sections. ----

    private static void WriteExecutor(StringBuilder sb, List<Cell> cells)
    {
        var rows = cells.Where(c => c.Kind == "executor").ToList();

        if (rows.Count == 0)
            return;

        sb.AppendLine("## Executor level: closed loop (matched A/B)");
        sb.AppendLine();
        sb.AppendLine("Fixed concurrency, so these are throughput and allocation figures. The latency columns are");
        sb.AppendLine("here for completeness only: in a closed loop at fixed DOP, latency is `DOP / throughput` by");
        sb.AppendLine("Little's Law and carries no information the ops/s column does not. Latency claims belong to");
        sb.AppendLine("the open-loop section below.");
        sb.AppendLine();
        sb.AppendLine("| Arm | DOP | GC | ops/s | +- | B/op | +- | Alloc/s | Coll/s | Pause % | Pause each | Pool peak | Queue peak | Att/op |");
        sb.AppendLine("|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");

        foreach (var row in rows.OrderBy(r => r.Dop).ThenBy(r => r.ServerGc).ThenBy(r => ArmOrder(r.Arm)).ThenBy(r => r.Arm))
        {
            sb.AppendLine(
                $"| {row.Arm} | {row.Dop} | {row.Gc} " +
                $"| {row.OpsPerSecond:N0} | {row.OpsSpread:P0} " +
                $"| {row.BytesPerOp:N0} | {row.BytesSpread:P0} " +
                $"| {row.AllocatedBytesPerSecond / 1e9:N2} GB | {row.CollectionsPerSecond:N0} " +
                $"| {row.GcPausePercent:N1} | {Format(row.GcMeanPause)} " +
                $"| {row.PoolThreadPeak} | {row.PoolQueuePeak:N0} | {(row.AttemptsPerOp > 0 ? row.AttemptsPerOp.ToString("N2") : "-")} |");
        }

        sb.AppendLine();
        sb.AppendLine("> [!NOTE]");
        sb.AppendLine("> Matched pairs: `raw` is the callback with no library at all; `lib-passthrough` vs `polly-empty`");
        sb.AppendLine("> is bound-for-bound (neither can retry, neither has a time bound); `lib-trivial` vs");
        sb.AppendLine("> `polly-retry-only` pairs two three-attempt retry shapes with no time bounds; `lib-default` vs");
        sb.AppendLine("> `polly-retry-timeout` pairs each library's default machinery; the `-cancellable` pair adds a");
        sb.AppendLine("> caller token that can be cancelled and never is; `lib-retry-twice` vs `polly-retry-twice`");
        sb.AppendLine("> makes three attempts per op with zero delay.");
        sb.AppendLine("> ");
        sb.AppendLine("> `Alloc/s` is bytes allocated per second - the quantity collection frequency actually tracks,");
        sb.AppendLine("> and not the same ranking as B/op once the arms differ in throughput. `Pause each` is the");
        sb.AppendLine("> wall-clock cost of one collection, which is where survivorship shows up: two arms can");
        sb.AppendLine("> allocate at the same rate and still not pay the same for it.");
        sb.AppendLine("> ");
        sb.AppendLine("> `Att/op` is counted only in the arms whose callback can fail, because counting it in the");
        sb.AppendLine("> others would mean wrapping the shared gate and charging those arms for the wrapper. It reads");
        sb.AppendLine("> `-` elsewhere; every such arm makes exactly one attempt per op, which its zero failure count");
        sb.AppendLine("> already establishes.");
        sb.AppendLine();
    }

    private static void WriteLatency(StringBuilder sb, List<Cell> cells)
    {
        var rows = cells.Where(c => c.Kind == "latency").ToList();

        if (rows.Count == 0)
            return;

        sb.AppendLine("## Latency: open loop at a fixed arrival rate");
        sb.AppendLine();
        sb.AppendLine("Ops are launched on a schedule and never wait for their predecessors, so a slower arm does not");
        sb.AppendLine("get a lighter load. Latency is measured from each arrival's *due* time, so queueing counts. Both");
        sb.AppendLine("arms of a pair are offered the identical absolute rate, which is what makes the comparison one");
        sb.AppendLine("of the arms rather than of their throughputs.");
        sb.AppendLine();
        sb.AppendLine("| Arm | Offered/s | Achieved/s | P50 | P50 range | P90 | P90 range | P99 | P99 range | Lag P99 | Missed |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");

        foreach (var row in rows.OrderBy(r => r.OfferedRate).ThenBy(r => ArmOrder(r.Arm)).ThenBy(r => r.Arm))
        {
            sb.AppendLine(
                $"| {row.Arm} | {row.OfferedRate:N0} | {row.OpsPerSecond:N0} " +
                $"| {Format(row.P50)} | {row.P50Range} " +
                $"| {Format(row.P90)} | {row.P90Range} " +
                $"| {Format(row.P99)} | {row.P99Range} " +
                $"| {Format(row.ArrivalLagP99)} | {row.ArrivalShortfall:P2} |");
        }

        sb.AppendLine();
        sb.AppendLine("> [!NOTE]");
        sb.AppendLine("> `Lag P99` is the pacer's self-report: the 99th percentile of the gap between an arrival's due");
        sb.AppendLine("> time and its launch. A lag comparable to the latency being measured means the row describes");
        sb.AppendLine("> the driver rather than the arm, so it is the first column to read. Any row that missed or shed");
        sb.AppendLine("> arrivals is listed as untrustworthy at the top of this report and is not quotable at all.");
        sb.AppendLine("> ");
        sb.AppendLine("> The `range` columns are the lowest and highest value the repetitions produced, and they are");
        sb.AppendLine("> the point of this table as much as the medians are. Read them before reading any percentile:");
        sb.AppendLine("> where a range spans orders of magnitude, the figure beside it is noise from this machine and");
        sb.AppendLine("> not a property of the arm, and no comparison may be drawn from it.");
        sb.AppendLine("> ");
        sb.AppendLine("> Per-op allocation is not reported here: the open-loop driver allocates a continuation per");
        sb.AppendLine("> arrival, so B/op in this mode includes the driver. Allocation figures come from the closed-loop");
        sb.AppendLine("> section, where the driver allocates nothing per op.");
        sb.AppendLine();
    }

    private static void WriteBudget(StringBuilder sb, List<Cell> cells)
    {
        var rows = cells.Where(c => c.Kind == "budget").ToList();

        if (rows.Count == 0)
            return;

        sb.AppendLine("## Retry budget under sustained failure: open loop (behavioral, not a matched A/B)");
        sb.AppendLine();
        sb.AppendLine("Polly has no retry budget, so this contrasts a client that caps its retries against one that");
        sb.AppendLine("does not. Open loop, and it has to be: a refused call pauses before it reports, so under a");
        sb.AppendLine("closed loop the budgeted arm's own pause would reduce the load it offers and every column would");
        sb.AppendLine("measure the pause instead of the budget.");
        sb.AppendLine();
        sb.AppendLine("**Retries per op is the column that matters.** It is the amplification a dependency feels, and it");
        sb.AppendLine("does not move with how fast the client happens to be running.");
        sb.AppendLine();
        sb.AppendLine("| Arm | Offered/s | Achieved/s | Att/op | Retries/op | Retries/s | Rejected/s | Succ/s | Fail/s | P99 | Missed |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");

        foreach (var row in rows.OrderBy(r => r.OfferedRate).ThenBy(r => r.Arm))
        {
            sb.AppendLine(
                $"| {row.Arm} | {row.OfferedRate:N0} | {row.OpsPerSecond:N0} " +
                $"| {row.AttemptsPerOp:N2} | {row.RetriesPerOp:N2} | {row.RetriesPerSecond:N0} " +
                $"| {row.RejectionsPerSecond:N0} | {row.SuccessesPerSecond:N0} | {row.FailuresPerSecond:N0} | {Format(row.P99)} | {row.ArrivalShortfall:P2} |");
        }

        sb.AppendLine();
        sb.AppendLine("> [!NOTE]");
        sb.AppendLine("> The `-harsh` arms face a dependency failing half of what it is sent; the others a quarter. The");
        sb.AppendLine("> pattern is counted across the whole cell, so it is a property of the dependency and both arms");
        sb.AppendLine("> face the same one. Every refusal raises `CallRejectedException` after a pause, which is");
        sb.AppendLine("> deliberate - a free refusal invites a caller to spin - and which is why the budgeted arm's");
        sb.AppendLine("> latency percentiles are dominated by that pause rather than by any executor cost.");
        sb.AppendLine();
    }

    private static void WriteHttp(StringBuilder sb, List<Cell> cells)
    {
        var rows = cells.Where(c => c.Kind == "http").ToList();

        if (rows.Count == 0)
            return;

        sb.AppendLine("## HTTP level: closed loop, out-of-process Kestrel, matched policies");
        sb.AppendLine();
        sb.AppendLine("The dependency runs in its own process, so the allocation and the collections in these rows are");
        sb.AppendLine("the client's alone. Every arm runs over an identically configured transport, so the resilience");
        sb.AppendLine("handler is the only difference between them.");
        sb.AppendLine();
        sb.AppendLine("| Arm | DOP | GC | ops/s | +- | B/op | +- | B/request | Handler B/op | Att/op | Fail % | Pause % |");
        sb.AppendLine("|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");

        foreach (var group in rows.GroupBy(r => (r.Dop, r.ServerGc)).OrderBy(g => g.Key.Dop).ThenBy(g => g.Key.ServerGc))
        {
            // The normalization baseline is the plain client against the *same* endpoint at
            // exactly one request per op, so its B/op is bytes per request. Subtracting
            // att/op x that from a retrying arm leaves the handler's own cost, instead of
            // charging the handler for the extra requests retrying necessarily sends.
            var baseline = group.FirstOrDefault(r => r.Arm == "http-raw-flaky");

            foreach (var row in group.OrderBy(r => ArmOrder(r.Arm)).ThenBy(r => r.Arm))
            {
                var perRequest = row.ServerAttemptsPerOp > 0 ? row.BytesPerOp / row.ServerAttemptsPerOp : 0;
                var handler = baseline is null || row.Arm.StartsWith("http-raw", StringComparison.Ordinal)
                    ? (double?)null
                    : row.BytesPerOp - (row.ServerAttemptsPerOp * baseline.BytesPerOp);

                sb.AppendLine(
                    $"| {row.Arm} | {row.Dop} | {row.Gc} " +
                    $"| {row.OpsPerSecond:N0} | {row.OpsSpread:P0} " +
                    $"| {row.BytesPerOp:N0} | {row.BytesSpread:P0} " +
                    $"| {perRequest:N0} | {(handler is null ? "-" : handler.Value.ToString("N0"))} " +
                    $"| {row.ServerAttemptsPerOp:N3} | {row.FailurePercent:N1} | {row.GcPausePercent:N1} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("> [!NOTE]");
        sb.AppendLine("> `Att/op` is observed at the server, not inferred: every request that lands is counted, and the");
        sb.AppendLine("> 200 and 503 answers carry the same body so a request costs the same whichever it gets.");
        sb.AppendLine("> ");
        sb.AppendLine("> `B/request` is B/op divided by observed Att/op. `Handler B/op` subtracts the transport a row");
        sb.AppendLine("> actually used - `Att/op x http-raw-flaky's B/op` - from its B/op, leaving the resilience");
        sb.AppendLine("> handler's own cost. `http-raw-flaky` is the right baseline because it faces the same endpoint");
        sb.AppendLine("> and the same status mix at exactly one request per op; `http-raw` against the always-200");
        sb.AppendLine("> endpoint is here only as a no-failure reference.");
        sb.AppendLine("> ");
        sb.AppendLine("> `http-lib-minimal` is the lib handler with the on-by-default extras off (stall bound,");
        sb.AppendLine("> rate-limit header reads, nested-retry stamping) - the B/op gap between it and `http-lib`");
        sb.AppendLine("> prices that feature set as a package.");
        sb.AppendLine();
    }

    // ---- Aggregation across repetitions. ----

    /// <summary>
    ///     One cell's repetitions reduced to a median and a spread. The median rather than the
    ///     mean because a single thermal excursion or a stray background process moves a mean and
    ///     not a median; the spread published beside it because the reader needs to know which
    ///     differences the run can actually resolve.
    /// </summary>
    private sealed record Cell
    {
        public required string Kind { get; init; }

        public required string Arm { get; init; }

        public required int Dop { get; init; }

        public required double OfferedRate { get; init; }

        public required bool ServerGc { get; init; }

        public required string Gc { get; init; }

        public required string Load { get; init; }

        public required int Runs { get; init; }

        public required double OpsPerSecond { get; init; }

        public required double OpsSpread { get; init; }

        public required double BytesPerOp { get; init; }

        public required double BytesSpread { get; init; }

        public required double AllocatedBytesPerSecond { get; init; }

        public required double CollectionsPerSecond { get; init; }

        public required double GcPausePercent { get; init; }

        public required TimeSpan GcMeanPause { get; init; }

        public required int PoolThreadPeak { get; init; }

        public required long PoolQueuePeak { get; init; }

        public required TimeSpan P50 { get; init; }

        public required TimeSpan P90 { get; init; }

        public required TimeSpan P99 { get; init; }

        public required TimeSpan Max { get; init; }

        public required TimeSpan ArrivalLagP99 { get; init; }

        public required string P50Range { get; init; }

        public required string P90Range { get; init; }

        public required string P99Range { get; init; }

        public required double ArrivalShortfall { get; init; }

        public required double AttemptsPerOp { get; init; }

        public required double RetriesPerOp { get; init; }

        public required double RetriesPerSecond { get; init; }

        public required double RejectionsPerSecond { get; init; }

        public required double SuccessesPerSecond { get; init; }

        public required double FailuresPerSecond { get; init; }

        public required double FailurePercent { get; init; }

        public required double ServerAttemptsPerOp { get; init; }

        public required bool Trustworthy { get; init; }

        public required string Untrustworthy { get; init; }
    }

    private static List<Cell> Aggregate(List<CellResult> results) =>
    [
        .. results
            .GroupBy(r => (r.Kind, r.Arm, r.Dop, r.OfferedRate, r.ServerGc, r.ConcurrentGc))
            .Select(g =>
            {
                var runs = g.ToList();
                var first = runs[0];
                var seconds = first.WindowSeconds;

                return new Cell
                {
                    Kind = first.Kind,
                    Arm = first.Arm,
                    Dop = first.Dop,
                    OfferedRate = first.OfferedRate,
                    ServerGc = first.ServerGc,
                    Gc = $"{(first.ServerGc ? "server" : "work")}/{(first.ConcurrentGc ? "conc" : "nonc")}",
                    Load = first.Mode == LoadMode.OpenLoop ? $"rate={first.OfferedRate:N0}/s" : $"dop={first.Dop}",
                    Runs = runs.Count,
                    OpsPerSecond = Median(runs, r => r.OpsPerSecond),
                    OpsSpread = Spread(runs, r => r.OpsPerSecond),
                    BytesPerOp = Median(runs, r => r.BytesPerOp),
                    BytesSpread = Spread(runs, r => r.BytesPerOp),
                    AllocatedBytesPerSecond = Median(runs, r => r.AllocatedBytesPerSecond),
                    CollectionsPerSecond = Median(runs, r => r.Gen0PerSecond),
                    GcPausePercent = Median(runs, r => r.GcPausePercent),
                    GcMeanPause = MedianTime(runs, r => r.GcMeanPause),
                    PoolThreadPeak = (int)Median(runs, r => r.PoolThreadPeak),
                    PoolQueuePeak = (long)Median(runs, r => r.PoolQueuePeak),
                    P50 = MedianTime(runs, r => r.P50),
                    P90 = MedianTime(runs, r => r.P90),
                    P99 = MedianTime(runs, r => r.P99),
                    Max = MedianTime(runs, r => r.Max),
                    ArrivalLagP99 = MedianTime(runs, r => r.ArrivalLagP99),
                    P50Range = Range(runs, r => r.P50),
                    P90Range = Range(runs, r => r.P90),
                    P99Range = Range(runs, r => r.P99),
                    ArrivalShortfall = runs.Max(r => r.ArrivalShortfall),
                    AttemptsPerOp = Median(runs, r => r.AttemptsPerOp),
                    RetriesPerOp = Median(runs, r => r.RetriesPerOp),
                    RetriesPerSecond = Median(runs, r => r.Retries / seconds),
                    RejectionsPerSecond = Median(runs, r => r.BudgetRejections / seconds),
                    SuccessesPerSecond = Median(runs, r => r.Successes / seconds),
                    FailuresPerSecond = Median(runs, r => r.Failures / seconds),
                    FailurePercent = Median(runs, r => r.Ops == 0 ? 0 : r.Failures * 100.0 / r.Ops),
                    ServerAttemptsPerOp = Median(runs, r => r.ServerAttemptsPerOp),
                    Trustworthy = runs.All(r => r.Trustworthy),
                    Untrustworthy = Describe(runs),
                };
            })
    ];

    private static string Describe(List<CellResult> runs)
    {
        var reasons = new List<string>();

        if (runs.Any(r => !r.GcModeVerified))
            reasons.Add(runs.First(r => !r.GcModeVerified).GcModeError ?? "GC mode mismatch");

        var missed = runs.Sum(r => r.MissedArrivals);
        var shed = runs.Sum(r => r.ShedArrivals);

        if (missed > 0 && runs.Any(r => r.ArrivalShortfall > 0.001))
            reasons.Add($"{missed:N0} arrivals missed ({runs.Max(r => r.ArrivalShortfall):P2} of those due) - the pacer could not hold the schedule");

        if (shed > 0)
            reasons.Add($"{shed:N0} arrivals shed - the outstanding cap was reached");

        return string.Join("; ", reasons);
    }

    private static double Median(List<CellResult> runs, Func<CellResult, double> select)
    {
        var values = runs.Select(select).OrderBy(v => v).ToArray();

        return values.Length == 0
            ? 0
            : values.Length % 2 == 1
                ? values[values.Length / 2]
                : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;
    }

    private static TimeSpan MedianTime(List<CellResult> runs, Func<CellResult, TimeSpan> select) =>
        TimeSpan.FromTicks((long)Median(runs, r => select(r).Ticks));

    /// <summary>
    ///     The lowest and highest value the repetitions produced. Printed rather than a spread
    ///     percentage for the latency percentiles, because those vary by orders of magnitude and
    ///     a percentage of the median would understate what the reader needs to see.
    /// </summary>
    private static string Range(List<CellResult> runs, Func<CellResult, TimeSpan> select)
    {
        if (runs.Count < 2)
            return "-";

        var values = runs.Select(select).ToArray();

        return $"{Format(values.Min())} - {Format(values.Max())}";
    }

    /// <summary>
    ///     Half the observed range as a fraction of the median: the "+-" a reader needs to decide
    ///     whether two rows differ. Half the range rather than a standard deviation because three
    ///     repetitions do not support a standard deviation, and the range is what was actually seen.
    /// </summary>
    private static double Spread(List<CellResult> runs, Func<CellResult, double> select)
    {
        var values = runs.Select(select).ToArray();

        if (values.Length < 2)
            return 0;

        var median = Median(runs, select);

        return median == 0 ? 0 : (values.Max() - values.Min()) / 2 / median;
    }

    /// <summary>Keeps a pair's two arms adjacent in the printed tables, whatever order they ran in.</summary>
    private static int ArmOrder(string arm) => arm switch
    {
        "raw" => 0,
        "http-raw" => 0,
        "http-raw-flaky" => 1,
        "lib-passthrough" => 10,
        "polly-empty" => 11,
        "lib-trivial" => 20,
        "polly-retry-only" => 21,
        "lib-default" => 30,
        "polly-retry-timeout" => 31,
        "lib-default-cancellable" => 40,
        "polly-retry-timeout-cancellable" => 41,
        "lib-retry-twice" => 50,
        "polly-retry-twice" => 51,
        "http-lib-minimal" => 60,
        "http-polly" => 61,
        "http-lib" => 62,
        _ => 99,
    };

    private static string Format(TimeSpan t) => t.TotalMilliseconds >= 1000
        ? $"{t.TotalSeconds:0.00} s"
        : t.TotalMicroseconds >= 1000
            ? $"{t.TotalMilliseconds:0.##} ms"
            : $"{t.TotalMicroseconds:0.##} us";
}
