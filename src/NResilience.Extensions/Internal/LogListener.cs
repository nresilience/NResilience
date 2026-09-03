using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NResilience.Extensions.Internal;

/// <summary>
///     Turns <see cref="CallEvent" />s into log records.
///     <para>
///         A class rather than a static delegate, because unlike <see cref="ResilienceTelemetry.Listener" />
///         it carries state: an <see cref="ILogger" />, an options object, and the suppression bookkeeping
///         that keeps an open breaker from writing one line per refused request for the whole break
///         duration.
///     </para>
/// </summary>
internal sealed class LogListener
{
    /// <summary>
    ///     The cap on distinct keys in either dictionary. Bounded by construction rather than evicted:
    ///     beyond the cap a new key is treated as already seen, which degrades to quiet rather than to
    ///     unbounded growth. A constant rather than an option, because no journey asks to tune it.
    /// </summary>
    private const int MaxKeys = 64;

    /// <summary>
    ///     How many record IDs the sampling counters cover, starting at
    ///     <see cref="Log.Codes.AttemptSucceeded" />. An ID past the end is never sampled, which is the
    ///     safe direction: a record the counters cannot reach is written rather than dropped.
    /// </summary>
    private const int SamplingSlots = 32;

    /// <summary>How many of each sampled record this listener has seen. Null when sampling is off.</summary>
    private readonly long[]? _counts;

    private readonly ILogger _logger;
    private readonly ResilienceLoggingOptions _options;

    /// <summary>
    ///     Read once at construction rather than per event: a null here is the whole cost of the feature
    ///     for a caller who did not configure it.
    /// </summary>
    private readonly LogSampling? _sampling;

