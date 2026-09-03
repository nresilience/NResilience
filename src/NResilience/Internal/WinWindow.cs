namespace NResilience.Internal;

/// <summary>
///     The control loop behind <see cref="WinRate" />: a sliding count of hedges started and hedges won,
///     the allowance that count moves, and the deficit accounting that turns an allowance into a
///     decision about one hedge.
/// </summary>
/// <remarks>
///     <para>
///         Four rings over the window, the same division <see cref="LatencyWindow" /> uses, which makes
///         a ring a quarter of the window and a ring boundary the loop's decision point. The rings are
///         cleared when they are next written rather than on a timer, so a policy that stops hedging
///         stops costing anything, and a window whose counts are a revolution old reports nothing rather
///         than something stale.
///     </para>
///     <para>
///         Guarded by one lock rather than interlocked like <see cref="LatencyWindow" />, because unlike
///         a quantile estimate this thing takes a decision: the allowance, the credit and the counts have
///         to move together or two threads racing at a ring boundary can both retreat on the same
///         evidence. The lock is taken only on the hedged path of a policy that configured
///         <see cref="WinRate" />, which is <c>1 - Quantile</c> of that policy's calls.
///     </para>
/// </remarks>
internal sealed class WinWindow
{
    /// <summary>Slices the window is divided into, and so how often the loop takes a decision.</summary>
    private const int Rings = 4;

    private readonly WinRate _feedback;
    private readonly object _gate = new();
    private readonly long[] _ringEpochs = new long[Rings];
    private readonly int[] _started = new int[Rings];
    private readonly long _startedAt;
    private readonly long _ticksPerRing;
    private readonly TimeProvider _time;
    private readonly int[] _won = new int[Rings];

    /// <summary>The fraction of would-be hedges currently admitted. Starts at 1: a cold loop hedges as configured.</summary>
    private double _allowance = 1;

    /// <summary>Fractional admissions carried between decisions, so an allowance of 0.25 admits every fourth hedge rather than a random quarter.</summary>
    private double _credit;

    /// <summary>The ring boundary the last decision was taken at. Below any real epoch until one has been.</summary>
    private long _decidedAt = long.MinValue;

    /// <summary>Creates a window.</summary>
    /// <param name="feedback">The configuration. Already validated by the policy.</param>
    /// <param name="time">The clock.</param>
    internal WinWindow(WinRate feedback, TimeProvider time)
    {
        _feedback = feedback;
        _time = time;
        _startedAt = time.GetTimestamp();
        _ticksPerRing = Math.Max(feedback.Window.Ticks / Rings, 1);

        for (var slot = 0; slot < Rings; slot++)
        {
            // Far enough in the past that no real epoch can be mistaken for it, and near enough that
            // subtracting it from one cannot overflow.
            _ringEpochs[slot] = -1 - Rings;
        }
    }

    /// <summary>
    ///     The fraction of would-be hedges currently admitted, after bringing the loop up to date.
    ///     Exists for tests and diagnostics; the executor goes through <see cref="Admits" />.
    /// </summary>
    internal double Allowance
    {
        get
        {
            lock (_gate)
            {
                Decide(Epoch());

                return _allowance;
            }
        }
    }

    /// <summary>
    ///     Whether the hedge whose threshold has just elapsed may start, taking the loop's decision if a
    ///     ring boundary has passed since the last one.
    /// </summary>
    /// <returns>True when this hedge is one of the admitted fraction.</returns>
    /// <remarks>
    ///     Deficit accounting rather than a coin flip, for the reason <see cref="Breaker" />'s recovery
    ///     ramp gives: the admitted fraction comes out evenly spaced, and a simulation of this loop runs
    ///     the same way twice. Nothing is counted here - a hedge the retry budget goes on to refuse never
    ///     reached the dependency and is not evidence about anything, so <see cref="Started" /> is what
    ///     the executor calls once a leg is actually running.
    /// </remarks>
    internal bool Admits()
    {
        lock (_gate)
        {
            Decide(Epoch());

            _credit += _allowance;

            if (_credit < 1)
                return false;

            _credit -= 1;

            return true;
        }
    }

    /// <summary>Counts a hedge that actually started: the denominator of the win rate.</summary>
    internal void Started()
    {
        lock (_gate)
        {
            var epoch = Epoch();
            Claim(epoch);
            _started[(int)(epoch & (Rings - 1))]++;
        }
    }

    /// <summary>Counts a hedge that produced the answer: the numerator.</summary>
    internal void Won()
    {
        lock (_gate)
        {
            var epoch = Epoch();
            Claim(epoch);
            _won[(int)(epoch & (Rings - 1))]++;
        }
    }

    private long Epoch() => _time.GetElapsedTime(_startedAt).Ticks / _ticksPerRing;

    /// <summary>
    ///     Takes over the ring this epoch writes to, clearing whatever revolution's counts it still
    ///     held. Always called with the lock held.
    /// </summary>
    private void Claim(long epoch)
    {
        var slot = (int)(epoch & (Rings - 1));

        if (_ringEpochs[slot] == epoch)
            return;

        _started[slot] = 0;
        _won[slot] = 0;
        _ringEpochs[slot] = epoch;
    }

    /// <summary>
    ///     Moves the allowance if a ring boundary has passed since the last decision. Always called with
    ///     the lock held.
    /// </summary>
    /// <param name="epoch">The current epoch.</param>
    /// <remarks>
    ///     <para>
    ///         The three cases are the whole loop. Enough hedges and too few wins: retreat, halving the
    ///         allowance. Enough hedges and enough wins: return one step. Not enough hedges to say:
    ///         return one step per boundary passed, which is both the cold-start rule and the only way
    ///         back from a retreat deep enough to have starved the window of evidence.
    ///     </para>
    ///     <para>
    ///         The evidence overlaps between decisions - the window is four rings and a decision happens
    ///         every ring - so a genuinely losing minute retreats four times rather than once. That is
    ///         the intent: <see cref="WinRate.MinimumAllowance" /> bounds where it can get to, and
    ///         reaching it within a window is the point of a multiplicative retreat.
    ///     </para>
    /// </remarks>
    private void Decide(long epoch)
    {
        if (epoch == _decidedAt)
            return;

        var slices = _decidedAt == long.MinValue ? 1 : epoch - _decidedAt;
        _decidedAt = epoch;

        var started = 0;
        var won = 0;

        for (var slot = 0; slot < Rings; slot++)
        {
            var stamped = _ringEpochs[slot];

            // A ring stamped outside the last Rings epochs holds counts from a previous revolution.
            if (stamped > epoch || epoch - stamped >= Rings)
                continue;

            started += _started[slot];
            won += _won[slot];
        }

        if (started < _feedback.MinimumSamples)
        {
            _allowance = _feedback.Relaxed(_allowance, slices);
            return;
        }

        _allowance = won < _feedback.Minimum * started
            ? _feedback.Retreated(_allowance)
            : _feedback.Relaxed(_allowance, 1);
    }
}
