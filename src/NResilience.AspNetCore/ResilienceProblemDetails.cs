using System.Text.Json;
using System.Text.Json.Serialization;

namespace NResilience.AspNetCore;

/// <summary>
///     An RFC 9457 problem document, as the handful of members this handler writes.
///     <para>
///         Not <c>Microsoft.AspNetCore.Mvc.ProblemDetails</c>: that type serialises through MVC's
///         reflection-based path, and this package is <c>IsAotCompatible</c>. A record with a
///         source-generated context is the AOT-clean equivalent.
///     </para>
/// </summary>
internal sealed record ResilienceProblemDetails
{
    public required string Type { get; init; }

    public required string Title { get; init; }

    public required int Status { get; init; }

    public required string Detail { get; init; }

    public required string Instance { get; init; }

    /// <summary>The RFC 9457 extension member, present only when IncludeAttemptDetails is set.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResilienceAttemptDetails? Resilience { get; init; }
}

/// <summary>What the call did before it failed. An extension member, off by default.</summary>
internal sealed record ResilienceAttemptDetails
{
    public required int Attempts { get; init; }

    /// <summary>Wall-clock milliseconds. A number, because a duration in a problem document is a number.</summary>
    public required double ElapsedMs { get; init; }
}

/// <summary>
///     The only serialisation this package does. Source-generated, so the AOT and trim analyzers stay
///     quiet and the published binary carries no reflection metadata for it.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ResilienceProblemDetails))]
internal sealed partial class ResilienceProblemJsonContext : JsonSerializerContext;