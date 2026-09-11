using NResilience.Internal;
using NResilience.Testing.Internal;

namespace NResilience.Testing;

/// <summary>
///     A graph of services calling each other, being built. Start with <see cref="Simulate.Topology" />;
///     every method returns a new value, so a half-built graph can be shared between tests that vary
///     the rest.
///     <para>
///         <see cref="Simulate.Policy" /> answers what one policy costs one dependency, which is the
///         question a policy's author has. The question a team has is the other one: <i>when the bank
///         browns out, does the checkout fall over?</i> That is a property of a call graph rather than
///         of a policy - a retry storm is B retrying C while A retries B, and B's breaker opening
///         changes the load A offers - and no arrangement of a single-dependency run can show it.
///     </para>
/// </summary>
/// <example>
///     <code>
/// var report = Simulate.Topology()
///     .Calls("checkout", "payments", Policies.Payments)
///     .Calls("checkout", "catalog", Policies.Read)
///     .Calls("payments", "bank", Policies.Bank)
///     .Leaf("bank", Dependency.Healthy(p50, p99).Brownout(after, slower: 8, lasting))
///     .Leaf("catalog", Dependency.Healthy(p50, p99))
///     .Under(Load.Constant(perSecond: 500), at: "checkout")
///     .For(TimeSpan.FromMinutes(5))
///     .Run(seed: 42);
///
/// // The number the bank's owner asks for.
/// Assert.True(report.On("payments", "bank").LoadMultiplier &lt;= 1.2);
///
/// // And the one the business asks for.
/// Assert.True(report.At("checkout").Availability &gt; 0.99);
/// </code>
/// </example>
/// <remarks>
///     A class rather than a record, unlike <see cref="Simulation" />: a graph holds arrays, so the
///     equality a record would generate compares references and would be a claim this type cannot
///     honour. Sharing a half-built one works the same way, because nothing here mutates.
/// </remarks>
/// <seealso cref="Simulate" />
/// <seealso cref="TopologyReport" />
public sealed class Topology
{
    private readonly Declared[] _edges;

    private readonly Offered[] _loads;

    private readonly Leafed[] _leaves;

    internal Topology()
    {
        _edges = [];
        _leaves = [];
        _loads = [];
    }

    private Topology(Declared[] edges, Leafed[] leaves, Offered[] loads, TimeSpan duration, bool records)
    {
        _edges = edges;
        _leaves = leaves;
        _loads = loads;
        Duration = duration;
        Records = records;
    }

    /// <summary>How long the run offers load for. Set it with <see cref="For" />.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Every call declared, in the order they were declared.</summary>
    public IReadOnlyList<Edge> Edges => [.. _edges.Select(edge => new Edge(edge.Caller, edge.Callee))];

    /// <summary>Whether the run records a timeline per edge. Set it with <see cref="Recording" />.</summary>
    public bool Records { get; }

    /// <summary>
    ///     Declares that one service calls another, under a policy. The policy belongs to the call
    ///     rather than to either end of it, because that is where a real one is configured: the
    ///     checkout retries its payment provider on different terms from its catalog.
    /// </summary>
    /// <param name="caller">The service making the call. A service is any name with a call of its own.</param>
    /// <param name="callee">The service or <see cref="Leaf">leaf</see> being called.</param>
    /// <param name="policy">The caller's policy for this call. Its clock is replaced by the run's.</param>
    /// <returns>A new topology. The receiver is unchanged.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     A service's calls are made <b>in the order they were declared, one after another</b>. A page
    ///     that fans out to three services in parallel is not this, and modeling it as this overstates
    ///     what the caller waits - the sum rather than the longest. Two calls the caller really does
    ///     make in sequence are exactly this.
    /// </remarks>
    public Topology Calls(string caller, string callee, Resilience policy)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(callee);
        ArgumentNullException.ThrowIfNull(policy);

