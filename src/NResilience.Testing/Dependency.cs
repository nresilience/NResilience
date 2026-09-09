using NResilience.Testing.Internal;

namespace NResilience.Testing;

/// <summary>
///     The thing a simulated policy calls: a latency distribution, an optional capacity, and a
///     schedule of things going wrong. This is the only part of a simulation that is a model - the
///     policy, the executor, the breaker, the budget, the classifier and every estimator are the real
///     ones.
///     <para>
///         Build one with <see cref="Healthy" /> and add impairments with <see cref="Brownout" /> and
///         <see cref="Outage" />. Every method returns a new value, so a dependency can be held in a
///         <c>static readonly</c> field and varied per simulation.
///     </para>
/// </summary>
/// <example>
///     <code>
/// var api = Dependency
///     .Healthy(p50: TimeSpan.FromMilliseconds(20), p99: TimeSpan.FromMilliseconds(200))
///     .Brownout(after: TimeSpan.FromSeconds(30), slower: 8, lasting: TimeSpan.FromMinutes(1));
/// </code>
/// </example>
/// <seealso cref="Simulate" />
public sealed record Dependency
{
    /// <summary>Beyond this multiple of <see cref="Concurrency" />, the dependency sheds rather than queues.</summary>
    private const int ShedMultiple = 3;

    /// <summary>
    ///     The largest draw the latency model will honor, so the tail is long rather than unbounded.
    ///     One call in ten thousand lands here.
    /// </summary>
    private const double Ceiling = 0.9999;

    /// <summary>The odds ratio at the 99th percentile: the square root of 99, computed once.</summary>
    private static readonly double Odds99 = Math.Sqrt(99);

    private Impairment[] _impairments = [];

    private Dependency()
    {
    }

    /// <summary>
    ///     How many calls the dependency serves at once before it starts queueing. Zero, the default,
    ///     is a dependency with no capacity bound - which is the right model when what you are
    ///     measuring is your own policy rather than the thing it calls.
    ///     <para>
    ///         Set it with <see cref="Capacity" />. Past this many in flight the dependency slows in
    ///         proportion to how far past it is, and past three times it fails immediately, which is
    ///         what a server shedding load looks like from the client.
    ///     </para>
    /// </summary>
    public int Concurrency { get; private init; }

    /// <summary>The fraction of calls that fail even while nothing is wrong, from 0 to 1. Set it with <see cref="Failing" />.</summary>
    public double FailureRate { get; private init; }

    /// <summary>The median response time while the dependency is healthy.</summary>
    public TimeSpan P50 { get; private init; }

    /// <summary>The 99th-percentile response time while the dependency is healthy.</summary>
    public TimeSpan P99 { get; private init; }

    /// <summary>
    ///     A dependency that answers every call, with the latency spread these two quantiles describe.
    ///     <para>
    ///         The two numbers fit a log-symmetric curve, so the sampled latencies hit both quantiles
    ///         exactly, the tail has the shape a real dependency's does, and the first percentile sits
    ///         as far below the median as the 99th sits above it. Passing the same value twice gives a
    ///         dependency with no spread at all, which is occasionally what a test wants.
    ///     </para>
    /// </summary>
    /// <param name="p50">The median response time. Must be positive.</param>
    /// <param name="p99">The 99th-percentile response time. Must be at least <paramref name="p50" />.</param>
    /// <returns>The dependency.</returns>
    public static Dependency Healthy(TimeSpan p50, TimeSpan p99) => new() { P50 = p50, P99 = p99 };

    /// <summary>
    ///     Bounds how many calls the dependency serves at once. Past the bound it queues, and past three
    ///     times it sheds - so this is the term that makes a retry storm cost something, and without it
    ///     <see cref="Load.Peers" /> has nothing to act on.
    /// </summary>
    /// <param name="concurrent">Calls served at once before queueing begins. Must be positive.</param>
    /// <returns>A new dependency. The receiver is unchanged.</returns>
    public Dependency Capacity(int concurrent) => this with { Concurrency = concurrent };

