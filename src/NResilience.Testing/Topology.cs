using System.Threading.RateLimiting;
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

    private readonly Limited[] _limiters;

    private readonly Offered[] _loads;

    private readonly Leafed[] _leaves;

    private readonly Pooled[] _pools;

    private readonly Shared[] _shared;

    internal Topology()
    {
        _edges = [];
        _leaves = [];
        _loads = [];
        _limiters = [];
        _shared = [];
        _pools = [];
    }

    private Topology(Topology from, Declared[]? edges = null, Leafed[]? leaves = null, Offered[]? loads = null,
        Limited[]? limiters = null, Shared[]? shared = null, Pooled[]? pools = null, TimeSpan? duration = null, bool? records = null)
    {
        _edges = edges ?? from._edges;
        _leaves = leaves ?? from._leaves;
        _loads = loads ?? from._loads;
        _limiters = limiters ?? from._limiters;
        _shared = shared ?? from._shared;
        _pools = pools ?? from._pools;
        Duration = duration ?? from.Duration;
        Records = records ?? from.Records;
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

        return new Topology(this, edges: [.. _edges, new Declared(caller, callee, policy)]);
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

        return new Topology(this, leaves: [.. _leaves, new Leafed(name, dependency)]);
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
    ///     <see cref="Load.Peers" /> is refused on a graph - a topology of more than one call. It exists
    ///     so a single-dependency run can ask "does my retry budget hold when I am one of fifty" without
    ///     claiming to have simulated fifty policies, and a graph is the place where the peers <i>do</i>
    ///     have policies, so approximating them away is the one thing it should not offer. Offer the load
    ///     at another entry instead, or accept that this graph is one process per service. A topology of
    ///     one call accepts peers: it is the single-dependency model in this shape, not a graph.
    /// </remarks>
    public Topology Under(Load load, string at)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(at);

        return new Topology(this, loads: [.. _loads, new Offered(at, load)]);
    }

    /// <summary>
    ///     The limiter one call acquires a permit from, built fresh for each run against the virtual
    ///     clock. A limiter guards one outbound call, which is why it goes on a call rather than on a
    ///     service: a checkout's bulkhead for its payment provider is not its bulkhead for its catalog.
    /// </summary>
    /// <param name="caller">The service making the call.</param>
    /// <param name="callee">The service or leaf being called.</param>
    /// <param name="limiter">Builds the limiter. The argument is the run's virtual clock.</param>
    /// <returns>A new topology. The receiver is unchanged.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     The same two kinds <see cref="Simulation.WithLimiter" /> refuses are refused here, for the
    ///     same reasons: a replenishing limiter refills against a wall clock the run does not have, and
    ///     a queueing one resumes a queued wait on the thread pool rather than on the thread the run
    ///     drives everything from. The permit is acquired before the attempt is counted as having
    ///     reached the callee, because a refused attempt never left.
    /// </remarks>
    public Topology WithLimiter(string caller, string callee, Func<TimeProvider, RateLimiter> limiter)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(callee);
        ArgumentNullException.ThrowIfNull(limiter);

        return new Topology(this, limiters: [.. _limiters, new Limited(caller, callee, limiter)]);
    }

    /// <summary>
    ///     The limiter one service acquires a permit from on <i>every</i> call it makes, built fresh for
    ///     each run against the virtual clock. The process-wide bulkhead: a bound on how much work this
    ///     service has in flight anywhere, rather than on how much it has in flight against one callee.
    /// </summary>
    /// <param name="service">The service. It must make calls of its own.</param>
    /// <param name="limiter">Builds the limiter. The argument is the run's virtual clock.</param>
    /// <returns>A new topology. The receiver is unchanged.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     <para>
    ///         One limiter, shared by every call the service makes, which makes it a bound on the
    ///         <i>total</i> this service has in flight rather than on each call separately - and so a
    ///         much tighter bound than the same number written per call. That is the difference between
    ///         the two guards, and the reason to reach for one rather than the other.
    ///     </para>
    ///     <para>
    ///         It is <b>not</b> a way to see a slow callee starve the calls to a healthy one. That shape
    ///         needs a service whose calls run at the same time, and a service here makes its calls one
    ///         after another: a request holds one permit at a time, and a request whose first call is
    ///         refused never makes its second. A graph cannot show that failure, and says so rather than
    ///         producing a number that looks like it.
    ///     </para>
    ///     <para>
    ///         A service may have this and a per-call limiter at once, which is the two-level bulkhead:
    ///         the service's permit is acquired first, then the call's, so the broader bound is the one
    ///         a refusal reports first. Either refusal is counted against the call that was attempting.
    ///     </para>
    /// </remarks>
    public Topology WithLimiter(string service, Func<TimeProvider, RateLimiter> limiter)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(limiter);

        return new Topology(this, shared: [.. _shared, new Shared(service, limiter)]);
    }

    /// <summary>
    ///     One service's own thread pool, as the run models it. A pool belongs to a service because it
    ///     is a property of a process, and in a graph each service is one.
    /// </summary>
    /// <param name="service">The service. It must make calls of its own - a leaf is outside what is simulated.</param>
    /// <param name="pool">The pool.</param>
    /// <returns>A new topology. The receiver is unchanged.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    ///     <para>
    ///         The queue delay is paid by every attempt this service makes, on every call it makes,
    ///         whether or not the policy on that call is configured to notice - because that is where
    ///         the wait actually is. A policy that does configure <see cref="Resilience.Saturation" />
    ///         reads <i>its caller's</i> pool: the work item waiting for a thread is the outbound call,
    ///         and the process it waits in is the one making it.
    ///     </para>
    ///     <para>
    ///         This is the shape the feature exists for, at the scale it actually bites.
    ///         <see cref="Resilience.Saturation" /> stops a stalled process mistaking itself for a slow
    ///         dependency - but to everyone <i>calling</i> that process, a stall and a slow dependency
    ///         are still the same thing, and their estimators adapt to a problem that is not theirs. A
    ///         graph is the only place that shows.
    ///     </para>
    ///     <para>
    ///         A policy that configures <see cref="Resilience.Saturation" /> on a call from a service
    ///         with no pool reads a modeled pool that never queues, exactly as
    ///         <see cref="Simulation.WithPool" /> documents for a run with none.
    ///     </para>
    /// </remarks>
    public Topology WithPool(string service, Pool pool)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(pool);

        return new Topology(this, pools: [.. _pools, new Pooled(service, pool)]);
    }

    /// <summary>How long to offer load for. Calls still in flight when it elapses are allowed to finish.</summary>
    /// <param name="duration">The run length. Must be positive.</param>
    /// <returns>A new topology. The receiver is unchanged.</returns>
    public Topology For(TimeSpan duration) => new(this, duration: duration);

    /// <summary>
    ///     Records every event each edge's policy raises, with the virtual time it was raised at, into
    ///     that edge's <see cref="SimulationReport.Timeline" />.
    /// </summary>
    /// <returns>A new topology. The receiver is unchanged.</returns>
    public Topology Recording() => new(this, records: true);

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

            // Peers approximate processes that have no policies of their own, and a graph is where they
            // would have them - so a graph refuses them. A one-call topology is not a graph: it is the
            // single-dependency model written in this shape, it measures exactly what Simulate measures,
            // and refusing peers there would refuse the one question peers exist to answer.
            if (offered.Load.Peers != 1 && _edges.Length > 1)
            {
                problems.Add(
                    $"Load offered at '{offered.At}' has {offered.Load.Peers} peers, and this topology makes {_edges.Length} calls. "
                    + "Peers approximate processes that have no policies of their own, and a graph is where they would have them - "
                    + "offer the load at another entry instead, or reduce the graph to a single call.");
            }
        }

        var limited = new HashSet<(string, string)>();

        foreach (var limiter in _limiters)
        {
            if (!declared.Contains((limiter.Caller, limiter.Callee)))
                problems.Add($"A limiter is declared for '{limiter.Caller}' calling '{limiter.Callee}', which is not a call this topology makes.");
            else if (!limited.Add((limiter.Caller, limiter.Callee)))
                problems.Add($"'{limiter.Caller}' calling '{limiter.Callee}' has more than one limiter. One call, one limiter.");
        }

        var bulkheaded = new HashSet<string>(StringComparer.Ordinal);

        foreach (var shared in _shared)
        {
            if (leaves.Contains(shared.Service))
                problems.Add($"'{shared.Service}' is a leaf and cannot have a limiter. A leaf makes no calls to bound.");
            else if (!callers.Contains(shared.Service))
                problems.Add($"A limiter is declared for '{shared.Service}', which makes no calls. A limiter bounds what a service sends out.");
            else if (!bulkheaded.Add(shared.Service))
                problems.Add($"'{shared.Service}' has more than one limiter of its own. One service, one process-wide limiter.");
        }

        var pooled = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pool in _pools)
        {
            if (leaves.Contains(pool.Service))
                problems.Add($"'{pool.Service}' is a leaf and cannot have a pool. A leaf is the edge of what is simulated, and its own process is not.");
            else if (!callers.Contains(pool.Service))
                problems.Add($"A pool is declared for '{pool.Service}', which makes no calls. A pool is what the attempts a service makes wait in.");
            else if (!pooled.Add(pool.Service))
                problems.Add($"'{pool.Service}' has more than one pool. One service, one process, one pool.");
        }

        if (Cycle() is { } cycle)
            problems.Add($"The calls form a cycle: {cycle}. A run would never finish, and a cycle is a different simulator.");

        if (problems.Count > 0)
            throw new InvalidOperationException("The topology cannot be run:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", problems));

        foreach (var edge in _edges)
            edge.Policy.Validate();

        foreach (var leaf in _leaves)
            leaf.Dependency.Validate();

        foreach (var pool in _pools)
            pool.Pool.Validate();

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

        private readonly int _peers;

        private readonly int _seed;

        private Exception? _fault;

        private int _outstanding;

        /// <summary>Keeps every saturation-aware policy reading what its caller's pool published.</summary>
        private ProbeDriver? _probes;

        internal Execution(Topology topology, int seed)
        {
            _seed = seed;
            _duration = topology.Duration;
            _dice = new ChaosDice(seed);

            foreach (var leaf in topology._leaves)
                _nodes[leaf.Name] = new NodeState(leaf.Name, leaf.Dependency);

            var edges = new List<EdgeState>();

            // One scope for the whole graph run: two services configured with the same budget - which
            // is what RetryBudget.Shared hands out for a name - keep sharing one bucket within this
            // run, and share nothing with any other run.
            var budgets = new BudgetClock(_clock);

            foreach (var declared in topology._edges)
            {
                var caller = Node(declared.Caller);
                var edge = new EdgeState(caller, Node(declared.Callee), declared.Caller, declared.Callee, topology.Records);

                // Every policy in the graph is put on the one clock, and its events are recorded against
                // the edge that raised them - which is what keeps a graph's telemetry attributable when
                // a dozen policies are raising events into the same run.
                edge.Policy = declared.Policy
                    .WithClock(_clock, budgets)
                    .WithListener(raised => edge.Record(TimeSpan.FromTicks(_clock.Now), raised));

                edge.Work = token => Reach(edge, token);

                caller.Out.Add(edge);
                edges.Add(edge);
            }

            foreach (var pooled in topology._pools)
                Node(pooled.Service).Pool = pooled.Pool;

            // A policy that configures Saturation reads its *caller's* pool: the work item waiting for
            // a thread is the outbound call, and the process it waits in is the one making it. One that
            // configures it from a service with no pool reads a pool that never queues, which is the
            // same guarantee a single-dependency run gives - never the real host pool, which would put
            // a machine-dependent term in a report that is otherwise reproducible to the last byte.
            var readers = new List<ProbeDriver.Reader>();

            foreach (var edge in edges)
            {
                if (edge.Policy.Saturation is not { } saturation)
                    continue;

                if (edge.Caller.Pool is { } pool)
                    readers.Add(new ProbeDriver.Reader(pool, edge.Policy, saturation.MinimumSamples));
                else
                    ProbeDriver.Freeze(edge.Policy);
            }

            if (readers.Count > 0)
            {
                _probes = new ProbeDriver([.. readers]);
                _clock.OnAdvance = _probes.Refresh;
                _probes.Refresh(_clock.Now);
            }

            // One gate per service, handed to every call it makes - which is what makes it a bulkhead
            // across the process rather than a limiter repeated per call.
            foreach (var shared in topology._shared)
            {
                var gate = new LimiterGate(shared.Build, _clock, $"'{shared.Service}'");

                foreach (var edge in edges.Where(candidate => string.Equals(candidate.CallerName, shared.Service, StringComparison.Ordinal)))
                    edge.ServiceGate = gate;
            }

            foreach (var limited in topology._limiters)
            {
                var edge = edges.Single(candidate =>
                    string.Equals(candidate.CallerName, limited.Caller, StringComparison.Ordinal)
                    && string.Equals(candidate.CalleeName, limited.Callee, StringComparison.Ordinal));

                edge.Gate = new LimiterGate(limited.Build, _clock, $"'{limited.Caller}' calling '{limited.Callee}'");
            }

            _edges = [.. edges];
            _entries = [.. topology._loads.Select(offered => new EntryState(Node(offered.At), offered.Load))];

            // Peers scale what a dependency is offered without being simulated, exactly as they do in a
            // single-dependency run - see Simulation's own Serve. Validate() only permits them on a
            // one-call topology, so there is one entry and one leaf and the factor is unambiguous;
            // on a graph every entry has Peers of 1 and this is 1.
            _peers = topology._loads.Length == 1 ? topology._loads[0].Load.Peers : 1;

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

            foreach (var edge in _edges)
            {
                if (edge.ServiceGate is { Queued: true })
                    throw new InvalidOperationException(LimiterGate.QueuedMessage($"'{edge.CallerName}'"));

                if (edge.Gate is { Queued: true })
                    throw new InvalidOperationException(LimiterGate.QueuedMessage($"'{edge.CallerName}' calling '{edge.CalleeName}'"));
            }

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
                        var (latency, fails) = dependency.Serve(TimeSpan.FromTicks(_clock.Now), node.InFlight * _peers, _dice);

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
        /// <remarks>
        ///     The permit is acquired first, so a refused attempt is not counted as having reached the
        ///     callee - it never left the caller. The lease is held for the length of the attempt, which
        ///     is what makes a concurrency limit a bulkhead.
        /// </remarks>
        private async Task Reach(EdgeState edge, CancellationToken cancellationToken)
        {
            // The caller's own pool, because this attempt is the caller's outbound work item. Waited
            // before the permit and before the attempt counts, and spent inside the measured duration -
            // which is the whole mechanism: a stall lengthens every attempt, so it reaches availability
            // and the latency quantiles exactly as a slow callee does, and that is what Saturation
            // exists to tell apart.
            if (edge.Caller.Pool is { } pool)
            {
                var queued = pool.DelayAt(TimeSpan.FromTicks(_clock.Now));

                if (queued > TimeSpan.Zero && !await _clock.Sleep(queued, cancellationToken).ConfigureAwait(false))
                    cancellationToken.ThrowIfCancellationRequested();
            }

            // The service's permit before the call's, so the broader bound is the one a refusal reports
            // first, and so a call refused by the narrower one has already been counted against the
            // process it was leaving.
            RateLimitLease? outer = null;
            RateLimitLease? inner = null;

            try
            {
                try
                {
                    if (edge.ServiceGate is { } service)
                        outer = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);

                    if (edge.Gate is { } call)
                        inner = await call.AcquireAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (RateLimitedException) when (!Queueing(edge))
                {
                    // Counted against the call that was attempting, whichever limiter refused it: a
                    // service's gate is shared, so it cannot say which of its calls was turned away.
                    edge.Refused++;

                    throw;
                }

                edge.Reached++;
                edge.BucketAt(_clock.Now).Reached++;

                // The caller's own multiplier is what it sends downstream per request it served, across
                // every edge it has - fan-out and retries together, which is what its own callee feels.
                edge.Caller.Downstream++;
                edge.Caller.BucketAt(_clock.Now).Reached++;

                await Serve(edge.Callee, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                inner?.Dispose();
                outer?.Dispose();
            }
        }

        /// <summary>Whether either limiter on a call queued, which stops the run rather than counting.</summary>
        private static bool Queueing(EdgeState edge) => edge.ServiceGate is { Queued: true } || edge.Gate is { Queued: true };

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

            /// <summary>This service's own modeled thread pool, or null when the run models none for it.</summary>
            internal Pool? Pool { get; set; }

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

            /// <summary>This call's own limiter, or null when it has none.</summary>
            internal LimiterGate? Gate { get; set; }

            /// <summary>The caller's process-wide limiter, shared with its other calls, or null.</summary>
            internal LimiterGate? ServiceGate { get; set; }

            /// <summary>Attempts on this call a limiter refused before they could leave the caller.</summary>
            internal int Refused;

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
                    Refused,
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

    /// <summary>One call's limiter, before the graph is resolved.</summary>
    private readonly record struct Limited(string Caller, string Callee, Func<TimeProvider, RateLimiter> Build);

    /// <summary>One service's modeled thread pool, before the graph is resolved.</summary>
    private readonly record struct Pooled(string Service, Pool Pool);

    /// <summary>One service's process-wide limiter, before the graph is resolved.</summary>
    private readonly record struct Shared(string Service, Func<TimeProvider, RateLimiter> Build);

    /// <summary>One declared entry point and the traffic arriving at it.</summary>
    private readonly record struct Offered(string At, Load Load);
}
