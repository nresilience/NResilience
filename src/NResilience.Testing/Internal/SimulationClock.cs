namespace NResilience.Testing.Internal;

/// <summary>
///     Virtual time for a simulation: a clock that only moves when the driver moves it, and a timer
///     queue that fires in a fixed order.
///     <para>
///         Nothing here sleeps. The driver advances to the next due timer, fires it on its own thread,
///         and the continuation waiting on that timer runs inline - so five simulated minutes cost
///         whatever the arithmetic costs and no more.
///     </para>
/// </summary>
/// <remarks>
///     Deliberately single-threaded and deliberately without a <see cref="SynchronizationContext" />.
///     The executor awaits with <c>ConfigureAwait(false)</c> throughout, which runs a continuation
///     inline on the thread that completed the task when - and only when - that thread has the default
///     context and the default scheduler. Installing a context here would push every continuation onto
///     the thread pool and take the ordering, and therefore the determinism, with it.
/// </remarks>
internal sealed class SimulationClock : TimeProvider
{
    /// <summary>
    ///     A fixed origin rather than the real date, so a report that prints a wall-clock time prints
    ///     the same one on every machine and in every year.
    /// </summary>
    private static readonly DateTimeOffset Origin = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly int _driver = Environment.CurrentManagedThreadId;

    private readonly PriorityQueue<Entry, (long Due, long Order)> _timers = new();

    private long _now;

    private long _order;

    /// <summary>
    ///     Called with the new time every time the clock moves, before anything due at it runs. How a
    ///     modeled <see cref="Pool" /> keeps the reading the executor is about to take in step with the
    ///     virtual clock.
    ///     <para>
    ///         A hook rather than something the driver does between advances, because
    ///         <see cref="AdvanceTo" /> fires timers on the way to its target and the continuations
    ///         waiting on them start attempts. A reading refreshed only at arrivals would be stale for
    ///         every retry and every hedge - which is to say, for exactly the attempts a saturation
    ///         episode is about.
    ///     </para>
    /// </summary>
    internal Action<long>? OnAdvance { get; set; }

    /// <summary>Ticks since the start of the run.</summary>
    internal long Now => _now;

    /// <summary>When the next timer is due, in ticks since the start of the run, or null when none is.</summary>
    internal long? NextDue => _timers.TryPeek(out _, out var priority) ? priority.Due : null;

    public override DateTimeOffset GetUtcNow() => Origin.AddTicks(_now);

    public override long GetTimestamp() => _now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        return new SimulationTimer(this, callback, state, dueTime, period);
    }

    /// <summary>
    ///     Moves to <paramref name="target" />, firing everything due on the way in due order and
    ///     re-draining after each round - a continuation that schedules a zero-delay timer has to run
    ///     before the clock moves past it, or the run is not the run the same code would make in real
    ///     time.
    /// </summary>
    /// <param name="target">Ticks since the start of the run. Never earlier than <see cref="Now" />.</param>
    internal void AdvanceTo(long target)
    {
        while (_timers.TryPeek(out _, out var priority) && priority.Due <= target)
        {
            if (priority.Due > _now)
            {
                _now = priority.Due;
                OnAdvance?.Invoke(_now);
            }

            var entry = _timers.Dequeue();
            entry.Fire();
        }

        if (target > _now)
        {
            _now = target;
            OnAdvance?.Invoke(_now);
        }
    }

    /// <summary>
    ///     Pauses for <paramref name="delay" /> on virtual time, coming back early when the token
    ///     cancels. The answer is whether the full delay elapsed.
    ///     <para>
    ///         This is what a simulated dependency sleeps on rather than
    ///         <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)" />, and the difference
    ///         is the whole reason a simulation is reproducible. A task completed from inside a
    ///         cancellation callback does not run its continuations inline - the runtime queues them to
    ///         the thread pool - so a cancelled delay would resume the executor on a pool thread, and
    ///         from there every draw of jitter and every timer would be racing the driver. Cancellation
    ///         here schedules a timer instead of completing anything, so the completion happens where
    ///         every other completion does: inside <see cref="AdvanceTo" />, on the driver thread.
    ///     </para>
    /// </summary>
    /// <param name="delay">How long to pause for.</param>
    /// <param name="cancellationToken">The token that can cut it short.</param>
    /// <returns>True when the full delay elapsed; false when the token cancelled it.</returns>
    internal Task<bool> Sleep(TimeSpan delay, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>();

        var elapsed = CreateTimer(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), completion, delay,
            Timeout.InfiniteTimeSpan);

        if (!cancellationToken.CanBeCanceled)
            return completion.Task;

        var registration = cancellationToken.Register(
            static state =>
            {
                var (clock, source) = ((SimulationClock, TaskCompletionSource<bool>))state!;

                clock.CreateTimer(static inner => ((TaskCompletionSource<bool>)inner!).TrySetResult(false), source, TimeSpan.Zero,
                    Timeout.InfiniteTimeSpan);
            },
            (this, completion));

        return Finish(completion.Task, elapsed, registration);

        static async Task<bool> Finish(Task<bool> pending, ITimer timer, CancellationTokenRegistration registration)
        {
            try
            {
                return await pending.ConfigureAwait(false);
            }
            finally
            {
                registration.Dispose();
                timer.Dispose();
            }
        }
    }

    /// <summary>Queues a timer's next firing. Called by the timer itself, on the driver thread.</summary>
    private void Schedule(SimulationTimer timer, long generation, TimeSpan delay)
    {
        if (Environment.CurrentManagedThreadId != _driver)
        {
            throw new InvalidOperationException(
                "A simulated call scheduled a timer off the driver thread, so this run is neither ordered nor "
                + "reproducible. Something on the path read a clock or resumed a continuation the simulator does not own.");
        }

        var due = _now + (delay > TimeSpan.Zero ? delay.Ticks : 0);

        _timers.Enqueue(new Entry(timer, generation), (due, _order++));
    }

    /// <summary>
    ///     One queued firing. The generation is how a rescheduled or disposed timer's stale entries are
    ///     dropped - a <see cref="PriorityQueue{TElement,TPriority}" /> cannot remove, and a simulation
    ///     reschedules constantly.
    /// </summary>
    private readonly record struct Entry(SimulationTimer Timer, long Generation)
    {
        internal void Fire()
        {
            if (Timer.Generation == Generation)
                Timer.Fire();
        }
    }

    private sealed class SimulationTimer : ITimer
    {
        private readonly TimerCallback _callback;

        private readonly SimulationClock _clock;

        private readonly object? _state;

        private TimeSpan _period;

        internal SimulationTimer(SimulationClock clock, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _clock = clock;
            _callback = callback;
            _state = state;

            Change(dueTime, period);
        }

        /// <summary>Bumped by every <see cref="Change" /> and by <see cref="Dispose" />, invalidating queued entries.</summary>
        internal long Generation { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Generation++;
            _period = period;

            if (dueTime != Timeout.InfiniteTimeSpan)
                _clock.Schedule(this, Generation, dueTime);

            return true;
        }

        public void Dispose() => Generation++;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }

        internal void Fire()
        {
            if (_period != Timeout.InfiniteTimeSpan && _period > TimeSpan.Zero)
                _clock.Schedule(this, Generation, _period);

            _callback(_state);
        }
    }
}
