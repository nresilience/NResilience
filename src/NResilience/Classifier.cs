using System.Net.Sockets;
using System.Text;

namespace NResilience;

/// <summary>
///     Says once what counts as a failure. Retry, backoff selection, the attempt log and the breaker
///     and the budget all read the same answer, so there is no way for them
///     to disagree.
///     <para>
///         Predicates are synchronous, because a classification is a type test or a status-code
///         comparison. Work that genuinely needs I/O to classify a result belongs in the callback.
///     </para>
///     <para>
///         Instances are immutable. <see cref="On{TException}(Verdict)" /> and
///         <see cref="OnResult{T}(Func{T, Verdict})" /> return a new classifier, so the shipped statics
///         can never be mutated by a caller deriving from them.
///     </para>
/// </summary>
public sealed class Classifier
{
    private static readonly ExceptionRule[] DefaultRules =
    [
        ExceptionRule.Fixed(typeof(TimeoutException), Verdict.Transient, nameof(TimeoutException)),
        ExceptionRule.Fixed(typeof(IOException), Verdict.Transient, nameof(IOException)),
        ExceptionRule.Fixed(typeof(SocketException), Verdict.Transient, nameof(SocketException)),
    ];

    private readonly ExceptionRule[] _exceptionRules;
    private readonly string _name;
    private readonly ResultRule[] _resultRules;
    private readonly Verdict _unrecognized;

    private Classifier(ExceptionRule[] exceptionRules, ResultRule[] resultRules, Verdict unrecognized, string name)
    {
        _exceptionRules = exceptionRules;
        _resultRules = resultRules;
        _unrecognized = unrecognized;
        _name = name;
    }

    /// <summary>
    ///     Curated exception rules - <see cref="TimeoutException" />, <see cref="IOException" /> and
    ///     <see cref="SocketException" /> are transient - and
    ///     <b>
    ///         anything unrecognized is
    ///         <see cref="VerdictKind.Permanent" />
    ///     </b>
    ///     .
    ///     <para>
    ///         Retrying a programming error is worse than not retrying it: it converts a fast, clear
    ///         failure into a slow, confusing one and hides the bug. The cost is that a genuinely
    ///         transient exception of your own needs one line -
    ///         <c>Classifier.Default.On&lt;MyDbException&gt;(Verdict.Transient)</c>.
    ///     </para>
    /// </summary>
    public static Classifier Default { get; } = new(DefaultRules, [], Verdict.Permanent, "Default");

    /// <summary>
    ///     <see cref="Default" /> plus HTTP transport knowledge. Used by <see cref="Resilience.Http" />.
    ///     <para>
    ///         Held behind a nested holder so that an application which never touches HTTP does not root
    ///         <see cref="HttpResponseMessage" /> and its dependencies.
    ///     </para>
    /// </summary>
    public static Classifier Http => HttpHolder.Instance;

    /// <summary>
    ///     No rules at all: every exception is <see cref="VerdictKind.Transient" />. Opt-in, and named
    ///     so that choosing it is a visible decision rather than an accident.
    /// </summary>
    public static Classifier RetryEverything { get; } = new([], [], Verdict.Transient, "RetryEverything");

    /// <summary>Classifies an exception type as a fixed verdict.</summary>
    /// <typeparam name="TException">The exception type, matched including subclasses.</typeparam>
    /// <param name="verdict">The verdict to give it.</param>
    /// <returns>A new classifier. The receiver is unchanged.</returns>
    public Classifier On<TException>(Verdict verdict)
        where TException : Exception
    {
        var rule = ExceptionRule.Fixed(typeof(TException), verdict, typeof(TException).Name);
        return new Classifier(Prepend(_exceptionRules, rule), _resultRules, _unrecognized, DerivedName());
    }

    /// <summary>Classifies an exception type with a predicate that can inspect it.</summary>
    /// <typeparam name="TException">The exception type, matched including subclasses.</typeparam>
    /// <param name="judge">Given the exception, returns its verdict.</param>
    /// <returns>A new classifier. The receiver is unchanged.</returns>
    public Classifier On<TException>(Func<TException, Verdict> judge)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(judge);

