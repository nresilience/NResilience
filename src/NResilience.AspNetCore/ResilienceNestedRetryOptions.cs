namespace NResilience.AspNetCore;

/// <summary>What the inbound nested-retry middleware reads.</summary>
/// <remarks>
///     A mutable class rather than a record, for the same reason
///     <see cref="ResilienceDeadlineOptions" /> is: this is the type a configure callback configures.
/// </remarks>
public sealed class ResilienceNestedRetryOptions
{
    /// <summary>
    ///     The header carrying the caller's retry marker. Defaults to
    ///     <see cref="NestedRetry.Header" />, which is what a retrying
    ///     <see cref="HttpResilienceHandler" /> writes.
    /// </summary>
    public string Header { get; set; } = NestedRetry.Header;
}