    /// <summary>First-sighting keys for the footgun and unrecognized-exception records.</summary>
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);

    private readonly TimeProvider _time;

    /// <summary>Rejection windows, keyed by policy name and reason - so one bad host does not silence another.</summary>
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

    /// <summary>When the current incident window closes, as a <see cref="TimeProvider.GetTimestamp" /> value.</summary>
    private long _incidentUntil;

    internal LogListener(ILogger logger, ResilienceLoggingOptions options, TimeProvider? time = null)
    {
        _logger = logger;
        _options = options;
        _time = time ?? TimeProvider.System;

        if (options.Sampling is { } sampling)
        {
            var problems = new List<string>();
            sampling.Validate(problems);

            if (problems.Count > 0)
                throw new ResilienceConfigurationException(problems);

            // KeepOneIn of 1 keeps everything, so it costs nothing to notice that here and take the
            // same path as no sampling at all.
            if (sampling.KeepOneIn > 1)
            {
                _sampling = sampling;
                _counts = new long[SamplingSlots];
            }
        }
    }

    /// <summary>The listener, as the delegate <see cref="Resilience.OnEvent" /> takes.</summary>
    internal Action<CallEvent> Record => Write;

    private void Write(CallEvent e)
    {
        var policy = e.PolicyName ?? "(unnamed)";

        switch (e.Kind)
        {
            case CallEventKind.Attempt:
                Attempt(e, policy);
                break;

            case CallEventKind.Retrying:
                if (Level(Log.Ids.Retrying, e) is { } retrying)
                    Log.Retrying(_logger, retrying, Cause(e), policy, Ms(e.Delay), e.AttemptNumber, Name(e.Verdict.Kind));

                break;

            case CallEventKind.Succeeded:
                Succeeded(e, policy);
                break;

            case CallEventKind.NotRetried:
                NotRetried(e, policy);
                break;

            case CallEventKind.RejectedByBreaker:
            case CallEventKind.RejectedByBudget:
                Rejected(e, policy);
                break;

            case CallEventKind.DeadlineExceeded:
                if (Level(Log.Ids.DeadlineExceeded, e) is { } deadline)
                    Log.DeadlineExceeded(_logger, deadline, e.Exception, policy, Ms(e.Duration), e.AttemptNumber);

                break;

            case CallEventKind.Exhausted:
                if (Level(Log.Ids.Exhausted, e) is { } exhausted)
                    Log.Exhausted(_logger, exhausted, e.Exception, policy, e.AttemptNumber, Ms(e.Duration), ErrorType(e));

                break;

            case CallEventKind.BreakerOpened:
                if (Level(Log.Ids.BreakerOpened, e) is { } opened)
                    Log.BreakerOpened(_logger, opened, policy, e.AttemptNumber);

                break;

            case CallEventKind.BreakerHalfOpened:
                if (Level(Log.Ids.BreakerHalfOpened, e) is { } halfOpened)
                    Log.BreakerHalfOpened(_logger, halfOpened, policy);

                break;

            case CallEventKind.BreakerClosed:
                if (Level(Log.Ids.BreakerClosed, e) is { } closed)
                    Log.BreakerClosed(_logger, closed, policy);

                break;

            case CallEventKind.OrphanedWork:
                Footgun(e, policy, Log.Ids.OrphanedWork, Log.Ids.OrphanedWorkRepeat);
                break;

            case CallEventKind.NestedRetry:
                Footgun(e, policy, Log.Ids.NestedRetry, Log.Ids.NestedRetryRepeat);
                break;

            // Traffic, not incidents: a healthy hedging policy does this on a known fraction of its
            // calls by construction. The metrics count them; these records exist so that one call can
            // be followed end to end when somebody is working out why the tail moved.
            case CallEventKind.HedgeStarted:
                if (Level(Log.Ids.HedgeStarted, e) is { } hedging)
                    Log.HedgeStarted(_logger, hedging, policy, e.AttemptNumber, Ms(e.Delay));

                break;

            case CallEventKind.HedgeSuppressed:
                if (Level(Log.Ids.HedgeSuppressed, e) is { } suppressed)
                    Log.HedgeSuppressed(_logger, suppressed, policy, e.AttemptNumber, Ms(e.Delay));

                break;

            case CallEventKind.HedgeWon:
                if (Level(Log.Ids.HedgeWon, e) is { } won)
                    Log.HedgeWon(_logger, won, policy, e.AttemptNumber, Ms(e.Duration));

                break;

            case CallEventKind.HedgeDiscarded:
                if (Level(Log.Ids.HedgeDiscarded, e) is { } discarded)
                    Log.HedgeDiscarded(_logger, discarded, policy, e.AttemptNumber, Ms(e.Duration));

                break;

            // Raised on change rather than per call, so this is already one line per movement of the
            // estimate. No flood control for the same reason the breaker transitions need none.
            case CallEventKind.AttemptTimeoutAdapted:
                if (Level(Log.Ids.AttemptTimeoutAdapted, e) is { } ceiling)
                    Log.AttemptTimeoutAdapted(_logger, ceiling, policy, Ms(e.Delay));

                break;

            // On change too, and here it matters more: this one is raised on the retry decision, so
            // without the change filter it would be a line per retry during the incident.
            case CallEventKind.BackoffBaseAdapted:
                if (Level(Log.Ids.BackoffBaseAdapted, e) is { } backoffBase)
                    Log.BackoffBaseAdapted(_logger, backoffBase, policy, Ms(e.Delay));

                break;
        }
    }

    private void Attempt(CallEvent e, string policy)
    {
        // A self-imposed verdict is a local limiter refusing the attempt before it left the process,
        // which is a different fact from the dependency failing and reads as one.
        if (e.Verdict.SelfImposed)
        {
            if (Level(Log.Ids.AttemptLimited, e) is { } limited)
                Log.AttemptLimited(_logger, limited, policy, e.AttemptNumber);

            return;
        }

        if (e.Verdict.Kind == VerdictKind.Ok && e.Exception is null)
        {
            if (Level(Log.Ids.AttemptSucceeded, e) is { } succeeded)
                Log.AttemptSucceeded(_logger, succeeded, policy, e.AttemptNumber, Ms(e.Duration));

            return;
        }

        if (Level(Log.Ids.AttemptFailed, e) is { } failed)
            Log.AttemptFailed(_logger, failed, Cause(e), policy, e.AttemptNumber, Ms(e.Duration), Name(e.Verdict.Kind), ErrorType(e));
    }

    private void Succeeded(CallEvent e, string policy)
    {
        if (e.AttemptNumber > 1)
        {
            if (Level(Log.Ids.CallSucceededAfterRetries, e) is { } retried)
                Log.CallSucceededAfterRetries(_logger, retried, policy, e.AttemptNumber, Ms(e.Duration));

            return;
        }

        if (Level(Log.Ids.CallSucceeded, e) is { } first)
            Log.CallSucceeded(_logger, first, policy, Ms(e.Duration));
    }

    /// <summary>
    ///     The first time a policy declines to retry a given exception type, that is a candidate
    ///     misconfiguration and worth naming the type. The ten thousandth 404 is not - and a 404 is
    ///     classified from a response, so it arrives with no exception at all and takes the quiet path.
    /// </summary>
    private void NotRetried(CallEvent e, string policy)
    {
        if (e.Exception is { } error
            && Level(Log.Ids.NotRetriedFirstSighting, e) is { } sighting
            && FirstSighting($"{policy}|notretried|{error.GetType().FullName}"))
        {
            Log.NotRetriedFirstSighting(_logger, sighting, error, policy, error.GetType().Name, e.AttemptNumber);
            return;
        }

        if (Level(Log.Ids.NotRetried, e) is { } ordinary)
            Log.NotRetried(_logger, ordinary, e.Exception, policy, e.AttemptNumber);
    }

    /// <summary>
    ///     An open breaker refuses every call for the whole break duration, so the useful record is one
    ///     line saying it is refusing plus a count. Inside the window rejections are demoted to <c>RejectedRepeat</c>
    ///     rather than dropped, and the count reaches the next warning.
    /// </summary>
    private void Rejected(CallEvent e, string policy)
    {
        var id = e.Kind == CallEventKind.RejectedByBudget
            ? Log.Ids.RejectedBudgetExhausted
            : Log.Ids.RejectedDependencyUnavailable;

        // The level is decided before the window is consumed, so a warning nobody is carrying does
        // not silently reset the suppressed count.
        if (Level(id, e) is { } level && ShouldWarn($"{policy}|{id.Id}", out var suppressed))
        {
            if (id.Id == Log.Codes.RejectedBudgetExhausted)
                Log.RejectedBudgetExhausted(_logger, level, policy, suppressed);
            else
                Log.RejectedDependencyUnavailable(_logger, level, policy, suppressed);

            return;
        }

        if (Level(Log.Ids.RejectedRepeat, e) is { } repeat)
            Log.RejectedRepeat(_logger, repeat, policy, Reason(e.Reason));
    }

    /// <summary>
    ///     A configuration mistake rather than an event: loud the first time it is seen for a policy,
    ///     quiet after, because the second one carries no information the first did not.
    /// </summary>
    private void Footgun(CallEvent e, string policy, EventId first, EventId repeat)
    {
        if (Level(first, e) is { } loud && FirstSighting($"{policy}|{first.Id}"))
        {
            if (first.Id == Log.Codes.OrphanedWork)
                Log.OrphanedWork(_logger, loud, policy, e.AttemptNumber);
            else
                Log.NestedRetry(_logger, loud, policy);

            return;
        }

        if (Level(repeat, e) is { } quiet)
        {
            if (repeat.Id == Log.Codes.OrphanedWorkRepeat)
                Log.OrphanedWorkRepeat(_logger, quiet, policy, e.AttemptNumber);
            else
                Log.NestedRetryRepeat(_logger, quiet, policy);
        }
    }

    /// <summary>
    ///     The level this record is emitted at, or null when it is dropped. Null covers both
    ///     <see cref="LogLevel.None" /> from the delegate and a level the provider is not carrying, so a
    ///     disabled record costs one <c>switch</c> and one <see cref="ILogger.IsEnabled" /> call.
    /// </summary>
    private LogLevel? Level(EventId id, CallEvent e)
    {
        var level = _options.Profile switch
        {
            ResilienceLogProfile.Verbose => Verbose(id.Id),
            ResilienceLogProfile.Default => Ordinary(id.Id),
            _ => LogLevel.None,
        };

        if (_options.Level is { } custom)
            level = custom(id, e) ?? level;

        // The window is opened from the event rather than from the written record, and before the
        // enabled check, so an incident whose warning the sink is not carrying still turns sampling
        // off for the minute that follows it.
        if (_sampling is { } sampling && IsIncident(id.Id))
        {
            var window = (long)(sampling.IncidentWindow.TotalSeconds * _time.TimestampFrequency);
            Interlocked.Exchange(ref _incidentUntil, _time.GetTimestamp() + window);
        }

        if (level == LogLevel.None || !_logger.IsEnabled(level))
            return null;

        return _sampling is { } active && !Keep(active, id.Id) ? null : level;
    }

    /// <summary>
    ///     Whether sampling keeps this record. A record that is not proportional to traffic is never
    ///     sampled, and inside an incident window nothing is.
    /// </summary>
    /// <remarks>
    ///     The count is exact rather than random - every <c>KeepOneIn</c>th record, counted per record -
    ///     so a test asserting what a policy logged does not have a seed in it, and two processes at the
    ///     same traffic write the same number of lines. It counts records the sink would have carried
    ///     rather than events, so raising the category filter does not change what survives sampling.
    /// </remarks>
    private bool Keep(in LogSampling sampling, int id)
    {
        var slot = id - Log.Codes.AttemptSucceeded;

        if (!IsTraffic(id) || (uint)slot >= (uint)_counts!.Length)
            return true;

        if (_time.GetTimestamp() < Interlocked.Read(ref _incidentUntil))
            return true;

        var seen = Interlocked.Increment(ref _counts[slot]);

        return seen <= sampling.MinimumSamples || seen % sampling.KeepOneIn == 0;
    }

    /// <summary>
    ///     What opens the incident window: the breaker opening, and either rejection reason. Three IDs
    ///     rather than "everything at <c>Warning</c>", because the other three warnings do not end.
    ///     <see cref="Log.Codes.OrphanedWork" /> and <see cref="Log.Codes.NestedRetry" /> are
    ///     configuration errors that recur on every call, and
    ///     <see cref="Log.Codes.NotRetriedFirstSighting" /> is raised for a dependency answering "no"
    ///     steadily and correctly - each of them would hold the window open for the life of the process
    ///     and turn sampling off without saying so.
    /// </summary>
    private static bool IsIncident(int id) =>
        id is Log.Codes.BreakerOpened
            or Log.Codes.RejectedDependencyUnavailable
            or Log.Codes.RejectedBudgetExhausted;

    /// <summary>
    ///     The records whose volume is proportional to traffic, and so the only ones sampling touches.
    ///     Everything left out is already one line per event rather than one line per call: the
    ///     transitions, the first sightings, the adapted estimates, the rejection warnings the repeat
    ///     window already throttles, and the terminal failures the caller is about to see thrown.
    /// </summary>
    private static bool IsTraffic(int id) => id switch
    {
        Log.Codes.AttemptSucceeded => true,
        Log.Codes.AttemptFailed => true,
        Log.Codes.AttemptLimited => true,
        Log.Codes.Retrying => true,
        Log.Codes.CallSucceeded => true,
        Log.Codes.CallSucceededAfterRetries => true,
        Log.Codes.HedgeStarted => true,
        Log.Codes.HedgeWon => true,
        Log.Codes.HedgeDiscarded => true,
        _ => false,
    };

    /// <summary>
    ///     A record's level is set by what its volume is proportional to: traffic is <c>Trace</c> or
    ///     <c>Debug</c> because the metrics already count it, incidents are <c>Warning</c> because one
    ///     line per incident is what an operator can read, and a caller-visible failure is <c>Debug</c>
    ///     because the caller logs it with the business context this library does not have.
    /// </summary>
    private static LogLevel Ordinary(int id) => id switch
    {
        Log.Codes.AttemptSucceeded => LogLevel.Trace,
        Log.Codes.CallSucceeded => LogLevel.Trace,
        Log.Codes.NestedRetryRepeat => LogLevel.Trace,
        Log.Codes.HedgeStarted => LogLevel.Trace,
        Log.Codes.HedgeWon => LogLevel.Trace,
        Log.Codes.HedgeDiscarded => LogLevel.Trace,

        // Debug rather than Trace, unlike the three records beside it. A hedge that started is
        // ordinary traffic; a hedge held back is this process deciding that a feature the operator
        // turned on has stopped being worth its load, and the operator reading Default should see it.
        Log.Codes.HedgeSuppressed => LogLevel.Debug,
        Log.Codes.PolicyClassifier => LogLevel.Trace,
        Log.Codes.NotRetriedFirstSighting => LogLevel.Warning,
        Log.Codes.RejectedDependencyUnavailable => LogLevel.Warning,
        Log.Codes.RejectedBudgetExhausted => LogLevel.Warning,
        Log.Codes.BreakerOpened => LogLevel.Warning,
        Log.Codes.OrphanedWork => LogLevel.Warning,
        Log.Codes.NestedRetry => LogLevel.Warning,
        Log.Codes.BreakerHalfOpened => LogLevel.Information,
        Log.Codes.BreakerClosed => LogLevel.Information,
        _ => LogLevel.Debug,
    };

    /// <summary>
    ///     Raises the traffic-proportional records to <c>Information</c> and leaves the incident records
    ///     where they are. For the sink that will not carry <c>Debug</c> however the filter is set.
    /// </summary>
    private static LogLevel Verbose(int id) => Ordinary(id) switch
    {
        LogLevel.Trace when id == Log.Codes.PolicyClassifier => LogLevel.Debug,
        LogLevel.Trace => LogLevel.Information,
        LogLevel.Debug => LogLevel.Information,
        LogLevel level => level,
    };

    private bool FirstSighting(string key)
    {
        if (_seen.ContainsKey(key))
            return false;

        return _seen.Count < MaxKeys && _seen.TryAdd(key, 0);
    }

    private bool ShouldWarn(string key, out int suppressed)
    {
        suppressed = 0;

        if (_options.RepeatWindow <= TimeSpan.Zero)
            return true;

        if (!_windows.TryGetValue(key, out var window))
        {
            if (_windows.Count >= MaxKeys)
                return false;

            window = _windows.GetOrAdd(key, static _ => new Window());
        }

        lock (window)
        {
            var now = _time.GetTimestamp();

            if (window.Opened && now < window.NextAt)
            {
                window.Suppressed++;
                return false;
            }

            suppressed = window.Suppressed;
            window.Suppressed = 0;
            window.Opened = true;
            window.NextAt = now + (long)(_options.RepeatWindow.TotalSeconds * _time.TimestampFrequency);
            return true;
        }
    }

    private Exception? Cause(CallEvent e) => _options.IncludeStackTracesOnRetry ? e.Exception : null;

    private static long Ms(TimeSpan duration) => (long)duration.TotalMilliseconds;

    private static long Ms(TimeSpan? duration) => duration is { } value ? Ms(value) : 0;

    /// <summary>
    ///     What failed, as a name. The exception type when there is one; otherwise the type of the
    ///     result the classifier judged, because the HTTP path classifies a response rather than a
    ///     throw and "HttpResponseMessage" is more use than a placeholder. The status code itself is
    ///     deliberately not here - <c>Microsoft.Extensions.Http</c> already logs it, and correlating is
    ///     cheaper than duplicating.
    /// </summary>
    private static string ErrorType(CallEvent e) =>
        e.Exception?.GetType().Name ?? e.Result?.GetType().Name ?? "(no exception)";

    /// <summary>
    ///     Verdict names as constants rather than <c>ToString()</c>, because this is on a per-attempt
    ///     path and <c>Enum.ToString</c> allocates a string each time.
    /// </summary>
    private static string Name(VerdictKind kind) => kind switch
    {
        VerdictKind.Ok => "Ok",
        VerdictKind.Transient => "Transient",
        VerdictKind.Throttled => "Throttled",
        _ => "Permanent",
    };

    private static string Reason(StopReason? reason) => reason switch
    {
        StopReason.BudgetExhausted => "the retry budget is exhausted",
        StopReason.DependencyUnavailable => "its circuit breaker is open",
        _ => "a guard refused it",
    };

    /// <summary>One rejection reason's warning window, and what it has demoted since the last one.</summary>
    private sealed class Window
    {
        public bool Opened { get; set; }

        public long NextAt { get; set; }

        public int Suppressed { get; set; }
    }
}