        return new Topology([.. _edges, new Declared(caller, callee, policy)], _leaves, _loads, Duration, Records);
    }

    /// <summary>
    ///     Declares a leaf: something the graph calls and that calls nothing back, modeled the way
    ///     <see cref="Simulate.Policy" /> models its one dependency. A database, a third party, the edge
    ///     of what is being simulated.
    /// </summary>
    /// <param name="name">The leaf's name, as the services that call it name it.</param>
    /// <param name="dependency">The dependency.</param>
    /// <returns>A new topology. The receiver is unchanged.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public Topology Leaf(string name, Dependency dependency)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(dependency);

        return new Topology(_edges, [.. _leaves, new Leafed(name, dependency)], _loads, Duration, Records);
    }

    /// <summary>
    ///     Offers traffic at one service. Call it more than once to drive several entry points at once,
    ///     which is the honest way to ask what a shared dependency feels.
    /// </summary>
    /// <param name="load">The traffic. Its <see cref="Load.Peers" /> must be one - see the remarks.</param>
    /// <param name="at">The service the traffic arrives at. It must have a call of its own.</param>
    /// <returns>A new topology. The receiver is unchanged.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     <see cref="Load.Peers" /> is refused here. It exists so a single-dependency run can ask "does
    ///     my retry budget hold when I am one of fifty" without claiming to have simulated fifty
    ///     policies - and a topology is the place where the peers <i>do</i> have policies, so
    ///     approximating them away is the one thing it should not offer. Offer the load at another entry
    ///     instead, or accept that this graph is one process per service.
    /// </remarks>
    public Topology Under(Load load, string at)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(at);

        return new Topology(_edges, _leaves, [.. _loads, new Offered(at, load)], Duration, Records);
    }

    /// <summary>How long to offer load for. Calls still in flight when it elapses are allowed to finish.</summary>
    /// <param name="duration">The run length. Must be positive.</param>
    /// <returns>A new topology. The receiver is unchanged.</returns>
    public Topology For(TimeSpan duration) => new(_edges, _leaves, _loads, duration, Records);

    /// <summary>
    ///     Records every event each edge's policy raises, with the virtual time it was raised at, into
    ///     that edge's <see cref="SimulationReport.Timeline" />.
    /// </summary>
    /// <returns>A new topology. The receiver is unchanged.</returns>
    public Topology Recording() => new(_edges, _leaves, _loads, Duration, true);

    /// <summary>
    ///     Runs the graph and reports what happened, per edge and per service. Nothing sleeps.
    /// </summary>
    /// <param name="seed">Fixes the random stream. The same seed produces the same report.</param>
    /// <returns>The report.</returns>
    /// <exception cref="ResilienceConfigurationException">A policy, a dependency or a load cannot be run.</exception>
    /// <exception cref="InvalidOperationException">The graph is not one the simulator can run.</exception>
    public TopologyReport Run(int seed)
    {
        Validate();

        return new Execution(this, seed).Execute();
    }

    /// <summary>
    ///     Runs the graph once per seed and reports the band of what they measured. The same argument
    ///     <see cref="Simulation.RunAll" /> makes, and more of it: a graph has more places for one draw
    ///     to decide an answer.
    /// </summary>
    /// <param name="seeds">The seeds. At least one, and no repeats.</param>
    /// <returns>The band.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="seeds" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="seeds" /> is empty, or names a seed twice.</exception>
    public TopologyBand RunAll(params int[] seeds)
    {
        ArgumentNullException.ThrowIfNull(seeds);

        if (seeds.Length == 0)
            throw new ArgumentException("A band needs at least one seed.", nameof(seeds));

        if (new HashSet<int>(seeds).Count != seeds.Length)
            throw new ArgumentException("A band cannot run the same seed twice - the repeat would narrow the band rather than widen it.", nameof(seeds));

        var reports = new TopologyReport[seeds.Length];

        for (var i = 0; i < seeds.Length; i++)
            reports[i] = Run(seeds[i]);

        return new TopologyBand(reports);
    }

    /// <summary>
    ///     Checks the graph and throws listing every problem at once, the same way
    ///     <see cref="Resilience.Validate" /> does.
    /// </summary>
    /// <exception cref="InvalidOperationException">The graph is not one the simulator can run.</exception>
    /// <exception cref="ResilienceConfigurationException">A policy, a dependency or a load cannot be run.</exception>
    public void Validate()
    {
        var problems = new List<string>();

        if (_edges.Length == 0)
            problems.Add("The topology has no calls. Declare at least one with Calls().");

        if (_loads.Length == 0)
            problems.Add("The topology has no load. Call Under() with a service to offer traffic at.");

        if (Duration <= TimeSpan.Zero)
            problems.Add("The topology has no duration. Call For() with a positive time.");

        var leaves = new HashSet<string>(StringComparer.Ordinal);

        foreach (var leaf in _leaves)
        {
            if (!leaves.Add(leaf.Name))
                problems.Add($"'{leaf.Name}' is declared as a leaf more than once.");
        }

        var callers = new HashSet<string>(_edges.Select(edge => edge.Caller), StringComparer.Ordinal);
        var declared = new HashSet<(string, string)>();

        foreach (var edge in _edges)
        {
            if (string.Equals(edge.Caller, edge.Callee, StringComparison.Ordinal))
                problems.Add($"'{edge.Caller}' calls itself, which a run would never finish.");

            if (!declared.Add((edge.Caller, edge.Callee)))
                problems.Add($"'{edge.Caller}' calls '{edge.Callee}' more than once. One call, one policy - declare a second callee instead.");

            if (leaves.Contains(edge.Caller))
                problems.Add($"'{edge.Caller}' is declared as a leaf and also makes calls. A leaf is the edge of what is simulated.");

            if (!leaves.Contains(edge.Callee) && !callers.Contains(edge.Callee))
                problems.Add($"'{edge.Callee}' is called but never declared: give it a Leaf() with a dependency, or a Calls() of its own.");
        }

        foreach (var offered in _loads)
        {
            if (!callers.Contains(offered.At))
                problems.Add($"Load is offered at '{offered.At}', which makes no calls. Traffic has to arrive somewhere that does something.");

            if (offered.Load.Peers != 1)
            {
                problems.Add(
                    $"Load offered at '{offered.At}' has {offered.Load.Peers} peers. Peers approximate processes that have no policies of "
                    + "their own, and a topology is where they would have them - offer the load at another entry instead.");
            }
        }

        if (Cycle() is { } cycle)
            problems.Add($"The calls form a cycle: {cycle}. A run would never finish, and a cycle is a different simulator.");

        if (problems.Count > 0)
            throw new InvalidOperationException("The topology cannot be run:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", problems));

        foreach (var edge in _edges)
            edge.Policy.Validate();

        foreach (var leaf in _leaves)
            leaf.Dependency.Validate();

        foreach (var offered in _loads)
            offered.Load.Validate();
    }

    /// <summary>The first cycle found, rendered as a path, or null when the calls form a DAG.</summary>
    private string? Cycle()
    {
        var outgoing = _edges
            .GroupBy(edge => edge.Caller, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.Callee).ToArray(), StringComparer.Ordinal);

        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = new List<string>();

        foreach (var start in outgoing.Keys)
        {
            if (Walk(start) is { } found)
                return found;
        }

        return null;

        string? Walk(string node)
        {
            if (state.TryGetValue(node, out var seen))
                return seen == 1 ? string.Join(" -> ", path[path.IndexOf(node)..]) + " -> " + node : null;

            state[node] = 1;
            path.Add(node);

            if (outgoing.TryGetValue(node, out var next))
            {
                foreach (var callee in next)
                {
                    if (Walk(callee) is { } found)
                        return found;
                }
            }

            state[node] = 2;
            path.RemoveAt(path.Count - 1);

            return null;
        }
    }


    /// <summary>
    ///     One run of a graph. The same shape as <see cref="Simulation" />'s execution and for the same
    ///     reasons: one clock, one random stream, one thread, and every tally written by the driver and
    ///     by the callbacks it starts.
    /// </summary>
    private sealed class Execution
    {
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

        private readonly SimulationClock _clock = new();

        private readonly ChaosDice _dice;

        private readonly TimeSpan _duration;

        private readonly EdgeState[] _edges;

        private readonly EntryState[] _entries;

        private readonly Dictionary<string, NodeState> _nodes = new(StringComparer.Ordinal);

        private readonly int _seed;

        private Exception? _fault;

        private int _outstanding;

        internal Execution(Topology topology, int seed)
        {
            _seed = seed;
            _duration = topology.Duration;
            _dice = new ChaosDice(seed);

            foreach (var leaf in topology._leaves)
                _nodes[leaf.Name] = new NodeState(leaf.Name, leaf.Dependency);

            var edges = new List<EdgeState>();

            foreach (var declared in topology._edges)
            {
                var caller = Node(declared.Caller);
                var edge = new EdgeState(caller, Node(declared.Callee), declared.Caller, declared.Callee, topology.Records);

                // Every policy in the graph is put on the one clock, and its events are recorded against
                // the edge that raised them - which is what keeps a graph's telemetry attributable when
                // a dozen policies are raising events into the same run.
                edge.Policy = declared.Policy
                    .WithClock(_clock)
                    .WithListener(raised => edge.Record(TimeSpan.FromTicks(_clock.Now), raised));

                edge.Work = token => Reach(edge, token);

                caller.Out.Add(edge);
                edges.Add(edge);
            }

            _edges = [.. edges];
            _entries = [.. topology._loads.Select(offered => new EntryState(Node(offered.At), offered.Load))];

            NodeState Node(string name)
            {
                if (!_nodes.TryGetValue(name, out var node))
                    _nodes[name] = node = new NodeState(name, null);

                return node;
            }
        }

        internal TopologyReport Execute()
        {
            Rng.SeedWith(unchecked((uint)_seed));

            Drive();

            if (_fault is not null)
                throw new InvalidOperationException("A simulated call failed outside every policy in the graph.", _fault);

            var edges = new Dictionary<Edge, SimulationReport>(EdgeComparer.Instance);
            var nodes = new Dictionary<string, SimulationReport>(StringComparer.Ordinal);

            foreach (var edge in _edges)
                edges[new Edge(edge.CallerName, edge.CalleeName)] = edge.Report(_seed, _duration);

            // Declaration order, callers before the leaves they call, so the listing a report prints
            // reads the way the graph was written rather than the way a hash table happened to bucket
            // it.
            var names = new List<string>();

            foreach (var name in _edges.Select(edge => edge.CallerName).Concat(_edges.Select(edge => edge.CalleeName)))
            {
                if (!names.Contains(name, StringComparer.Ordinal))
                    names.Add(name);
            }

            foreach (var name in names)
                nodes[name] = _nodes[name].Report(_seed, _duration);

            return new TopologyReport(
                _seed,
                _duration,
                [.. _edges.Select(edge => new Edge(edge.CallerName, edge.CalleeName))],
                [.. _entries.Select(entry => entry.Node.Name)],
                names,
                edges,
                nodes);
        }

        /// <summary>
        ///     The driver: advance to whichever comes first, the next arrival at any entry or the next
        ///     timer, and let the continuations run inline. A tie between two entries goes to the one
        ///     declared first, which makes it a rule rather than a race.
        /// </summary>
        private void Drive()
        {
            var end = _duration.Ticks;
            var next = new long[_entries.Length];

            for (var i = 0; i < _entries.Length; i++)
                next[i] = Gap(_entries[i]);

            while (true)
            {
                var index = -1;
                var arrival = long.MaxValue;

                for (var i = 0; i < next.Length; i++)
                {
                    if (next[i] <= end && next[i] < arrival)
                    {
                        arrival = next[i];
                        index = i;
                    }
                }

                var timer = _clock.NextDue;
                var arriving = index >= 0 && (timer is null || arrival <= timer);

                if (!arriving && timer is null)
                {
                    if (_outstanding == 0 || !Settled())
                        break;

                    continue;
                }

                _clock.AdvanceTo(arriving ? arrival : timer!.Value);

                if (!arriving)
                    continue;

                Start(_entries[index]);
                next[index] += Gap(_entries[index]);
            }
        }

        /// <summary>Whether a timer appeared after the queue looked empty while calls were still in flight.</summary>
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

        private long Gap(EntryState entry)
        {
            var mean = (double)TimeSpan.TicksPerSecond / entry.Load.PerSecond;

            return (long)(2 * _dice.Next() * mean);
        }

        /// <summary>Starts one request at an entry. Runs inline until it suspends.</summary>
        private void Start(EntryState entry)
        {
            _outstanding++;

            if (!entry.Load.IsMixed)
            {
                _ = Request(entry);

                return;
            }

            // Published once at the entry and read all the way down: a level that arrives at the
            // checkout is the level its payment call runs at, which is the whole point of propagating
            // one.
            using var scope = AmbientCriticality.Begin(entry.Load.Draw(_dice));

            _ = Request(entry);
        }

        private async Task Request(EntryState entry)
        {
            try
            {
                await Serve(entry.Node, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Nothing stands above an entry, so a failure that gets this far is the request
                // failing - Serve has already counted it. A fault in the library would have been
                // caught around TryRunAsync in Invoke, where a policy is supposed to contain it.
            }
            finally
            {
                _outstanding--;
            }
        }

        /// <summary>
        ///     One service serving one request, or one leaf answering one attempt. A service makes its
        ///     own calls in the order they were declared, one after another.
        /// </summary>
        private async Task Serve(NodeState node, CancellationToken cancellationToken)
        {
            var started = _clock.Now;
            var criticality = AmbientCriticality.Current;

            node.Served++;
            node.Offered[(int)criticality]++;
            node.BucketAt(started).Calls++;

            try
            {
                if (node.Dependency is { } dependency)
                {
                    node.InFlight++;

                    try
                    {
                        var (latency, fails) = dependency.Serve(TimeSpan.FromTicks(_clock.Now), node.InFlight, _dice);

                        if (latency > TimeSpan.Zero && !await _clock.Sleep(latency, cancellationToken).ConfigureAwait(false))
                            cancellationToken.ThrowIfCancellationRequested();

                        if (fails)
                            throw new IOException($"The simulated dependency '{node.Name}' failed.");
                    }
                    finally
                    {
                        node.InFlight--;
                    }
                }
                else
                {
                    foreach (var edge in node.Out)
                    {
                        if (await Invoke(edge, criticality).ConfigureAwait(false) is { } failure)
                        {
                            // The downstream's own failure, raised to this caller's caller so that its
                            // policy classifies what actually happened rather than a wrapper this
                            // simulator invented.
                            throw failure;
                        }
                    }
                }

                node.Succeeded++;
                node.Completed[(int)criticality]++;
            }
            finally
            {
                node.Latencies.Add(_clock.Now - started);
            }
        }

        /// <summary>
        ///     One call along one edge, under the caller's policy for it. Returns the failure to raise to
        ///     the caller, or null when the call succeeded.
        /// </summary>
        private async Task<Exception?> Invoke(EdgeState edge, Criticality criticality)
        {
            var started = _clock.Now;
            var index = edge.Calls++;

            edge.Starts.Add(started);
            edge.Ok.Add(false);
            edge.Offered[(int)criticality]++;
            edge.BucketAt(started).Calls++;

            CallResult result;

            try
            {
                result = await edge.Policy.TryRunAsync(edge.Work!).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // TryRunAsync does not throw. One that did would be a bug in the library, and it is
                // reported as one rather than counted as a failed call.
                _fault ??= exception;

                return exception;
            }

            edge.Latencies.Add(_clock.Now - started);

            if (!result.IsSuccess)
                return result.Exception ?? new IOException($"The call to '{edge.CalleeName}' failed: {result.Reason}.");

            edge.Ok[index] = true;
            edge.Succeeded++;
            edge.Served[(int)criticality]++;

            return null;
        }

        /// <summary>One attempt reaching the callee. This is the callback the real executor drives.</summary>
        private async Task Reach(EdgeState edge, CancellationToken cancellationToken)
        {
            edge.Reached++;
            edge.BucketAt(_clock.Now).Reached++;

            // The caller's own multiplier is what it sends downstream per request it served, across
            // every edge it has - fan-out and retries together, which is what its own callee feels.
            edge.Caller.Downstream++;
            edge.Caller.BucketAt(_clock.Now).Reached++;

            await Serve(edge.Callee, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>One second of a run, from the caller's side and from the callee's.</summary>
        private sealed class Bucket
        {
            internal int Calls;

            internal int Reached;
        }

        private sealed class NodeState(string name, Dependency? dependency)
        {
            private readonly List<Bucket> _buckets = [];

            internal int Downstream;

            internal int InFlight;

            internal int Served;

            internal int Succeeded;

            internal int[] Completed { get; } = new int[Enum.GetValues<Criticality>().Length];

            internal Dependency? Dependency { get; } = dependency;

            internal List<long> Latencies { get; } = [];

            internal string Name { get; } = name;

            internal int[] Offered { get; } = new int[Enum.GetValues<Criticality>().Length];

            internal List<EdgeState> Out { get; } = [];

            internal Bucket BucketAt(long ticks)
            {
                var index = (int)(ticks / Window.Ticks);

                while (_buckets.Count <= index)
                    _buckets.Add(new Bucket());

                return _buckets[index];
            }

            internal SimulationReport Report(int seed, TimeSpan duration) =>
                new(
                    seed,
                    duration,
                    Served,
                    Succeeded,

                    // A leaf makes no calls of its own, so every request it served did arrive: its
                    // multiplier is one rather than zero, which would read as a node that refused
                    // everything it was sent.
                    Dependency is null ? Downstream : Served,
                    Amplification(),
                    null,
                    [.. Latencies],
                    new int[Enum.GetValues<CallEventKind>().Length],
                    Offered,
                    Completed,
                    0,
                    null);

            private double Amplification()
            {
                if (Dependency is not null)
                    return Served == 0 ? 0 : 1;

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
        }

        private sealed class EdgeState(NodeState caller, NodeState callee, string callerName, string calleeName, bool records)
        {
            private readonly List<Bucket> _buckets = [];

            private readonly int[] _kinds = new int[Enum.GetValues<CallEventKind>().Length];

            private readonly List<TimelineEntry>? _timeline = records ? [] : null;

            internal int Calls;

            internal int Reached;

            internal int Succeeded;

            internal NodeState Callee { get; } = callee;

            internal string CalleeName { get; } = calleeName;

            internal NodeState Caller { get; } = caller;

            internal string CallerName { get; } = callerName;

            internal List<long> Latencies { get; } = [];

            internal int[] Offered { get; } = new int[Enum.GetValues<Criticality>().Length];

            internal List<bool> Ok { get; } = [];

            internal Resilience Policy { get; set; } = null!;

            internal int[] Served { get; } = new int[Enum.GetValues<Criticality>().Length];

            internal List<long> Starts { get; } = [];

            internal Func<CancellationToken, Task>? Work { get; set; }

            internal Bucket BucketAt(long ticks)
            {
                var index = (int)(ticks / Window.Ticks);

                while (_buckets.Count <= index)
                    _buckets.Add(new Bucket());

                return _buckets[index];
            }

            internal void Record(TimeSpan at, CallEvent raised)
            {
                _kinds[(int)raised.Kind]++;
                _timeline?.Add(new TimelineEntry(at, raised));
            }

            internal SimulationReport Report(int seed, TimeSpan duration) =>
                new(
                    seed,
                    duration,
                    Calls,
                    Succeeded,
                    Reached,
                    Amplification(),
                    TimeToRecover(duration),
                    [.. Latencies],
                    _kinds,
                    Offered,
                    Served,
                    0,
                    _timeline is null ? null : [.. _timeline]);

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
            ///     How long after the callee's own impairment ended before this caller saw a full second
            ///     of nothing but successes. Only a leaf is impaired on a schedule, so an edge into a
            ///     service has nothing to recover from and reports null - the recovery worth reading is
            ///     the one on the edge into the leaf that broke, and then on every edge above it.
            /// </summary>
            private TimeSpan? TimeToRecover(TimeSpan duration)
            {
                if (Callee.Dependency?.ImpairedUntil is not { } impaired)
                    return null;

                var failures = new int[Starts.Count + 1];

                for (var i = 0; i < Starts.Count; i++)
                    failures[i + 1] = failures[i] + (Ok[i] ? 0 : 1);

                var last = 0;

                for (var i = 0; i < Starts.Count; i++)
                {
                    if (Starts[i] < impaired.Ticks)
                        continue;

                    var closes = Starts[i] + Window.Ticks;

                    if (closes > duration.Ticks)
                        return null;

                    if (last < i)
                        last = i;

                    while (last < Starts.Count && Starts[last] < closes)
                        last++;

                    if (failures[last] - failures[i] == 0)
                        return TimeSpan.FromTicks(Starts[i] - impaired.Ticks);
                }

                return null;
            }
        }

        private sealed class EntryState(NodeState node, Load load)
        {
            internal Load Load { get; } = load;

            internal NodeState Node { get; } = node;
        }

        private sealed class EdgeComparer : IEqualityComparer<Edge>
        {
            internal static readonly EdgeComparer Instance = new();

            public bool Equals(Edge x, Edge y) =>
                string.Equals(x.Caller, y.Caller, StringComparison.Ordinal)
                && string.Equals(x.Callee, y.Callee, StringComparison.Ordinal);

            public int GetHashCode(Edge obj) => HashCode.Combine(obj.Caller, obj.Callee);
        }
    }

    /// <summary>One declared call, before the graph is resolved.</summary>
    private readonly record struct Declared(string Caller, string Callee, Resilience Policy);

    /// <summary>One declared leaf.</summary>
    private readonly record struct Leafed(string Name, Dependency Dependency);

    /// <summary>One declared entry point and the traffic arriving at it.</summary>
    private readonly record struct Offered(string At, Load Load);
}
