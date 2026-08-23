using NBenchmark;
using NBenchmark.Reporters;

namespace NResilience.Baseline;

/// <summary>
/// Trend tracking, published rather than gated.
///
/// The hard gate lives in tests/NResilience.Gates and depends on no benchmark harness at all -
/// it is xunit over allocation counters, which is deterministic and fails with a byte count.
/// This project exists for the latency picture and for run-to-run trends, which are worth
/// watching and not worth failing a build over: shared CI runners are noisy enough that a
/// latency gate is either loose enough to catch nothing or tight enough to flake weekly.
///
///   dotnet run -c Release -f net10.0 --project bench/NResilience.Baseline
///   dotnet run -c Release -f net10.0 --project bench/NResilience.Baseline -- --category socket
///   dotnet run -c Release -f net10.0 --project bench/NResilience.Baseline -- --reporter json --output baseline.json
/// </summary>
internal static class Program
{
    private const string OutputDirectory = "artifacts";

    private static async Task<int> Main(string[] args)
    {
        IReadOnlyList<BenchmarkResult> results = await BenchmarkHarness.Create(args)
            .AddFromAssembly<SuspendingPathBenchmarks>()
            // Reports land under artifacts/, which .gitignore already covers. The default writes
            // timestamped files into the working directory, and a benchmark run should not leave
            // anything behind in the repository.
            .WithReporter(new MarkdownReporter(OutputDirectory, "baseline", ReportDetail.Advanced))
            .WithDiagnostics(DiagnosticsMode.GcAndCpu)
            .RunAsync(CancellationToken.None)
            .ConfigureAwait(false);

        return results.Any(r => r.Errored) ? 1 : 0;
    }
}
