using NResilience.Internal;
using NResilience.Testing.Internal;

namespace NResilience.Testing;

/// <summary>
///     One simulation, being built. Start with <see cref="Simulate.Policy" />; every method returns a
///     new value, so a half-built simulation can be shared between tests that vary the rest.
/// </summary>
/// <seealso cref="Simulate" />
public sealed record Simulation
{
    /// <summary>The window <see cref="SimulationReport.Amplification" /> is worst-cased over.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    internal Simulation(Resilience policy) => Policy = policy;

    /// <summary>The dependency the policy calls. Set it with <see cref="Against" />.</summary>
    public Dependency? Dependency { get; private init; }

    /// <summary>How long the run offers load for. Set it with <see cref="For" />.</summary>
    public TimeSpan Duration { get; private init; }

    /// <summary>The traffic offered. Set it with <see cref="Under" />.</summary>
    public Load? Load { get; private init; }

    /// <summary>The policy being simulated.</summary>
    public Resilience Policy { get; }

    /// <summary>The dependency the policy calls.</summary>
    /// <param name="dependency">The dependency.</param>
    /// <returns>A new simulation. The receiver is unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dependency" /> is null.</exception>
    public Simulation Against(Dependency dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);

        return this with { Dependency = dependency };
    }

    /// <summary>How long to offer load for. Calls still in flight when it elapses are allowed to finish.</summary>
    /// <param name="duration">The run length. Must be positive.</param>
    /// <returns>A new simulation. The receiver is unchanged.</returns>
    public Simulation For(TimeSpan duration) => this with { Duration = duration };

    /// <summary>The traffic to offer.</summary>
    /// <param name="load">The load.</param>
    /// <returns>A new simulation. The receiver is unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="load" /> is null.</exception>
    public Simulation Under(Load load)
    {
        ArgumentNullException.ThrowIfNull(load);

        return this with { Load = load };
    }

    /// <summary>
    ///     Runs the simulation and reports what happened. Nothing sleeps: five simulated minutes cost
    ///     whatever the arithmetic costs.
    /// </summary>
    /// <param name="seed">Fixes the random stream. The same seed produces the same report.</param>
    /// <returns>The report.</returns>
    /// <exception cref="ResilienceConfigurationException">The policy, the dependency or the load cannot be run.</exception>
    /// <exception cref="InvalidOperationException">The simulation is missing its dependency, its load or its duration.</exception>
    public SimulationReport Run(int seed)
    {
        if (Dependency is null)
            throw new InvalidOperationException("The simulation has no dependency. Call Against() before Run().");

        if (Load is null)
            throw new InvalidOperationException("The simulation has no load. Call Under() before Run().");

        if (Duration <= TimeSpan.Zero)
            throw new InvalidOperationException("The simulation has no duration. Call For() with a positive time before Run().");

        Policy.Validate();
        Dependency.Validate();
        Load.Validate();

        return new Execution(this, seed).Execute();
    }

    /// <summary>
    ///     One execution. A class rather than a pile of locals because the arrival driver, the
    ///     dependency callback and the listener all write to the same tallies, and closures over a
    ///     dozen locals read worse than fields do.
    /// </summary>
    private sealed class Execution
    {
        private readonly List<Bucket> _buckets = [];

        private readonly SimulationClock _clock = new();

        private readonly Dependency _dependency;

        private readonly ChaosDice _dice;

        private readonly TimeSpan _duration;

        private readonly int[] _kinds = new int[Enum.GetValues<CallEventKind>().Length];

        private readonly List<long> _latencies = [];

        private readonly Load _load;

        /// <summary>Whether each call succeeded, indexed the same way <see cref="_starts" /> is.</summary>
        private readonly List<bool> _ok = [];

        private readonly Resilience _policy;

        private readonly int _seed;

        /// <summary>When each call started, in start order - which is why recovery can be read off it directly.</summary>
        private readonly List<long> _starts = [];

        private readonly Func<CancellationToken, Task> _work;

        /// <summary>The first exception a call threw out of <c>TryRunAsync</c>, which would be a library bug.</summary>
        private Exception? _fault;

        /// <summary>Calls the dependency is serving right now, before the peers' share is applied.</summary>
        private int _inflight;

        private int _outstanding;

        private int _reached;

        private int _succeeded;

        internal Execution(Simulation simulation, int seed)
        {
            _policy = simulation.Policy.WithClock(_clock).WithListener(Record);
            _dependency = simulation.Dependency!;
            _load = simulation.Load!;
            _duration = simulation.Duration;
            _seed = seed;
            _dice = new ChaosDice(seed);
            _work = Serve;
        }

        internal SimulationReport Execute()
        {
            // The library draws its jitter from a thread-static stream, and the whole run happens on
            // this thread - so seeding it here is what makes a backoff curve and a break duration
            // reproducible alongside everything else.
            Rng.SeedWith(unchecked((uint)_seed));

            Drive();

            if (_fault is not null)
                throw new InvalidOperationException("A simulated call failed outside the policy.", _fault);

            return new SimulationReport(
                _seed,
                _duration,
                _starts.Count,
                _succeeded,
                _reached,
                Amplification(),
                TimeToRecover(),
                [.. _latencies],
                _kinds);
        }

        /// <summary>
        ///     The driver: advance to whichever comes first, the next arrival or the next timer, and let
        ///     the continuations run inline. Arrivals stop at the end of the run and the loop keeps going
        ///     until the calls already in flight have finished.
        /// </summary>
        private void Drive()
        {
            var end = _duration.Ticks;
            var arrival = Gap();

            while (true)
            {
                var timer = _clock.NextDue;
                var arriving = arrival <= end && (timer is null || arrival <= timer);

                if (!arriving && timer is null)
                {
                    if (_outstanding == 0 || !Settled())
                        break;

                    continue;
                }

                _clock.AdvanceTo(arriving ? arrival : timer!.Value);

                if (!arriving)
                    continue;

                Start();
                arrival += Gap();
            }
        }

        /// <summary>
        ///     Whether a timer appeared after the queue looked empty while calls were still in flight.
        ///     <para>
        ///         In a correct run this never fires: every suspension in a simulation is on the virtual
        ///         clock, so an empty timer queue means no work is pending. It exists because a
        ///         continuation that escaped to the thread pool would otherwise be silently dropped from
        ///         the report rather than showing up as a hang - and the determinism gate is what turns
        ///         that into a failing test.
        ///     </para>
        /// </summary>
        private bool Settled()
        {
            for (var spin = 0; spin < 100; spin++)
            {
                Thread.Yield();

                if (_clock.NextDue is not null)
                    return true;
            }

            return false;
        }

        /// <summary>
        ///     Ticks until the next arrival: a gap drawn uniformly from zero to twice the mean, so the
        ///     run contains the short bursts a real second of traffic contains and still offers the rate
        ///     that was asked for.
        /// </summary>
        private long Gap()
        {
            var mean = (double)TimeSpan.TicksPerSecond / _load.PerSecond;

            return (long)(2 * _dice.Next() * mean);
        }

        /// <summary>Starts one caller-level call. Runs inline until it suspends, which is the arrival's whole cost.</summary>
        private void Start()
        {
            var index = _starts.Count;

            _starts.Add(_clock.Now);
            _ok.Add(false);
            BucketAt(_clock.Now).Calls++;
            _outstanding++;

            _ = Call(index);
        }

        private async Task Call(int index)
        {
            var started = _clock.Now;

            try
            {
                var result = await _policy.TryRunAsync(_work).ConfigureAwait(false);

                if (result.IsSuccess)
                {
                    _ok[index] = true;
                    _succeeded++;
                }
            }
            catch (Exception exception)
            {
                _fault ??= exception;
            }

            _latencies.Add(_clock.Now - started);
            _outstanding--;
        }

        /// <summary>One attempt against the dependency. This is the callback the real executor drives.</summary>
        private async Task Serve(CancellationToken cancellationToken)
        {
            _reached++;
            BucketAt(_clock.Now).Reached++;
            _inflight++;

            try
            {
                var (latency, fails) = _dependency.Serve(TimeSpan.FromTicks(_clock.Now), _inflight * _load.Peers, _dice);

                if (latency > TimeSpan.Zero && !await _clock.Sleep(latency, cancellationToken).ConfigureAwait(false))
                    cancellationToken.ThrowIfCancellationRequested();

                if (fails)
                    throw new IOException("The simulated dependency failed.");
            }
            finally
            {
                _inflight--;
            }
        }

        private Bucket BucketAt(long ticks)
        {
            var index = (int)(ticks / Window.Ticks);

            while (_buckets.Count <= index)
                _buckets.Add(new Bucket());

            return _buckets[index];
        }

        /// <summary>The worst second, counting only seconds the caller actually made a call in.</summary>
        private double Amplification()
        {
            var worst = 0.0;

            foreach (var bucket in _buckets)
            {
                if (bucket.Calls == 0)
                    continue;

                var ratio = (double)bucket.Reached / bucket.Calls;

                if (ratio > worst)
                    worst = ratio;
            }

            return worst;
        }

        private void Record(CallEvent callEvent) => _kinds[(int)callEvent.Kind]++;

        /// <summary>
        ///     How long after the last impairment ended before a full second of calls all succeeded.
        ///     Scanned over a running count of failures, so the answer has the precision of a call rather
        ///     than of a second.
        /// </summary>
        private TimeSpan? TimeToRecover()
        {
            if (_dependency.ImpairedUntil is not { } impaired)
                return null;

            var failures = new int[_starts.Count + 1];

            for (var i = 0; i < _starts.Count; i++)
                failures[i + 1] = failures[i] + (_ok[i] ? 0 : 1);

            var last = 0;

            for (var i = 0; i < _starts.Count; i++)
            {
                if (_starts[i] < impaired.Ticks)
                    continue;

                var closes = _starts[i] + Window.Ticks;

                if (closes > _duration.Ticks)
                    return null;

                if (last < i)
                    last = i;

                while (last < _starts.Count && _starts[last] < closes)
                    last++;

                if (failures[last] - failures[i] == 0)
                    return TimeSpan.FromTicks(_starts[i] - impaired.Ticks);
            }

            return null;
        }

        /// <summary>One second of the run, from the caller's side and from the dependency's.</summary>
        private sealed class Bucket
        {
            internal int Calls;

            internal int Reached;
        }
    }
}
