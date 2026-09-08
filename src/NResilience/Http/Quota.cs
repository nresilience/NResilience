namespace NResilience;

/// <summary>
///     The allowance the dependency publishes, honored before it has to refuse you.
///     <para>
///         Most large APIs say how much quota you have, how much is left, and when the window resets -
///         on every response, not just the 429. The handler reads those headers, keeps the numbers per
///         host, and refuses an attempt locally once the remaining allowance is inside
///         <see cref="Reserve" />. Nothing leaves the process, so nothing is charged to the retry
///         budget and nothing is counted against the host's breaker.
///     </para>
///     <para>
///         The operator supplies no rate at all: the dependency supplies it. That is what separates
///         this from a limiter, whose <c>PermitsPerSecond</c> is a constant somebody had to guess.
///     </para>
/// </summary>
/// <example>
///     <code>
/// using HttpClient client = HttpResilience.CreateClient(
///     options: new HttpResilienceOptions { Quota = Quota.Reserving(0.2) });
/// </code>
/// </example>
/// <remarks>
///     <para>
///         <b>Both header shapes are read.</b>
///         <see href="https://datatracker.ietf.org/doc/draft-ietf-httpapi-ratelimit-headers/">
///             draft-ietf-httpapi-ratelimit-headers
///         </see>
///         defines two structured fields - <c>RateLimit-Policy</c> carrying the quota <c>q</c> and the
///         window <c>w</c>, and <c>RateLimit</c> carrying the remaining allowance <c>r</c> and the
///         seconds until reset <c>t</c>. The draft names the older
///         <c>X-RateLimit-Limit</c> / <c>-Remaining</c> / <c>-Reset</c> triple as the practice it
///         exists to replace, and that triple is what most APIs send today, so
///         <see cref="LegacyHeaders" /> reads it when the standard fields are absent.
///     </para>
///     <para>
///         <b>Cold start.</b> A host that has never sent either shape has no quota, and the feature is
///         invisible. So is a host whose last published window has already reset: the reading it
///         described is over, and the guard has no opinion until the next response.
///     </para>
///     <para>
///         <b>Tighten-only.</b> The quota can refuse an attempt and can never permit one the policy
///         would have refused. A server publishing a wrong quota can slow you down and cannot break
///         you.
///     </para>
///     <para>
///         <b>The failure mode, stated outright.</b> A server publishing a per-account quota while you
///         are one of fifty pods will make every pod believe it owns the whole allowance. The reserve
///         does not fix that; only the 429 does, and the 429 still works exactly as it always has. The
///         honest use is a single-instance client, or a generous reserve.
///     </para>
/// </remarks>
public sealed record Quota
{
    /// <summary>
    ///     The fraction of the published allowance to leave unspent. Default <c>0.1</c>, and the only
    ///     number here.
    ///     <para>
    ///         At <c>0.1</c> against a published quota of 1,000, the handler refuses locally once 100
    ///         are left. At <c>0</c> it refuses only once the dependency says nothing is left, which is
    ///         also what it does for a host that publishes a remaining count without a quota to take a
    ///         fraction of.
    ///     </para>
    ///     <para>
    ///         At least 0 and less than 1. A reserve of 1 would leave the whole allowance unspent.
    ///     </para>
    /// </summary>
    public double Reserve { get; init; } = 0.1;

    /// <summary>
    ///     The header triple to read when the standard fields are absent, in the order limit,
    ///     remaining, reset. Null - the default - means
    ///     <c>X-RateLimit-Limit</c>, <c>X-RateLimit-Remaining</c>, <c>X-RateLimit-Reset</c>.
    ///     <para>
    ///         Exactly three names, or none. A dependency that spells them differently -
    ///         <c>X-Rate-Limit-*</c> is the common variant - is configured here rather than left
    ///         unread.
    ///     </para>
    ///     <para>
    ///         The reset value is read as whole seconds, either as a Unix timestamp or as a count of
    ///         seconds from now; the two are told apart by size, because no sane delta reaches 2001.
    ///     </para>
    /// </summary>
    public string[]? LegacyHeaders { get; init; }

    /// <summary>The way to configure a reserve.</summary>
    /// <param name="reserve">The fraction of the published allowance to leave unspent.</param>
    /// <returns>The configuration.</returns>
    public static Quota Reserving(double reserve = 0.1) => new() { Reserve = reserve };

    /// <inheritdoc />
    public override string ToString() =>
        $"{Reserve:0.###} of the published allowance held in reserve";

    /// <summary>
    ///     Collects everything wrong with this configuration, in the shape
    ///     <see cref="HttpResilienceOptions.Validate" /> reports problems.
    /// </summary>
    /// <param name="problems">The list to add to.</param>
    internal void Validate(List<string> problems)
    {
        if (double.IsNaN(Reserve) || Reserve < 0 || Reserve >= 1)
        {
            problems.Add(
                $"Quota.Reserve must be at least 0 and less than 1; it is {Reserve}. " +
                "Use Quota.Reserving(0.1) to leave a tenth of the published allowance unspent, or 0 to spend all of it.");
        }

        if (LegacyHeaders is { } names)
        {
            if (names.Length != 3)
            {
                problems.Add(
                    $"Quota.LegacyHeaders must name exactly three headers - limit, remaining and reset, in that order; it names {names.Length}. " +
                    "Leave it null for the X-RateLimit-* triple, or set Quota to null to read no headers at all.");
            }

            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name))
                    problems.Add("Quota.LegacyHeaders must not contain an empty name; each entry is the name of a header.");
            }
        }
    }
}
