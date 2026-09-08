using System.Globalization;

namespace NResilience.Internal;

/// <summary>
///     One host's published allowance: the last numbers it sent, and the answer to "may this attempt
///     go".
///     <para>
///         Updated from every response and read before every send, so the cost is one header lookup on
///         a path that has already parsed the response, and one comparison on a path that is about to
///         open a socket. See <see cref="Quota" /> for the feature and for its failure mode.
///     </para>
/// </summary>
/// <remarks>
///     One instance per host, held by <see cref="HostScope" />, so two hosts have independent
///     allowances and a client talking to fifty of them keeps fifty small readings rather than one
///     confused average.
/// </remarks>
internal sealed class HostQuota
{
    /// <summary>The standard field carrying the remaining allowance and the seconds until it resets.</summary>
    private const string RateLimitHeader = "RateLimit";

    /// <summary>The standard field carrying the quota and the window length.</summary>
    private const string PolicyHeader = "RateLimit-Policy";

    /// <summary>
    ///     Where a legacy reset value stops being a count of seconds and starts being a Unix
    ///     timestamp. Roughly September 2001, so no delta a server could sanely publish reaches it and
    ///     no timestamp a server could sanely publish falls below it.
    /// </summary>
    private const long EpochBoundary = 1_000_000_000;

    /// <summary>What <see cref="Quota.LegacyHeaders" /> means when it is null.</summary>
    private static readonly string[] DefaultLegacyHeaders =
        ["X-RateLimit-Limit", "X-RateLimit-Remaining", "X-RateLimit-Reset"];

    private readonly Action<CallEvent>? _listener;
    private readonly string? _policyName;
    private readonly Quota _quota;
    private readonly TimeProvider _time;

    /// <summary>
    ///     The last reading, or null while the host has published nothing. Swapped whole rather than
    ///     updated field by field: three numbers only mean anything together, and a request reading
    ///     them while a response writes them must not see a remaining count from one window beside a
    ///     reset from the next.
    /// </summary>
    private Window? _window;

    internal HostQuota(Quota quota, string host, TimeProvider time, string? policyName, Action<CallEvent>? listener)
    {
        _quota = quota;
        _time = time;
        _policyName = policyName;
        _listener = listener;
        Limiter = $"{host} quota";
    }

    /// <summary>
    ///     What <see cref="RateLimitedException.Limiter" /> reports, so a caller reading the message
    ///     can tell this guard from a limiter of their own.
    /// </summary>
    internal string Limiter { get; }

    /// <summary>
    ///     Reads whatever this response published. A response that publishes nothing leaves the last
    ///     reading in place, and a malformed field is ignored rather than thrown - a header this
    ///     library cannot parse is not a reason to fail a call that worked.
    /// </summary>
    /// <param name="response">The response the attempt returned.</param>
    internal void Observe(HttpResponseMessage response)
    {
        if (TryReadStandard(response, out var window) || TryReadLegacy(response, out window))
            Volatile.Write(ref _window, window);
    }

    /// <summary>
    ///     How long until the published window resets, when the remaining allowance is inside the
    ///     reserve, or null when the attempt may go.
    /// </summary>
    /// <param name="attemptNumber">The attempt this call was about to make, for the event.</param>
    /// <returns>The pushback to refuse with, or null to admit.</returns>
    /// <remarks>
    ///     Three ways to get null, and all three are the feature being invisible rather than
    ///     permissive: the host has published nothing, the window it published has already reset, or
    ///     there is allowance left above the reserve. A host that published a remaining count without
    ///     a quota to take a fraction of has no denominator for the reserve, so it is refused only
    ///     once the count reaches zero.
    /// </remarks>
    internal TimeSpan? Refusal(int attemptNumber)
    {
        if (Volatile.Read(ref _window) is not { } window)
            return null;

        var now = _time.GetUtcNow();

        if (now >= window.ResetsAt)
            return null;

        var reserved = window.Limit is { } limit ? limit * _quota.Reserve : 0d;

        if (window.Remaining > reserved)
            return null;

        var until = window.ResetsAt - now;

        if (_listener is { } listener)
        {
            listener(new CallEvent(
                CallEventKind.RejectedByQuota, _policyName, attemptNumber, Verdict.Refused(until), TimeSpan.Zero, until, null, null, null));
        }

        return until;
    }

    /// <summary>
    ///     Reads the two structured fields. The most constraining member of <c>RateLimit</c> wins -
    ///     a host publishing a burst policy and a daily one is bound by whichever has least left - and
    ///     its quota comes from the <c>RateLimit-Policy</c> member of the same name.
    /// </summary>
    private bool TryReadStandard(HttpResponseMessage response, out Window? window)
    {
        window = null;

        if (!response.Headers.TryGetValues(RateLimitHeader, out var values))
            return false;

        var remaining = long.MaxValue;
        long reset = 0;
        string? name = null;

        foreach (var value in values)
        {
            foreach (var member in Members(value))
            {
                // Both or neither: a remaining count with no reset says nothing about when the
                // allowance comes back, and this guard's whole answer is a time.
                if (!TryParameter(member, 'r', out var left) || !TryParameter(member, 't', out var seconds))
                    continue;

                if (left < 0 || seconds < 0 || left >= remaining)
                    continue;

                remaining = left;
                reset = seconds;
                name = NameOf(member).ToString();
            }
        }

        if (name is null)
            return false;

        window = new Window(remaining, QuotaFor(response, name), _time.GetUtcNow().AddSeconds(reset));

        return true;
    }

