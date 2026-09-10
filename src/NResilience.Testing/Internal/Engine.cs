using System.Reflection;

namespace NResilience.Testing.Internal;

/// <summary>
///     Which build of the engine produced a report.
///     <para>
///         Determinism is a promise about one version of the library, not about the library. Two
///         reports from the same seed and the same scenario are comparable when they came from the
///         same engine, and comparing them across versions is a deliberate act rather than an
///         accident - which it cannot be unless a report says which engine it came from.
///     </para>
/// </summary>
internal static class Engine
{
    /// <summary>The engine's version, read once.</summary>
    internal static string Version { get; } = Read();

    private static string Read()
    {
        // The core assembly rather than this one: the executor, the breaker, the budget and every
        // estimator live there, and they are what a report is a measurement of. The two version
        // together, so reading either would give the same answer today - and this one stays right if
        // that ever stops being true.
        var assembly = typeof(Resilience).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrEmpty(informational))
            return assembly.GetName().Version?.ToString() ?? "0.0.0";

        // Source link appends "+<commit sha>". Build metadata is not part of the version two reports
        // are compared across, and keeping it would make a local build and the CI build of the same
        // commit disagree about whether their reports are comparable. A prerelease label is kept:
        // 1.2.0-beta.1 and 1.2.0 really are different engines.
        var plus = informational.IndexOf('+');

        return plus < 0 ? informational : informational[..plus];
    }
}