    /// <summary>
    ///     A stretch during which the dependency is slower but still answering. The shape most incidents
    ///     actually take, and the one worth simulating: a dependency that is down trips the breaker
    ///     immediately, and a dependency that is eight times slower is the one that fills your attempt
    ///     budget while every individual call still succeeds.
    /// </summary>
    /// <param name="after">How far into the run the brownout starts.</param>
    /// <param name="slower">How many times slower the dependency is. Must be at least 1.</param>
    /// <param name="lasting">How long it lasts. Must be positive.</param>
    /// <returns>A new dependency. The receiver is unchanged.</returns>
    public Dependency Brownout(TimeSpan after, double slower, TimeSpan lasting) =>
        With(new Impairment(after, lasting, slower, Fails: false));

    /// <summary>The fraction of calls that fail while the dependency is otherwise healthy, from 0 to 1.</summary>
    /// <param name="rate">The failure rate.</param>
    /// <returns>A new dependency. The receiver is unchanged.</returns>
    /// <remarks>
    ///     A failure is an <see cref="IOException" />, which both <see cref="Classifier.Default" /> and
    ///     <see cref="Classifier.Http" /> call <see cref="VerdictKind.Transient" /> - so a simulated
    ///     failure is retried exactly as a real transport failure would be.
    /// </remarks>
    public Dependency Failing(double rate) => this with { FailureRate = rate };

    /// <summary>
    ///     A stretch during which every call fails immediately. What a dependency that is refusing
    ///     connections looks like from the client.
    /// </summary>
    /// <param name="after">How far into the run the outage starts.</param>
    /// <param name="lasting">How long it lasts. Must be positive.</param>
    /// <returns>A new dependency. The receiver is unchanged.</returns>
    public Dependency Outage(TimeSpan after, TimeSpan lasting) =>
        With(new Impairment(after, lasting, Slower: 1, Fails: true));

    /// <summary>
    ///     Checks the model and throws <see cref="ResilienceConfigurationException" /> listing every
    ///     problem at once, the same way <see cref="Resilience.Validate" /> does.
    /// </summary>
    /// <exception cref="ResilienceConfigurationException">The dependency cannot be simulated.</exception>
    public void Validate()
    {
        var problems = new List<string>();

        if (P50 <= TimeSpan.Zero)
            problems.Add($"P50 must be positive; it is {P50}.");

        if (P99 < P50)
            problems.Add($"P99 must be at least P50; P50 is {P50} and P99 is {P99}.");

        if (double.IsNaN(FailureRate) || FailureRate < 0 || FailureRate > 1)
            problems.Add($"FailureRate must be between 0 and 1; it is {FailureRate}.");

        if (Concurrency < 0)
            problems.Add($"Concurrency must not be negative; it is {Concurrency}.");

        foreach (var impairment in _impairments)
        {
            var name = impairment.Fails ? "Outage" : "Brownout";

            if (impairment.After < TimeSpan.Zero)
                problems.Add($"{name} must not start before the run does; it starts at {impairment.After}.");

            if (impairment.Lasting <= TimeSpan.Zero)
                problems.Add($"{name} must last a positive time; it lasts {impairment.Lasting}.");

            if (!impairment.Fails && !(impairment.Slower >= 1))
                problems.Add($"Brownout must be at least 1 times slower; it is {impairment.Slower}.");
        }

        if (problems.Count > 0)
            throw new ResilienceConfigurationException(problems);
    }

    /// <summary>Runs <see cref="Validate" /> and returns this dependency, so a bad one throws where it is written.</summary>
    /// <returns>This dependency.</returns>
    /// <exception cref="ResilienceConfigurationException">The dependency cannot be simulated.</exception>
    public Dependency Validated()
    {
        Validate();
        return this;
    }

