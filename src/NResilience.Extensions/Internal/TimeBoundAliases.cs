using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace NResilience.Extensions.Internal;

/// <summary>
///     Lets the two time bounds on <see cref="ResilienceOptions" /> spell "no bound" as a word.
///     <para>
///         <see cref="Timeout.InfiniteTimeSpan" /> is minus one millisecond, so the binder round-trips
///         it as <c>"-00:00:00.0010000"</c> - correct, and unwriteable by an operator editing
///         <c>appsettings.Production.json</c> during an incident. Every other "off" in the
///         configuration surface is a word, so these are too: <c>"Deadline": "Infinite"</c> reads the
///         way <c>"Enabled": false</c> reads.
///     </para>
///     <para>
///         A decorator over the section rather than a change to the DTO, because
///         <see cref="ResilienceOptions.Deadline" /> stays a <see cref="TimeSpan" /> for the callers who
///         set it in code. The substitution happens on the way into the binder; the numeric form still
///         binds untouched.
///     </para>
/// </summary>
internal static class TimeBoundAliases
{
    /// <summary>The spelling the binder round-trips <see cref="Timeout.InfiniteTimeSpan" /> as.</summary>
    private static readonly string Infinite = Timeout.InfiniteTimeSpan.ToString();

    /// <summary>Wraps a policy's section so its two time bounds accept the words as well as the duration.</summary>
    /// <param name="section">The section a policy is bound from.</param>
    /// <returns>The section, seen through the substitution.</returns>
    internal static IConfiguration Around(IConfiguration section) => new AliasedConfiguration(section);

    /// <summary>Whether a key at the top of a policy section is one of the two time bounds.</summary>
    /// <param name="key">The key.</param>
    /// <returns>True when the value is a time bound.</returns>
    private static bool IsTimeBound(string key) =>
        key.Equals(nameof(ResilienceOptions.Deadline), StringComparison.OrdinalIgnoreCase)
        || key.Equals(nameof(ResilienceOptions.AttemptTimeout), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Projects <c>"Infinite"</c>, <c>"None"</c> and <c>"Unbounded"</c> onto the duration the binder
    ///     parses as <see cref="Timeout.InfiniteTimeSpan" />. Anything else is passed through, so a
    ///     misspelling still produces the binder's own message rather than a silently unbounded call.
    /// </summary>
    /// <param name="value">The configured value.</param>
    /// <returns>The value the binder sees.</returns>
    private static string? Translate(string? value)
    {
        if (value is null)
            return null;

        var word = value.Trim();

        return word.Equals("Infinite", StringComparison.OrdinalIgnoreCase)
               || word.Equals("None", StringComparison.OrdinalIgnoreCase)
               || word.Equals("Unbounded", StringComparison.OrdinalIgnoreCase)
            ? Infinite
            : value;
    }

    /// <summary>The policy section, with the two time bounds seen through <see cref="Translate" />.</summary>
    private sealed class AliasedConfiguration(IConfiguration inner) : IConfiguration
    {
        /// <inheritdoc />
        public string? this[string key]
        {
            get => IsTimeBound(key) ? Translate(inner[key]) : inner[key];
            set => inner[key] = value;
        }

        /// <inheritdoc />
        public IEnumerable<IConfigurationSection> GetChildren() =>
            inner.GetChildren().Select(static child => IsTimeBound(child.Key) ? new AliasedSection(child) : child);

        /// <inheritdoc />
        public IChangeToken GetReloadToken() => inner.GetReloadToken();

        /// <inheritdoc />
        public IConfigurationSection GetSection(string key) =>
            IsTimeBound(key) ? new AliasedSection(inner.GetSection(key)) : inner.GetSection(key);
    }

    /// <summary>One time bound, reporting the word as the duration it stands for.</summary>
    private sealed class AliasedSection(IConfigurationSection inner) : IConfigurationSection
    {
        /// <inheritdoc />
        public string? this[string key]
        {
            get => inner[key];
            set => inner[key] = value;
        }

        /// <inheritdoc />
        public string Key => inner.Key;

        /// <inheritdoc />
        public string Path => inner.Path;

        /// <inheritdoc />
        public string? Value
        {
            get => Translate(inner.Value);
            set => inner.Value = value;
        }

        /// <inheritdoc />
        public IEnumerable<IConfigurationSection> GetChildren() => inner.GetChildren();

        /// <inheritdoc />
        public IChangeToken GetReloadToken() => inner.GetReloadToken();

        /// <inheritdoc />
        public IConfigurationSection GetSection(string key) => inner.GetSection(key);
    }
}
