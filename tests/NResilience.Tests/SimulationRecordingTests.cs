using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     The recording a simulation makes: every event it raised, the virtual time it was raised at,
///     and which build of the engine raised it.
///     <para>
///         The counts a report carries say what a run cost; the timeline says how it got there. Both
///         claims are only worth anything if recording one changes neither the other numbers nor their
///         reproducibility, which is what most of this file checks.
///     </para>
/// </summary>
public sealed class SimulationRecordingTests
{
    private static readonly Dependency Fast =
        Dependency.Healthy(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200));

    private static readonly Resilience Api = TestPolicy.Instant with
    {
        Attempts = 3,
        Deadline = TimeSpan.FromSeconds(10),
        AttemptTimeout = TimeSpan.FromSeconds(2),
        Backoff = Backoff.Exponential(TimeSpan.FromMilliseconds(50)),
    };

    private static Simulation Run(Dependency dependency, int seconds = 30) =>
        Simulate.Policy(Api)
            .Against(dependency)
            .Under(Load.Constant(perSecond: 200))
            .For(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void A_run_records_nothing_unless_it_is_asked_to()
    {
        var report = Run(Fast).Run(seed: 42);

        Assert.Null(report.Timeline);
    }

    [Fact]
    public void The_same_seed_produces_a_byte_identical_timeline()
    {
        var dependency = Fast.Brownout(TimeSpan.FromSeconds(10), slower: 8, TimeSpan.FromSeconds(10)).Failing(0.05);

        var first = Run(dependency).Recording().Run(seed: 42);
        var second = Run(dependency).Recording().Run(seed: 42);

        Assert.NotNull(first.Timeline);
        Assert.NotNull(second.Timeline);

        Assert.Equal(
            string.Join('\n', first.Timeline.Select(entry => entry.ToString())),
            string.Join('\n', second.Timeline.Select(entry => entry.ToString())));
    }

    [Fact]
    public void Recording_changes_nothing_about_what_the_run_measured()
    {
        var dependency = Fast.Brownout(TimeSpan.FromSeconds(10), slower: 8, TimeSpan.FromSeconds(10)).Failing(0.05);

        var quiet = Run(dependency).Run(seed: 42);
        var recorded = Run(dependency).Recording().Run(seed: 42);

        // The whole report, which is every number it carries - so this is the assertion that the
        // listener the recording hangs off does not perturb the run it is watching.
        Assert.Equal(quiet.ToString(), recorded.ToString());
    }

    [Fact]
    public void The_timeline_holds_every_event_the_counts_hold()
    {
        var report = Run(Fast.Failing(0.2)).Recording().Run(seed: 11);

        Assert.NotNull(report.Timeline);

        foreach (var kind in Enum.GetValues<CallEventKind>())
        {
            Assert.Equal(report.CountOf(kind), report.Timeline.Count(entry => entry.Event.Kind == kind));
        }
    }

    [Fact]
    public void The_timeline_runs_forwards_and_stays_inside_the_run()
    {
        var report = Run(Fast.Failing(0.2)).Recording().Run(seed: 11);

        Assert.NotNull(report.Timeline);
        Assert.NotEmpty(report.Timeline);

        var last = TimeSpan.Zero;

        foreach (var entry in report.Timeline)
        {
            Assert.True(entry.At >= last, $"an event at {entry.At} followed one at {last}");
            Assert.True(entry.At >= TimeSpan.Zero);

            last = entry.At;
        }

        // Calls in flight when the run stops offering load are allowed to finish, so the last event
        // lands after the duration rather than on it - but not by more than one call's worth.
        Assert.True(last >= TimeSpan.FromSeconds(20), $"a 30-second run's last event was at {last}");
    }

    [Fact]
    public void A_recorded_event_carries_what_the_listener_would_have_seen()
    {
        var report = Run(Fast.Failing(0.5)).Recording().Run(seed: 5);

        Assert.NotNull(report.Timeline);

        var retry = report.Timeline.First(entry => entry.Event.Kind == CallEventKind.Retrying);

        Assert.Equal(CallEventKind.Retrying, retry.Event.Kind);
        Assert.NotNull(retry.Event.Delay);
        Assert.Contains(retry.At.ToString("hh\\:mm\\:ss\\.fffffff"), retry.ToString(), StringComparison.Ordinal);
        Assert.Contains(retry.Event.ToString(), retry.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_report_says_which_engine_produced_it()
    {
        var report = Run(Fast).Run(seed: 42);

        Assert.False(string.IsNullOrWhiteSpace(report.EngineVersion));

        // Build metadata would make a local build and the CI build of the same commit disagree about
        // whether their reports are comparable.
        Assert.DoesNotContain("+", report.EngineVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void The_engine_version_stays_out_of_the_printed_report()
    {
        var report = Run(Fast).Run(seed: 42);

        // The block of text a determinism test pins, and the one the docs publish, must not change
        // every release - so the stamp is a property and nothing else.
        Assert.DoesNotContain(report.EngineVersion, report.ToString(), StringComparison.Ordinal);
    }
}
