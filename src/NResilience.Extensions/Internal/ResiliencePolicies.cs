using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NResilience.Extensions.Internal;

/// <summary>
///     A base policy and a post-configure step, registered under a name. Named options rather than a
///     dictionary of our own, so a second registration for the same name composes the way every other
///     options-based registration in the platform does.
/// </summary>
internal sealed class ResiliencePolicyRegistration
{
    /// <summary>What the configuration section is projected onto. Null means <see cref="Resilience.Default" />.</summary>
    public Resilience? Baseline { get; set; }

    /// <summary>
    ///     Runs last, after configuration and after the live objects are re-attached. This is where a
    ///     classifier or a hook goes, because JSON cannot hold a lambda.
    /// </summary>
    public Func<Resilience, Resilience>? Configure { get; set; }
}

/// <summary>The registered names, collected at registration time so <see cref="IResiliencePolicies.Names" /> can be answered.</summary>
internal sealed class ResilienceNames
{
    public ConcurrentDictionary<string, byte> Set { get; } = new(StringComparer.Ordinal);
}

/// <summary>
///     The live objects for one name: created on first projection and deliberately outliving every
///     reload, because a breaker's state is the reason to have one.
/// </summary>
internal sealed class LiveState
{
    public Breaker? Breaker { get; set; }

    public RetryBudget? Budget { get; set; }
}

/// <summary>One registered policy's live guards, as the health check reads them.</summary>
/// <param name="Name">The registration name.</param>
/// <param name="Breaker">Its breaker, or null when it has none.</param>
/// <param name="Budget">Its retry budget, or null when it has none.</param>
internal sealed record RegisteredGuards(string Name, Breaker? Breaker, RetryBudget? Budget);

/// <inheritdoc cref="IResiliencePolicies" />
internal sealed class ResiliencePolicies : IResiliencePolicies, IDisposable
{
    private readonly ConcurrentDictionary<string, LiveState> _live = new(StringComparer.Ordinal);
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ResilienceLoggingOptions _logging;
    private readonly ResilienceNames _names;
    private readonly IOptionsMonitor<ResilienceOptions> _options;
    private readonly ConcurrentDictionary<string, Resilience> _projected = new(StringComparer.Ordinal);
    private readonly IOptionsMonitor<ResiliencePolicyRegistration> _registrations;
    private readonly IDisposable? _subscription;

    public ResiliencePolicies(
        IOptionsMonitor<ResilienceOptions> options,
        IOptionsMonitor<ResiliencePolicyRegistration> registrations,
        ResilienceNames names,
        ILoggerFactory? loggerFactory = null,
        ResilienceLoggingOptions? logging = null)
    {
        _options = options;
        _registrations = registrations;
        _names = names;

        // Optional, both of them: a container that does not do logging gets no listener and no
        // exception, which is what makes logging on-by-default safe to be on by default.
        _loggerFactory = loggerFactory;
        _logging = logging ?? new ResilienceLoggingOptions();

        // The whole of hot reload. The projection cache is dropped for the changed name and the
        // next resolve rebuilds it; _live is untouched, which is what carries a breaker's state
        // across the edit.
        _subscription = options.OnChange((_, name) =>
        {
            if (name is not null)
                _projected.TryRemove(name, out var _);
        });
    }

    public void Dispose() => _subscription?.Dispose();

    public Resilience this[string name]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(name);

            if (!_names.Set.ContainsKey(name))
            {
                throw new ResilienceConfigurationException(
                    [$"No resilience policy is registered under \"{name}\". Registered: {Registered()}."]);
            }

