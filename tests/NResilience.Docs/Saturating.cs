using NResilience.Testing;

namespace NResilience.Docs;

/// <summary>
///     Local saturation: <c>Resilience.Saturation</c>, which stops a policy learning from a duration
///     that is mostly this process's own thread-pool queue.
/// </summary>
public sealed class Saturating
{
    [Fact]
    public void A_policy_can_stop_measuring_while_the_local_pool_queues()
    {
        // <snippet:saturation-configure>
        var api = Resilience.Http with
        {
            AttemptCeiling = AttemptCeiling.Above(multiple: 3),

            // Five times this process's own normal queue delay. Above that, the attempt ceiling, the
            // measured backoff base and the hedge threshold stop being fed - they hold what they last
            // learned until the queue drains. Nothing is refused and no bound moves.
            Saturation = Saturation.Above(multiple: 5),
        };

        // </snippet:saturation-configure>

        Assert.Equal(expected: Saturation.Above(multiple: 5), actual: api.Saturation);

        api.Validate();
    }

    /// <summary>
    ///     The two ways to see it: the live reading, and the event raised at the onset of an episode.
    /// </summary>
    [Fact]
    public async Task The_queue_delay_can_be_read_and_listened_for()
    {
        var recorder = new EventRecorder();

        // <snippet:saturation-read>
        var api = TestPolicy.Instant with
        {
            AttemptCeiling = AttemptCeiling.Above(multiple: 3),
            Saturation = Saturation.Above(multiple: 5),
            OnEvent = e =>
            {
                if (e.Kind == CallEventKind.SaturationDetected)
                {
                    // Once per episode, not once per call, so this counts local incidents. e.Delay is
                    // the queue delay that was measured.
                    Console.WriteLine(value: $"pool queueing for {e.Delay?.TotalMilliseconds} ms");
                }
            },
        };

        // The live number, for a dashboard. Null until the process-wide probe has a baseline, which
        // takes a few seconds - the probe samples four times a second at most.
        var queueDelay = api.Measured.QueueDelay;

        // </snippet:saturation-read>

        await (api with { OnEvent = recorder.Record }).RunAsync(static _ => Task.FromResult(result: 1));

        // A healthy pool raises nothing, which is what makes the event worth watching. The reading is
        // null here rather than zero, because the probe is cold in a process that has just started.
        Assert.Equal(expected: 0, actual: recorder.CountOf(kind: CallEventKind.SaturationDetected));
        Assert.True(condition: queueDelay is null || queueDelay >= TimeSpan.Zero);
    }
}
