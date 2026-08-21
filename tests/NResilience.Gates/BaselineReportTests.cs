using System.Globalization;
using System.Text;
using NResilience.Probes;
using Xunit;

namespace NResilience.Gates;

/// <summary>
/// Not a gate. This prints the full measurement table so the numbers in
/// plans/phase-0a-results.md are produced by a command anyone can re-run, rather than
/// transcribed from a session that no longer exists.
///
/// Run it with:
///   dotnet test tests/NResilience.Gates -f net10.0 --filter FullyQualifiedName~BaselineReport -l "console;verbosity=detailed"
/// </summary>
[Collection(BaselineCollection.Name)]
public sealed class BaselineReportTests
{
    private readonly ITestOutputHelper _output;

    public BaselineReportTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Report()
    {
        var report = new StringBuilder();
        report.Append(CultureInfo.InvariantCulture, $"runtime            : {Environment.Version}");
        report.AppendLine();
        report.Append(CultureInfo.InvariantCulture, $"framework          : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        report.AppendLine();
        report.Append(CultureInfo.InvariantCulture, $"architecture       : {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        report.AppendLine();
        report.Append(CultureInfo.InvariantCulture, $"server GC          : {System.Runtime.GCSettings.IsServerGC}");
        report.AppendLine();
        report.Append(CultureInfo.InvariantCulture, $"latency mode       : {System.Runtime.GCSettings.LatencyMode}");
        report.AppendLine();

        await AppendAsync(report, "SUSPENDING PATH (process-wide counter)", Arms.Suspending());
        await AppendAsync(report, "SYNCHRONOUS FAST PATH (thread-local counter)", Arms.SyncCompleting());
        await AppendAsync(report, "CANCELLATION PRIMITIVES (thread-local counter)", Arms.Cancellation());

        report.AppendLine();
        report.AppendLine("TIMEPROVIDER / CTS BEHAVIOUR");
        report.Append(CultureInfo.InvariantCulture, $"  TryReset, system provider      : {CtsFacts.TryResetWithSystemProvider()}");
        report.AppendLine();
        report.Append(CultureInfo.InvariantCulture, $"  TryReset, custom provider      : {CtsFacts.TryResetWithCustomProvider(new Microsoft.Extensions.Time.Testing.FakeTimeProvider())}");
        report.AppendLine();
        report.Append(CultureInfo.InvariantCulture, $"  TryReset, after cancellation   : {CtsFacts.TryResetAfterCancellation()}");
        report.AppendLine();

        _output.WriteLine(report.ToString());
    }

    private static async Task AppendAsync(StringBuilder report, string title, IReadOnlyList<Arm> arms)
    {
        Dictionary<string, AllocationMeasurement> results = await Arms.MeasureAsync(arms);

        report.AppendLine();
        report.AppendLine(title);
        foreach (Arm arm in arms)
        {
            AllocationMeasurement m = results[arm.Name];
            report.Append(CultureInfo.InvariantCulture, $"  {arm.Name,-36} {m.BytesPerOperation,9:0.0} B/op");
            report.AppendLine();
        }
    }
}
