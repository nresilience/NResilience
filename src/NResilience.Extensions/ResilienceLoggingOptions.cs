using Microsoft.Extensions.Logging;

namespace NResilience.Extensions;

/// <summary>
/// What level each log record is emitted at. The other half of the pair is the platform's own
/// category filter, which decides which records are kept - see <see cref="ResilienceLogging"/>.
/// </summary>
/// <remarks>
/// <see cref="Off"/> is the zero value on purpose: an accidentally-default profile is silent rather
/// than noisy.
/// </remarks>
public enum ResilienceLogProfile
{
    /// <summary>No listener is attached, so a record costs nothing rather than costing a suppressed call.</summary>
    Off,

    /// <summary>
    /// Healthy traffic writes nothing above <see cref="LogLevel.Trace"/>, a retried-then-successful
    /// call nothing above <see cref="LogLevel.Debug"/>, and an incident one
    /// <see cref="LogLevel.Warning"/>.
    /// </summary>
    Default,

    /// <summary>
    /// Raises every traffic-proportional record to <see cref="LogLevel.Information"/> and leaves the
    /// incident records where they are. For a sink that will not carry <see cref="LogLevel.Debug"/>,
    /// which is the one thing a category filter cannot fix.
    /// </summary>
    Verbose,
}

/// <summary>
/// The knobs on the log listener. Four members, because the fifth thing anybody wants is the
/// platform's category filter and that is not this library's to own.
/// </summary>
/// <example>
/// <code language="json">
/// { "Logging": { "LogLevel": { "NResilience": "Warning", "NResilience.payments": "Debug" } } }
/// </code>
/// </example>
public sealed class ResilienceLoggingOptions
{
    /// <summary>What level each record is emitted at. <see cref="ResilienceLogProfile.Default"/>.</summary>
    public ResilienceLogProfile Profile { get; set; } = ResilienceLogProfile.Default;

    /// <summary>
    /// How often a repeated rejection may warn. Inside the window rejections are counted and logged
    /// at <see cref="LogLevel.Debug"/>, and the count reaches the next warning, so nothing is
    /// dropped and only demoted. <see cref="TimeSpan.Zero"/> warns every time.
    /// </summary>
    public TimeSpan RepeatWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Attaches the exception object to the per-attempt and retry records as well as the terminal
    /// ones. Off by default, because a three-attempt call would otherwise write three stack traces
    /// for one failure and the one the caller cares about is the last.
    /// </summary>
    public bool IncludeStackTracesOnRetry { get; set; }

    /// <summary>
    /// The level for one record, or null to keep <see cref="Profile"/>'s.
    /// <see cref="LogLevel.None"/> drops the record.
    /// <para>
    /// Takes the <see cref="EventId"/> rather than the <see cref="CallEventKind"/> because that is
    /// what identifies a record precisely: 1006 and 1007 are both
    /// <see cref="CallEventKind.NotRetried"/>, and only one of them is the interesting one.
    /// </para>
    /// </summary>
    public Func<EventId, CallEvent, LogLevel?>? Level { get; set; }
}
