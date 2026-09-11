using System.Threading.RateLimiting;
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

    /// <summary>Whether the run records a timeline. Set it with <see cref="Recording" />.</summary>
    public bool Records { get; private init; }

    /// <summary>This process's own thread pool, as the run models it. Set it with <see cref="WithPool" />.</summary>
    public Pool? Pool { get; private init; }

    /// <summary>Builds the limiter each run acquires from. Set it with <see cref="WithLimiter" />.</summary>
    public Func<TimeProvider, RateLimiter>? Limiter { get; private init; }

    /// <summary>The dependency the policy calls.</summary>
    /// <param name="dependency">The dependency.</param>
    /// <returns>A new simulation. The receiver is unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dependency" /> is null.</exception>
    public Simulation Against(Dependency dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);

        return this with { Dependency = dependency };
    }

    /// <summary>
    ///     Records every event the run raises, with the virtual time it was raised at, into
    ///     <see cref="SimulationReport.Timeline" />.
    /// </summary>
    /// <returns>A new simulation. The receiver is unchanged.</returns>
    /// <remarks>
    ///     Off by default because a five-minute run at 500 rps raises a few hundred thousand events,
    ///     and a report that is only read for its ratios should not pay to keep them. Recording changes
    ///     nothing about what the run does: the events are the ones the policy already raises to its
    ///     listener, and the whole simulation is single-threaded on a virtual clock, so the time beside
    ///     each one is read rather than measured. A recorded run reproduces byte for byte from its seed
    ///     exactly as an unrecorded one does.
    /// </remarks>
    public Simulation Recording() => this with { Records = true };

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
    ///     This process's own thread pool, which is what <see cref="Resilience.Saturation" /> measures.
    ///     A property of the run rather than of the policy: every attempt waits in it, whether or not
    ///     the policy is configured to notice.
    /// </summary>
    /// <param name="pool">The pool.</param>
    /// <returns>A new simulation. The receiver is unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pool" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         Deliberately accepted on a policy with no <see cref="Resilience.Saturation" />, because
    ///         that is the comparison worth running: the same pool and the same seed against two
    ///         policies, one that tells a stalled pool apart from a slow dependency and one that cannot.
    ///         Refusing it would leave only "stalled" against "not stalled", which measures the stall
    ///         rather than the setting.
    ///     </para>
    ///     <para>
    ///         Leaving this off does not fall back to the real thread pool. A policy that configures
    ///         <see cref="Resilience.Saturation" /> then reads a modeled pool that never queues and so
    ///         is never saturated - the documented behaviour, and a guarantee rather than an accident of
    ///         the host being idle. Reading the real pool would put a machine-dependent term in a report
    ///         that is otherwise reproducible to the last byte.
    ///     </para>
    /// </remarks>
    public Simulation WithPool(Pool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);

        return this with { Pool = pool };
    }

    /// <summary>
    ///     The limiter every attempt acquires a permit from, built fresh for each run against the
    ///     virtual clock. A limiter bounds what leaves this process, which is the other half of
    ///     controlling amplification - a <see cref="Resilience.Budget" /> bounds the share of traffic
    ///     that is retried, and this bounds the absolute amount that goes out at all.
    /// </summary>
    /// <param name="limiter">
    ///     Builds the limiter. The argument is the run's virtual clock, so a limiter that takes a
    ///     <see cref="TimeProvider" /> measures simulated time rather than the few milliseconds the run
    ///     really takes.
    /// </param>
    /// <returns>A new simulation. The receiver is unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="limiter" /> is null.</exception>
    /// <example>
    ///     <code>
    /// // Limit lives in NResilience.Extensions.
    /// var bulkhead = simulation.WithLimiter(_ => Limit.Concurrency(permits: 50));
    /// var adaptive = simulation.WithLimiter(clock => Limit.Adaptive(maximum: 200, time: clock));
    /// </code>
    /// </example>
    /// <remarks>
    ///     <para>
    ///         <b>A factory rather than a limiter, for two reasons.</b> A limiter that takes a clock
    ///         needs this run's, and the caller has no way to reach it. And a limiter is stateful:
    ///         <see cref="RunAll" /> sharing one would let the first seed's discovered limit decide the
    ///         second seed's run, which would make a band of a scenario a band of the order its seeds
    ///         happened to be listed in.
    ///     </para>
    ///     <para>
    ///         The permit is acquired <i>inside</i> the attempt - after the modeled pool's queue and
    ///         before the dependency is reached - because that is where a real callback acquires one,
    ///         and because a guard a retry bypasses is not a guard. A refused attempt never reaches the
    ///         dependency, so it is not counted in <see cref="SimulationReport.Reached" /> and the
    ///         refusal shows up as a load multiplier below one. The lease is held for the length of the
    ///         attempt, which is what makes a concurrency limit a bulkhead.
    ///     </para>
    ///     <para>
    ///         <b>A replenishing limiter is refused.</b> <c>Limit.PerSecond</c> and
    ///         <c>Limit.PerWindow</c> return platform limiters that refill against the wall clock, and
    ///         a simulation has no wall clock - five virtual minutes are a few real milliseconds, so
    ///         one would hand out a single window's permits and refuse everything afterwards. That is
    ///         not a limit anybody configured, so the run stops rather than reporting it.
    ///     </para>
    ///     <para>
    ///         The limiter is the caller's, as it is everywhere else in the library, and the run does
    ///         not dispose it. The two kinds a simulation accepts hold no timer and no unmanaged
    ///         handle, so a caller who wants to read what an adaptive limiter settled on can close over
    ///         it and do so after the run.
    ///     </para>
    /// </remarks>
    public Simulation WithLimiter(Func<TimeProvider, RateLimiter> limiter)
    {
        ArgumentNullException.ThrowIfNull(limiter);

        return this with { Limiter = limiter };
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
        Pool?.Validate();

        return new Execution(this, seed).Execute();
    }

    /// <summary>
    ///     Runs the simulation once per seed and reports the band of what they measured.
    ///     <para>
    ///         One run is one sample. The numbers a tuning decision turns on - the worst second's
    ///         amplification, the time to recover, how many times the breaker tripped - are the least
    ///         stable of the ones a report carries, so two policies compared on a single seed can swap
    ///         places on the next one. Nothing here sleeps, so a band of twenty costs twenty times
    ///         almost nothing.
    ///     </para>
    /// </summary>
    /// <param name="seeds">The seeds. At least one, and no repeats.</param>
    /// <returns>The band.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="seeds" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="seeds" /> is empty, or names a seed twice.</exception>
    /// <exception cref="ResilienceConfigurationException">The policy, the dependency or the load cannot be run.</exception>
    /// <exception cref="InvalidOperationException">The simulation is missing its dependency, its load or its duration.</exception>
    /// <remarks>
    ///     A repeated seed is refused rather than run twice, because the second copy narrows the band
    ///     it was added to widen: it would make the answer look more robust for having been measured
    ///     less. The seeds are a part of the scenario like the load and the duration are - the same set
    ///     produces the same band - so they belong beside it in source rather than being drawn here.
    /// </remarks>
    public SimulationBand RunAll(params int[] seeds)
    {
        ArgumentNullException.ThrowIfNull(seeds);

        if (seeds.Length == 0)
            throw new ArgumentException("A band needs at least one seed.", nameof(seeds));

        if (new HashSet<int>(seeds).Count != seeds.Length)
            throw new ArgumentException("A band cannot run the same seed twice - the repeat would narrow the band rather than widen it.", nameof(seeds));

        var reports = new SimulationReport[seeds.Length];

        for (var i = 0; i < seeds.Length; i++)
            reports[i] = Run(seeds[i]);

        return new SimulationBand(reports);
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

        /// <summary>How many calls were offered at each level, and how many of those succeeded.</summary>
        private readonly int[] _offered = new int[Enum.GetValues<Criticality>().Length];

        private readonly int[] _served = new int[Enum.GetValues<Criticality>().Length];

        private readonly Resilience _policy;

        private readonly int _seed;

        /// <summary>When each call started, in start order - which is why recovery can be read off it directly.</summary>
        private readonly List<long> _starts = [];

        private readonly Func<CancellationToken, Task> _work;

        /// <summary>The modeled pool, or null when the run models none.</summary>
        private readonly Pool? _pool;

        /// <summary>This run's limiter, or null when the run has none.</summary>
        private readonly LimiterGate? _gate;

        /// <summary>Keeps the policy's saturation reading at what the modeled pool last published.</summary>
        private ProbeDriver? _probes;

        /// <summary>Attempts the limiter refused before they could leave the process.</summary>
        private int _refused;

        /// <summary>The recording, or null when the run was not asked for one.</summary>
        private readonly List<TimelineEntry>? _timeline;

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
            _timeline = simulation.Records ? [] : null;

            // The pool lengthens every attempt whatever the policy thinks, so it is installed first and
            // unconditionally. Only the reading below is conditional.
            _pool = simulation.Pool;

            // Built here rather than by the caller so it gets this run's clock, and built once per run
            // so a band measures the scenario rather than the order its seeds were listed in.
            if (simulation.Limiter is { } build)
                _gate = new LimiterGate(build, _clock, "the simulation");

            // A policy that does not configure Saturation never asks for a reading, and the
            // process-wide probe is never even queued for it. One that does gets a modeled reading
            // whether or not a pool was supplied: the alternative is reading the real pool, which is the
            // one input to a run the simulator would not own.
            if (simulation.Policy.Saturation is not { } saturation)
                return;

            if (_pool is null)
            {
                ProbeDriver.Freeze(_policy);
                return;
            }

            _probes = new ProbeDriver([new ProbeDriver.Reader(_pool, _policy, saturation.MinimumSamples)]);
            _clock.OnAdvance = _probes.Refresh;
            _probes.Refresh(_clock.Now);
        }

        internal SimulationReport Execute()
        {
            // The library draws its jitter from a thread-static stream, and the whole run happens on
            // this thread - so seeding it here is what makes a backoff curve and a break duration
            // reproducible alongside everything else.
            Rng.SeedWith(unchecked((uint)_seed));

            Drive();

            if (_gate is { Queued: true })
                throw new InvalidOperationException(LimiterGate.QueuedMessage("the simulation"));

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
                _kinds,
                _offered,
                _served,
                _refused,
                _timeline is null ? null : [.. _timeline]);
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

        /// <summary>
        ///     Starts one caller-level call. Runs inline until it suspends, which is the arrival's whole
        ///     cost.
        /// </summary>
        /// <remarks>
        ///     An unmixed load draws nothing here, so every run that predates a mix reproduces from its
        ///     seed exactly as it did: a draw taken unconditionally would shift the whole stream and
        ///     silently rewrite every report the simulator has ever produced.
        /// </remarks>
        private void Start()
        {
            var index = _starts.Count;
            var criticality = _load.IsMixed ? _load.Draw(_dice) : Criticality.Critical;

            _starts.Add(_clock.Now);
            _ok.Add(false);
            _offered[(int)criticality]++;
            BucketAt(_clock.Now).Calls++;
            _outstanding++;

            if (!_load.IsMixed)
            {
                _ = Call(index, criticality);

                return;
            }

            // Published the way a service publishes one, so the executor reads a level that arrived
            // rather than one the simulator handed it. The scope is disposed once the call has
            // suspended, by which point the level is already inside its execution context - and the
            // executor has read it anyway, because it reads it once, synchronously, before the first
            // attempt.
            using var scope = AmbientCriticality.Begin(criticality);

            _ = Call(index, criticality);
        }

        private async Task Call(int index, Criticality criticality)
        {
            var started = _clock.Now;

            try
            {
                var result = await _policy.TryRunAsync(_work).ConfigureAwait(false);

                if (result.IsSuccess)
                {
                    _ok[index] = true;
                    _succeeded++;
                    _served[(int)criticality]++;
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
        /// <remarks>
        ///     A modeled pool is waited on before the dependency is reached, because that is where the
        ///     wait actually is: a work item sits in the queue before it runs, and the executor's stopwatch
        ///     is already running when it does. Every estimator in the library therefore attributes the
        ///     queue delay to the dependency, and correcting that is the whole of what
        ///     <see cref="Resilience.Saturation" /> does - so a simulation that reported the delay to the
        ///     probe without also putting it inside the measured duration would have nothing for
        ///     saturation to protect, and would make the feature look like a counter of events.
        ///     <para>
        ///         Once per attempt rather than once per resumption, which understates a deep queue: a
        ///         real attempt pays the wait again every time a continuation is scheduled. The
        ///         conservative direction, and the one that does not need a model of how many times the
        ///         executor's own await points suspend.
        ///     </para>
        /// </remarks>
        private async Task Serve(CancellationToken cancellationToken)
        {
            if (_pool is { } pool)
            {
                var queued = pool.DelayAt(TimeSpan.FromTicks(_clock.Now));

                if (queued > TimeSpan.Zero && !await _clock.Sleep(queued, cancellationToken).ConfigureAwait(false))
                    cancellationToken.ThrowIfCancellationRequested();
            }

            // Acquired after the pool's queue and before the dependency, because that is where a real
            // callback acquires it: the work item is scheduled, then it runs and asks for a permit,
            // then it sends. The lease is held for the length of the attempt, which is the whole of
            // what makes a concurrency limit a bulkhead.
            RateLimitLease? lease = null;

            if (_gate is not null)
            {
                try
                {
                    lease = await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (RateLimitedException) when (!_gate.Queued)
                {
                    _refused++;

                    throw;
                }
            }

            try
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
            finally
            {
                lease?.Dispose();
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

        /// <summary>
        ///     The listener. Events fire inline on the driver thread, so the clock is already at the
        ///     instant the event describes and the reading beside it is a read rather than a
        ///     measurement.
        /// </summary>
        private void Record(CallEvent callEvent)
        {
            _kinds[(int)callEvent.Kind]++;
            _timeline?.Add(new TimelineEntry(TimeSpan.FromTicks(_clock.Now), callEvent));
        }

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
