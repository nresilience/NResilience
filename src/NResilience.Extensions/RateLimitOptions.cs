using System.Threading.RateLimiting;

namespace NResilience.Extensions;

/// <summary>
/// The bindable shape of a limiter: flat, mutable, and made of primitives and a
/// <see cref="TimeSpan"/>, for the same reason <see cref="ResilienceOptions"/> is.
/// <para>
/// Exactly one of <see cref="PermitsPerSecond"/>, <see cref="Permits"/> (with
/// <see cref="Window"/>) and <see cref="Concurrency"/> may be set. They are three different guards
/// and a section that asks for two of them is a section whose author expected one of them to win;
/// <see cref="Validate"/> says so instead of picking.
/// </para>
/// </summary>
/// <example>
/// <code language="json">
/// {
///   "RateLimit": {
///     "PermitsPerSecond": 100,
///     "PerHost": true
///   }
/// }
/// </code>
/// </example>
public sealed class RateLimitOptions
{
    /// <summary>Calls allowed per second, with one second of burst. See <see cref="Limit.PerSecond"/>.</summary>
    public int? PermitsPerSecond { get; set; }

    /// <summary>Calls allowed per <see cref="Window"/>. Set both or neither. See <see cref="Limit.PerWindow"/>.</summary>
    public int? Permits { get; set; }

    /// <summary>The window <see cref="Permits"/> applies to. Set both or neither.</summary>
    public TimeSpan? Window { get; set; }

    /// <summary>Calls allowed in flight at once - the bulkhead. See <see cref="Limit.Concurrency"/>.</summary>
    public int? Concurrency { get; set; }

    /// <summary>
    /// How many callers may wait for a permit. Zero - the default - refuses immediately.
    /// <para>
    /// Off by default because this library is already good at waiting: a refusal becomes a retry on
    /// the throttled backoff curve, honouring the limiter's own hint, capped by
    /// <c>Backoff.Max</c> and by the time left on the deadline, and visible in telemetry as a retry.
    /// Queue time is instead charged against <see cref="Resilience.AttemptTimeout"/>, where it is
    /// indistinguishable from a slow dependency and can trip a
    /// <see cref="BreakerSettings.SlowCallThreshold"/> breaker against a service that is fine. Raise
    /// <see cref="Resilience.AttemptTimeout"/> if you turn queueing on.
    /// </para>
    /// </summary>
    public int QueueLimit { get; set; }

    /// <summary>
    /// Whether each host gets its own quota. On by default, and scoped by the same
    /// <c>host:port</c> key the per-host breakers and budgets use, so all three agree on what a host
    /// is. HTTP registrations only.
    /// </summary>
    public bool PerHost { get; set; } = true;

    /// <summary>A name for the limiter, reported on <see cref="RateLimitedException.Limiter"/> and in the metrics.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Checks the options and throws <see cref="ResilienceConfigurationException"/> listing every
    /// problem at once, in the same shape <see cref="Resilience.Validate"/> uses.
    /// </summary>
    /// <exception cref="ResilienceConfigurationException">The options do not describe one limiter.</exception>
    public void Validate()
    {
        var problems = new List<string>();

        int kinds = 0;
        if (PermitsPerSecond is not null)
        {
            kinds++;
        }

        if (Permits is not null || Window is not null)
        {
            kinds++;

            if (Permits is null || Window is null)
            {
                problems.Add("Permits and Window must be set together; a windowed limit needs both.");
            }
        }

        if (Concurrency is not null)
        {
            kinds++;
        }

        if (kinds == 0)
        {
            problems.Add("Set one of PermitsPerSecond, Permits with Window, or Concurrency.");
        }
        else if (kinds > 1)
        {
            problems.Add("Set only one of PermitsPerSecond, Permits with Window, or Concurrency; they are three different guards.");
        }

        Positive(PermitsPerSecond, nameof(PermitsPerSecond), problems);
        Positive(Permits, nameof(Permits), problems);
        Positive(Concurrency, nameof(Concurrency), problems);

        if (Window is { } window && window <= TimeSpan.Zero)
        {
            problems.Add($"Window must be positive; it is {window}.");
        }

        if (QueueLimit < 0)
        {
            problems.Add($"QueueLimit cannot be negative; it is {QueueLimit}.");
        }

        if (problems.Count > 0)
        {
            throw new ResilienceConfigurationException(problems);
        }
    }

    /// <summary>Builds the limiter these options describe.</summary>
    /// <returns>The limiter. The caller owns it.</returns>
    /// <exception cref="ResilienceConfigurationException">The options do not describe one limiter.</exception>
    public RateLimiter ToLimiter()
    {
        Validate();

        if (PermitsPerSecond is { } perSecond)
        {
            return Limit.PerSecond(perSecond, QueueLimit);
        }

        if (Permits is { } permits && Window is { } window)
        {
            return Limit.PerWindow(permits, window, QueueLimit);
        }

        return Limit.Concurrency(Concurrency!.Value, QueueLimit);
    }

    private static void Positive(int? value, string name, List<string> problems)
    {
        if (value is { } set && set < 1)
        {
            problems.Add($"{name} must be at least 1; it is {set}.");
        }
    }
}