    /// <summary>Reads the legacy triple, in the order <see cref="Quota.LegacyHeaders" /> documents.</summary>
    private bool TryReadLegacy(HttpResponseMessage response, out Window? window)
    {
        window = null;

        var names = _quota.LegacyHeaders ?? DefaultLegacyHeaders;

        // Validated at construction, and re-checked because the array is the caller's and nothing
        // stops them shortening it afterwards.
        if (names.Length != 3)
            return false;

        if (!TryReadNumber(response, names[1], out var remaining) || remaining < 0)
            return false;

        if (!TryReadNumber(response, names[2], out var reset) || reset < 0)
            return false;

        var limit = TryReadNumber(response, names[0], out var declared) && declared > 0 ? declared : (long?)null;

        var resetsAt = reset >= EpochBoundary
            ? DateTimeOffset.UnixEpoch.AddSeconds(reset)
            : _time.GetUtcNow().AddSeconds(reset);

        window = new Window(remaining, limit, resetsAt);

        return true;
    }

    /// <summary>The quota of the named policy, or null when the host published none.</summary>
    private static long? QuotaFor(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(PolicyHeader, out var values))
            return null;

        foreach (var value in values)
        {
            foreach (var member in Members(value))
            {
                if (!TryParameter(member, 'q', out var quota) || quota <= 0)
                    continue;

                if (NameOf(member).Equals(name, StringComparison.Ordinal))
                    return quota;
            }
        }

        return null;
    }

    /// <summary>The first value of a header that parses as a whole number.</summary>
    private static bool TryReadNumber(HttpResponseMessage response, string header, out long number)
    {
        number = 0;

        if (!response.Headers.TryGetValues(header, out var values))
            return false;

        foreach (var value in values)
        {
            if (long.TryParse(value.AsSpan().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     The members of a structured-field list, split on the commas that are not inside a quoted
    ///     string.
    /// </summary>
    private static MemberEnumerator Members(string value) => new(value);

    /// <summary>The item's name: everything before the first parameter, unquoted.</summary>
    private static ReadOnlySpan<char> NameOf(ReadOnlySpan<char> member)
    {
        var end = member.IndexOf(';');

        return (end < 0 ? member : member[..end]).Trim().Trim('"');
    }

    /// <summary>
    ///     The value of one of an item's parameters, as a whole number.
    /// </summary>
    /// <remarks>
    ///     A scan per key rather than a parsed parameter list, because each field here is read for at
    ///     most two keys and a dictionary would allocate for every response.
    /// </remarks>
    private static bool TryParameter(ReadOnlySpan<char> member, char key, out long value)
    {
        value = 0;

        var rest = member;

        while (true)
        {
            var separator = rest.IndexOf(';');

            if (separator < 0)
                return false;

            rest = rest[(separator + 1)..];

            var end = rest.IndexOf(';');
            var parameter = (end < 0 ? rest : rest[..end]).Trim();

            if (parameter.Length > 1 && parameter[0] == key && parameter[1] == '=')
            {
                return long.TryParse(parameter[2..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            }
        }
    }

    /// <summary>One reading, as the host published it.</summary>
    /// <param name="Remaining">How much allowance is left.</param>
    /// <param name="Limit">The published quota, or null when the host sent a remaining count without one.</param>
    /// <param name="ResetsAt">When the allowance comes back.</param>
    private sealed record Window(long Remaining, long? Limit, DateTimeOffset ResetsAt);

    /// <summary>
    ///     Splits a structured-field list into members without allocating. A <c>ref struct</c>
    ///     carrying its own <c>GetEnumerator</c>, so <c>foreach</c> binds to it directly - an iterator
    ///     method cannot yield a <see cref="ReadOnlySpan{T}" />, and this runs on every response that
    ///     publishes a quota.
    /// </summary>
    private ref struct MemberEnumerator(string value)
    {
        private int _index;

        public ReadOnlySpan<char> Current { get; private set; }

        public MemberEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            var span = value.AsSpan();

            if (_index > span.Length)
                return false;

            var start = _index;
            var quoted = false;

            for (var i = start; i < span.Length; i++)
            {
                if (span[i] == '"')
                    quoted = !quoted;
                else if (span[i] == ',' && !quoted)
                {
                    Current = span[start..i];
                    _index = i + 1;

                    return true;
                }
            }

            Current = span[start..];
            _index = span.Length + 1;

            return Current.Length > 0;
        }
    }
}