    /// <summary>When the last impairment ends, which is where <see cref="SimulationReport.TimeToRecover" /> counts from.</summary>
    internal TimeSpan? ImpairedUntil
    {
        get
        {
            var last = default(TimeSpan?);

            foreach (var impairment in _impairments)
            {
                var ends = impairment.After + impairment.Lasting;

                if (last is null || ends > last)
                    last = ends;
            }

            return last;
        }
    }

    /// <summary>
    ///     What one attempt costs and whether it succeeds, given when it starts and how much is already
    ///     in flight. Draws exactly two numbers from <paramref name="dice" /> whatever it decides, so the
    ///     stream does not shift when the dependency's health does.
    /// </summary>
    /// <param name="at">How far into the run the attempt starts.</param>
    /// <param name="offered">Calls in flight at the dependency, including this one and the peers' share.</param>
    /// <param name="dice">The seeded stream.</param>
    /// <returns>The latency to serve and whether the attempt fails at the end of it.</returns>
    internal (TimeSpan Latency, bool Fails) Serve(TimeSpan at, int offered, ChaosDice dice)
    {
        var draw = dice.Next();
        var roll = dice.Next();

        var slower = 1.0;

        foreach (var impairment in _impairments)
        {
            if (at < impairment.After || at >= impairment.After + impairment.Lasting)
                continue;

            if (impairment.Fails)
                return (TimeSpan.Zero, true);

            slower *= impairment.Slower;
        }

        // Past capacity the dependency queues in proportion to how far past it is, and past the shed
        // multiple it gives up without spending any time on the call. The same shape the ramped
        // breaker's simulation uses, so the two models cannot disagree about what overload means.
        if (Concurrency > 0 && offered > Concurrency)
        {
            if (offered > ShedMultiple * Concurrency)
                return (TimeSpan.Zero, true);

            slower *= (double)offered / Concurrency;
        }

        var ticks = P50.Ticks * Spread(draw) * slower;

        return (TimeSpan.FromTicks((long)Math.Min(ticks, long.MaxValue)), roll < FailureRate);
    }

    /// <summary>
    ///     How many times the median a draw is worth: 1 at the median, <see cref="P99" /> over
    ///     <see cref="P50" /> at the 99th percentile, and the reciprocal of that at the first.
    /// </summary>
    /// <remarks>
    ///     Written in nothing but addition, multiplication, division and a square root - every one of
    ///     which IEEE-754 specifies to the last bit. An exponential or a logarithm would be the
    ///     textbook way to shape this curve and would cost the whole guarantee: those come from the
    ///     platform's math library, which is free to round the last bit differently on a different
    ///     operating system or processor, and a seed that produced a different report on a different
    ///     machine would be no guarantee at all.
    /// </remarks>
    /// <param name="draw">A uniform draw in [0, 1).</param>
    /// <returns>The multiple of <see cref="P50" /> to serve.</returns>
    private double Spread(double draw)
    {
        if (P99 <= P50)
            return 1;

        var u = draw < Ceiling ? draw : Ceiling;

        // Odds, square-rooted: 1 at the median, sqrt(99) at the 99th percentile, and the reciprocal
        // of itself at the mirror-image draw - which is what makes the curve log-symmetric.
        var odds = Math.Sqrt(u / (1 - u));
        var ratio = (double)P99.Ticks / P50.Ticks;
        var scale = (ratio - 1) / (Odds99 - 1);

        return odds >= 1 ? 1 + ((odds - 1) * scale) : 1 / (1 + (((1 / odds) - 1) * scale));
    }

    private Dependency With(Impairment impairment) => this with { _impairments = [.. _impairments, impairment] };

    /// <summary>One stretch of the run during which the dependency is not itself.</summary>
    private readonly record struct Impairment(TimeSpan After, TimeSpan Lasting, double Slower, bool Fails);
}
