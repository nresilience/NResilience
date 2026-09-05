using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using NResilience.Extensions.Internal;

namespace NResilience.Extensions;

/// <summary>
///     The log records. A listener that turns <see cref="CallEvent" />s into <see cref="ILogger" />
///     records carrying the library's own knowledge of what each event means, at a level that is silent
///     when the system is healthy.
///     <para>
///         Two knobs, and only one of them belongs to this library. The <b>profile</b>
///         (<see cref="ResilienceLogProfile" />) decides what level a record is emitted at, and the library
///         decides that almost always. The <b>category filter</b> decides which records are kept, and the
///         operator decides that freely, per policy, in <c>appsettings.json</c>, with no redeploy - because
///         each policy logs under its own category.
///     </para>
/// </summary>
/// <example>
///     <code language="json">
/// { "Logging": { "LogLevel": { "NResilience": "Warning", "NResilience.payments": "Debug" } } }
/// </code>
/// </example>
/// <remarks>
///     On by default for a policy registered in a container, off by default for a hand-built one, for
///     the reason <see cref="ResilienceTelemetry" /> gives: putting a policy in a container is taken as
///     asking to be able to see it.
/// </remarks>
public static class ResilienceLogging
{
    /// <summary>The prefix every category starts with.</summary>
    public const string CategoryPrefix = "NResilience";

    /// <summary>
    ///     The category a policy of this name logs under, for a filter or a test:
    ///     <c>NResilience</c> for an unnamed policy and <c>NResilience.&lt;name&gt;</c> otherwise.
    /// </summary>
    /// <param name="policyName"><see cref="Resilience.Name" />.</param>
    /// <returns>The logger category.</returns>
    /// <remarks>
    ///     Fixed when the listener is attached rather than read per event. The HTTP path derives a
    ///     per-host policy named <c>client:host</c>, so a computed-per-event category would give a client
    ///     talking to fifty hosts fifty categories and <c>"NResilience.api"</c> would match none of them.
    ///     The host-scoped name still reaches the record as its <c>Policy</c> field, so it is queryable
    ///     without fragmenting the filter.
    /// </remarks>
    public static string CategoryFor(string? policyName) =>
        string.IsNullOrEmpty(policyName) ? CategoryPrefix : $"{CategoryPrefix}.{policyName}";

    /// <summary>
    ///     A listener writing to this logger. Stateful, because of the suppression bookkeeping, so it is
    ///     one per policy rather than a shared singleton.
    /// </summary>
    /// <param name="logger">Where the records go.</param>
    /// <param name="options">The profile, the repeat window and the level delegate. Null takes the defaults.</param>
    /// <param name="time">The clock the repeat window runs on. Null takes <see cref="TimeProvider.System" />; pass the policy's own so a test can drive it.</param>
    /// <returns>The listener, as <see cref="Resilience.OnEvent" /> takes it.</returns>
    public static Action<CallEvent> Listener(
        ILogger logger,
        ResilienceLoggingOptions? options = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        return new LogListener(logger, options ?? new ResilienceLoggingOptions(), time).Record;
    }

    /// <summary>
    ///     Returns the policy with a log listener chained after whatever <see cref="Resilience.OnEvent" />
    ///     already held.
    /// </summary>
    /// <param name="policy">The policy.</param>
    /// <param name="logger">
    ///     Where the records go. Its category is what an <c>appsettings.json</c> filter matches, so <see cref="CategoryFor(string?)" /> is how to
    ///     create it.
    /// </param>
    /// <param name="options">The profile, the repeat window and the level delegate. Null takes the defaults.</param>
    /// <returns>The policy, or the same policy when a log listener is already attached.</returns>
    /// <remarks>
    ///     <b>At most one log listener per policy, and the first one attached wins.</b> A container
    ///     attaches last, after the <c>configure</c> callback, so the rule reads as "an explicit
    ///     <c>WithLogging</c> beats the automatic one" - which is the intuitive precedence and needs no
    ///     switch to express. Double-attached logging is a doubled log, and a doubled log is worse than
    ///     either alternative.
    /// </remarks>
    public static Resilience WithLogging(
        this Resilience policy,
        ILogger logger,
        ResilienceLoggingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(logger);

        var effective = options ?? new ResilienceLoggingOptions();

        if (effective.Profile == ResilienceLogProfile.Off || Attached(policy.OnEvent))
            return policy;

        // The policy's own clock, so FakeTimeProvider drives the repeat window in a test.
        var listener = new LogListener(logger, effective, policy.Time);

        return policy with { OnEvent = policy.OnEvent + listener.Record };
    }