            return Project(name);
        }
    }

    public IReadOnlyCollection<string> Names => _names.Set.Keys.ToArray();

    public bool TryGet(string name, out Resilience policy)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!_names.Set.ContainsKey(name))
        {
            policy = Resilience.Default;
            return false;
        }

        policy = Project(name);
        return true;
    }

    /// <summary>
    ///     The live breaker and budget behind every registered name, for the health check.
    ///     <para>
    ///         Resolving each name is what creates those objects, so this projects any name that has not
    ///         been resolved yet - which is cached, validated and idempotent. The alternative is a health
    ///         check that reports nothing about a policy until the first request happens to use it, which
    ///         is exactly backwards for a readiness probe.
    ///     </para>
    /// </summary>
    internal IEnumerable<RegisteredGuards> Guards()
    {
        foreach (var name in _names.Set.Keys)
        {
            TryGet(name, out _);

            if (_live.TryGetValue(name, out var live))
                yield return new RegisteredGuards(name, live.Breaker, live.Budget);
        }
    }

    private string Registered()
    {
        var names = _names.Set.Keys.ToArray();
        Array.Sort(names, StringComparer.Ordinal);
        return names.Length == 0 ? "(nothing)" : string.Join(", ", names);
    }

    /// <summary>
    ///     Builds the policy for a name, or returns the one already built.
    ///     <para>
    ///         <c>GetOrAdd</c> without a lock: a reload racing a resolve can build the policy twice, and
    ///         building it twice is harmless because the live breaker and budget are taken from
    ///         <see cref="_live" /> rather than created here. Two identical values, one of which is
    ///         discarded, is the cheap outcome; a lock on every resolve is not.
    ///     </para>
    /// </summary>
    private Resilience Project(string name) => _projected.GetOrAdd(name, Build, this);

    /// <summary>
    ///     Binds one policy's section, turning the binder's complaint about an unrecognized key into
    ///     one of ours.
    /// </summary>
    /// <remarks>
    ///     The section is bound with <c>ErrorOnUnknownConfiguration</c>, so a key this DTO does not
    ///     have is an error rather than a no-op. The binder's own message names the offending keys but
    ///     not what to do about them, and by far the most likely cause is a key that used to exist -
    ///     so this adds the sentence that points at the migration table.
    /// </remarks>
    /// <param name="self">The policies.</param>
    /// <param name="name">The registration name.</param>
    /// <returns>The bound options.</returns>
    /// <exception cref="ResilienceConfigurationException">The section holds a key <see cref="ResilienceOptions" /> does not have.</exception>
    private static ResilienceOptions OptionsFor(ResiliencePolicies self, string name)
    {
        try
        {
            return self._options.Get(name);
        }
        catch (InvalidOperationException error)
        {
            throw new ResilienceConfigurationException(
                $"The configuration section for policy \"{name}\" could not be bound. {error.Message} " +
                "Check the spelling, and check whether the key was renamed - see \"Migrating an existing file\" " +
                "in the configuration documentation.",
                error);
        }
    }

    private static Resilience Build(string name, ResiliencePolicies self)
    {
        var registration = self._registrations.Get(name);
        var options = OptionsFor(self, name);

        var policy = options.ToPolicy(registration.Baseline);

        // The registration name wins over a name the policy value already carried, and only an
        // explicit ResilienceOptions.Name overrides it. The reason is Resilience.Http: it is named
        // "http", so `AddResilience("api", Resilience.Http)` - the single most likely line anybody
        // writes - would tag every metric and every log line "http", and a process with four such
        // clients could not tell them apart. A preset's name is a label on the preset, not a
        // decision about this registration.
        if (options.Name is null)
            policy = policy with { Name = name };

        policy = self.Reuse(name, policy);

        if (registration.Configure is { } configure)
        {
            // Last, so a callback can override anything configuration said - including handing the
            // policy a breaker shared with another policy, which is the one scope decision that
            // cannot be expressed in JSON.
            policy = configure(policy) ?? policy;
        }

        var telemetry = options.Telemetry ?? true;

        if (telemetry)
            policy = policy.WithTelemetry();

        // After the configure callback, so a callback that assigns OnEvent does not lose logging and
        // one that calls WithLogging itself wins under the first-attach-wins rule.
        var profile = ResilienceLogging.ProfileFor(options.Logging, self._logging.Profile);

        if (profile != ResilienceLogProfile.Off && self._loggerFactory is { } factory)
        {
            var logging = self.LoggingFor(profile);
            policy = policy.WithLogging(factory, logging);

            // Provenance. Binding a section is silently partial, so one line per policy per
            // resolution says what actually came out - and a reload produces a fresh one, which is
            // exactly when somebody wants to know what it changed to.
            var logger = factory.CreateLogger(ResilienceLogging.CategoryFor(policy.Name));
            var reported = policy.Name ?? name;

            if (logger.IsEnabled(LogLevel.Debug))
                Log.PolicyResolved(logger, LogLevel.Debug, reported, ResilienceLogging.Describe(policy, telemetry, profile));

            // Classifier.ToString builds a multi-line dump, so the guard is the point rather than a
            // micro-optimization: this is the only record that costs a string before it is written.
            if (logger.IsEnabled(LogLevel.Trace))
                Log.PolicyClassifier(logger, LogLevel.Trace, reported, policy.Classifier.ToString());
        }

        policy.Validate();
        return policy;
    }

    /// <summary>
    ///     The process-wide logging options, or a copy with the profile a section overrode. Copied once
    ///     per policy per reload rather than per call, which is what keeps
    ///     <see cref="ResilienceLoggingOptions" /> a plain mutable options class.
    /// </summary>
    private ResilienceLoggingOptions LoggingFor(ResilienceLogProfile profile) =>
        profile == _logging.Profile
            ? _logging
            : new ResilienceLoggingOptions
            {
                Profile = profile,
                RepeatWindow = _logging.RepeatWindow,
                Sampling = _logging.Sampling,
                IncludeStackTracesOnRetry = _logging.IncludeStackTracesOnRetry,
                Level = _logging.Level,
            };

    /// <summary>
    ///     Re-attaches the breaker and the budget this name is already using, or adopts the ones this
    ///     projection created.
    ///     <para>
    ///         This is what makes reload a swap of configuration rather than a reset of state. A breaker
    ///         that is open because a dependency is down stays open when somebody edits an unrelated field
    ///         in appsettings.json; handing the traffic straight back to a dead dependency because a file
    ///         changed would be the worst possible reading of "hot reload".
    ///     </para>
    /// </summary>
    private Resilience Reuse(string name, Resilience policy)
    {
        var live = _live.GetOrAdd(name, static _ => new LiveState());

        lock (live)
        {
            if (policy.Breaker is { } breaker)
            {
                live.Breaker ??= breaker;
                policy = policy with { Breaker = live.Breaker };
            }

            if (policy.Budget is { IsAutomatic: false } budget)
            {
                live.Budget ??= budget;
                policy = policy with { Budget = live.Budget };
            }
            else if (policy.Budget is { IsAutomatic: true } && policy.Attempts > 1)
            {
                // RetryBudget.Automatic resolves to a bucket keyed by policy *instance*, and reload
                // produces a new instance - so the accumulated traffic history would be thrown away
                // on every edit, silently, on the default configuration. Materializing it here pins
                // it to the name instead. RetryBudget.Of's defaults are Automatic's defaults, so
                // nothing about the policy's behavior changes.
                live.Budget ??= RetryBudget.Of(time: policy.Time);
                policy = policy with { Budget = live.Budget };
            }
        }

        return policy;
    }
}