        var rule = new ExceptionRule(typeof(TException), ex => judge((TException)ex), typeof(TException).Name);
        return new Classifier(Prepend(_exceptionRules, rule), _resultRules, _unrecognized, DerivedName());
    }

    /// <summary>
    ///     Classifies a returned value. Resolved once per <typeparamref name="T" /> and then cached, so
    ///     the policy itself never needs to be generic.
    ///     <para>
    ///         If no judge is registered for <typeparamref name="T" />, any returned value is a success.
    ///     </para>
    /// </summary>
    /// <typeparam name="T">The result type this rule applies to. Matched exactly, not by assignability.</typeparam>
    /// <param name="judge">Given the value, returns its verdict.</param>
    /// <returns>A new classifier. The receiver is unchanged.</returns>
    public Classifier OnResult<T>(Func<T, Verdict> judge)
    {
        ArgumentNullException.ThrowIfNull(judge);

        var rule = new ResultRule(typeof(T), judge, typeof(T).Name);
        return new Classifier(_exceptionRules, Prepend(_resultRules, rule), _unrecognized, DerivedName());
    }

    /// <summary>
    ///     The verdict for an exception. Rules are evaluated most-recently-added first, so a rule you
    ///     add always beats one it was derived from.
    /// </summary>
    /// <param name="exception">The exception an attempt threw.</param>
    /// <returns>Its verdict, or the unrecognized-exception verdict when no rule matches.</returns>
    public Verdict ClassifyException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var rules = _exceptionRules;

        for (var i = 0; i < rules.Length; i++)
        {
            if (rules[i].ExceptionType.IsInstanceOfType(exception))
                return rules[i].Judge(exception);
        }

        return _unrecognized;
    }

    /// <summary>
    ///     The verdict for a returned value. Free when no result rules are configured, which is the
    ///     case for every classifier except <see cref="Http" /> and ones derived from it.
    /// </summary>
    /// <typeparam name="T">The static result type of the call.</typeparam>
    /// <param name="value">The value the callback returned.</param>
    /// <returns>Its verdict, or <see cref="Verdict.Ok" /> when nothing is registered for <typeparamref name="T" />.</returns>
    public Verdict ClassifyResult<T>(T value)
    {
        if (_resultRules.Length == 0)
            return Verdict.Ok;

        // A single-entry cache per result type, guarded by an owner check. The static slot is
        // shared across all classifiers for a given T, so the Owner field on the entry is what
        // makes it correct: a hit from a different classifier is a miss, and the slow path
        // overwrites the slot with this classifier's own judge. typeof(T) is a JIT constant, so
        // the steady-state cost for a monomorphic call site is one static read and one reference
        // comparison. Alternating two classifiers for the same T thrashes the slot and falls to
        // the slow path on every call, which is correct but not free.
        var cached = ResultJudge<T>.Cache;

        if (cached is not null && ReferenceEquals(cached.Owner, this))
            return cached.Judge is null ? Verdict.Ok : cached.Judge(value);

        return ClassifyResultSlow(value);
    }

    /// <summary>
    ///     Every rule, in the order they are evaluated. Answers "what will this actually retry?"
    ///     without reading the library's source.
    /// </summary>
    /// <returns>A multi-line dump of the ruleset.</returns>
    public override string ToString()
    {
        var text = new StringBuilder();
        text.Append("Classifier ").Append(_name).Append(':').Append('\n');

        foreach (var rule in _exceptionRules)
        {
            text.Append("  exception ").Append(rule.Description).Append(" -> ").Append(rule.Constant?.ToString() ?? "(predicate)").Append('\n');
        }

        foreach (var rule in _resultRules)
        {
            text.Append("  result ").Append(rule.Description).Append(" -> (predicate)").Append('\n');
        }

        text.Append("  any other exception -> ").Append(_unrecognized.Kind);
        text.Append('\n').Append("  any other result -> Ok");
        return text.ToString();
    }

    private Verdict ClassifyResultSlow<T>(T value)
    {
        Func<T, Verdict>? judge = null;
        var rules = _resultRules;

        for (var i = 0; i < rules.Length; i++)
        {
            if (rules[i].ResultType == typeof(T))
            {
                judge = (Func<T, Verdict>)rules[i].Judge;
                break;
            }
        }

        ResultJudge<T>.Cache = new ResultJudge<T>.Entry(this, judge);
        return judge is null ? Verdict.Ok : judge(value);
    }

    private string DerivedName() => _name.EndsWith('+') ? _name : _name + "+";

    private static T[] Prepend<T>(T[] existing, T item)
    {
        var next = new T[existing.Length + 1];
        next[0] = item;
        Array.Copy(existing, 0, next, 1, existing.Length);
        return next;
    }

    private sealed class ExceptionRule(Type exceptionType, Func<Exception, Verdict> judge, string description)
    {
        public Type ExceptionType { get; } = exceptionType;

        public Func<Exception, Verdict> Judge { get; } = judge;

        public string Description { get; } = description;

        /// <summary>Set only by <see cref="Fixed" />, purely so <c>ToString</c> can print it.</summary>
        public Verdict? Constant { get; private init; }

        /// <summary>
        ///     A rule that always gives the same verdict. The judge and the printed constant are
        ///     derived from one value here, so they cannot disagree.
        /// </summary>
        public static ExceptionRule Fixed(Type exceptionType, Verdict verdict, string description) =>
            new(exceptionType, _ => verdict, description) { Constant = verdict };
    }

    private sealed class ResultRule(Type resultType, object judge, string description)
    {
        public Type ResultType { get; } = resultType;

        public object Judge { get; } = judge;

        public string Description { get; } = description;
    }

    /// <summary>
    ///     The per-result-type judge cache. A static of a generic type, so <c>typeof(T)</c> never has
    ///     to be compared at run time and a monomorphic call site reduces to one static read.
    /// </summary>
    private static class ResultJudge<T>
    {
        public static Entry? Cache;

        public sealed class Entry(Classifier owner, Func<T, Verdict>? judge)
        {
            public Classifier Owner { get; } = owner;

            public Func<T, Verdict>? Judge { get; } = judge;
        }
    }

    private static class HttpHolder
    {
        internal static readonly Classifier Instance = BuildHttp();

        private static Classifier BuildHttp() =>
            Default
                .On<HttpRequestException>(Verdict.Transient)
                .OnResult<HttpResponseMessage>(static r => (int)r.StatusCode switch
                {
                    429 => Verdict.Throttled(RetryAfterOf(r)),
                    503 when RetryAfterOf(r) is { } after => Verdict.Throttled(after),
                    >= 500 or 408 => Verdict.Transient,

                    // A 404 is an answer, not a failure. Retrying one is in the most-copied
                    // retry snippet in .NET, and shipping the correct thing in the box is the
                    // only defense against that.
                    _ => Verdict.Ok,
                });

        private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
        {
            var header = response.Headers.RetryAfter;

            if (header is null)
                return null;

            if (header.Delta is { } delta)
                return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;

            if (header.Date is { } date)
            {
                var until = date - DateTimeOffset.UtcNow;
                return until > TimeSpan.Zero ? until : TimeSpan.Zero;
            }

            return null;
        }
    }
}