    /// <summary>
    ///     The same, creating the logger from the factory under the policy's own category.
    /// </summary>
    /// <param name="policy">The policy. Its <see cref="Resilience.Name" /> decides the category.</param>
    /// <param name="loggerFactory">The factory.</param>
    /// <param name="options">The profile, the repeat window and the level delegate. Null takes the defaults.</param>
    /// <returns>The policy, or the same policy when a log listener is already attached.</returns>
    public static Resilience WithLogging(
        this Resilience policy,
        ILoggerFactory loggerFactory,
        ResilienceLoggingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        if ((options ?? new ResilienceLoggingOptions()).Profile == ResilienceLogProfile.Off || Attached(policy.OnEvent))
            return policy;

        return policy.WithLogging(loggerFactory.CreateLogger(CategoryFor(policy.Name)), options);
    }

    /// <summary>
    ///     Parses <see cref="ResilienceOptions.Logging" />, the way <see cref="ResilienceOptions.Preset" />
    ///     is parsed and for the same reason: a typo has to name the valid values rather than produce a
    ///     binder stack trace about type conversion.
    /// </summary>
    internal static ResilienceLogProfile ProfileFor(string? logging, ResilienceLogProfile fallback)
    {
        if (logging is null)
            return fallback;

        return logging.ToUpperInvariant() switch
        {
            "OFF" => ResilienceLogProfile.Off,
            "DEFAULT" => ResilienceLogProfile.Default,
            "VERBOSE" => ResilienceLogProfile.Verbose,
            _ => throw new ResilienceConfigurationException(
                [$"Logging must be one of Off, Default or Verbose; it is \"{logging}\"."]),
        };
    }

    /// <summary>
    ///     The effective policy, for event 1020. Binding a section is silently partial, and the half that
    ///     worked is the evidence people use to conclude the other half did too - so the shape that came
    ///     out is worth one line per resolution.
    /// </summary>
    /// <remarks>
    ///     Says what is readable. <see cref="Backoff" /> exposes only its cap and its jitter, and
    ///     <see cref="RetryBudget" /> only its name, so the base delays, the factor, the fraction and the
    ///     floor are not echoed back.
    /// </remarks>
    internal static string Describe(Resilience policy, bool telemetry, ResilienceLogProfile logging)
    {
        var text = new StringBuilder();

        text.Append(policy.Attempts.ToString(CultureInfo.InvariantCulture)).Append(" attempts");
        text.Append(", deadline ").Append(Duration(policy.Deadline));
        text.Append(", attempt timeout ").Append(Duration(policy.AttemptTimeout));
        text.Append(", backoff max ").Append(Duration(policy.Backoff.MaximumDelay));
        text.Append(", jitter ").Append(policy.Backoff.Jitter);

        if (policy.Breaker is { } breaker)
        {
            var settings = breaker.Settings;
            text.Append(", breaker ");

            if (settings.FailureRatio is { } ratio)
            {
                text.Append(ratio.ToString("0.##%", CultureInfo.InvariantCulture))
                    .Append(" of ").Append(settings.MinimumCalls.ToString(CultureInfo.InvariantCulture))
                    .Append(" calls over ").Append(Duration(settings.TripWindow));
            }
            else
            {
                text.Append(settings.ConsecutiveFailures.ToString(CultureInfo.InvariantCulture))
                    .Append(" consecutive failures");
            }

            text.Append(" / ").Append(Duration(settings.BreakDuration)).Append(" break");
        }
        else
            text.Append(", no breaker");

        text.Append(policy.Budget switch
        {
            { IsNone: true } => ", no budget",
            { IsAutomatic: true } => ", automatic budget",
            { Name: { } shared } => $", budget shared as \"{shared}\"",
            _ => ", own budget",
        });

        text.Append(", telemetry ").Append(telemetry ? "on" : "off");
        text.Append(", logging ").Append(logging);

        return text.ToString();
    }

    /// <summary>
    ///     True when any entry in the invocation list is a log listener, whatever logger it holds. This
    ///     is the whole of the first-attach-wins rule.
    /// </summary>
    private static bool Attached(Action<CallEvent>? existing)
    {
        if (existing is null)
            return false;

        foreach (var entry in existing.GetInvocationList())
        {
            if (entry.Target is LogListener)
                return true;
        }

        return false;
    }

    private static string Duration(TimeSpan value)
    {
        if (value == Timeout.InfiniteTimeSpan)
            return "none";

        return value.TotalSeconds >= 1
            ? $"{value.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)}s"
            : $"{value.TotalMilliseconds.ToString("0.##", CultureInfo.InvariantCulture)}ms";
    }
}
