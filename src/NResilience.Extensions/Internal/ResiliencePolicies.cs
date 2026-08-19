using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace NResilience.Extensions.Internal;

/// <summary>
/// A base policy and a post-configure step, registered under a name. Named options rather than a
/// dictionary of our own, so a second registration for the same name composes the way every other
/// options-based registration in the platform does.
/// </summary>
internal sealed class ResiliencePolicyRegistration
{
    /// <summary>What the configuration section is projected onto. Null means <see cref="Resilience.Default"/>.</summary>
    public Resilience? Baseline { get; set; }

    /// <summary>
    /// Runs last, after configuration and after the live objects are re-attached. This is where a
    /// classifier or a hook goes, because JSON cannot hold a lambda.
    /// </summary>
    public Func<Resilience, Resilience>? Configure { get; set; }
}

/// <summary>The registered names, collected at registration time so <see cref="IResiliencePolicies.Names"/> can be answered.</summary>
internal sealed class ResilienceNames
{
    public ConcurrentDictionary<string, byte> Set { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The live objects for one name: created on first projection and deliberately outliving every
/// reload, because a breaker's state is the reason to have one.
/// </summary>
internal sealed class LiveState
{
    public Breaker? Breaker { get; set; }

    public RetryBudget? Budget { get; set; }
}

/// <inheritdoc cref="IResiliencePolicies"/>
internal sealed class ResiliencePolicies : IResiliencePolicies, IDisposable
{
    private readonly IOptionsMonitor<ResilienceOptions> _options;
    private readonly IOptionsMonitor<ResiliencePolicyRegistration> _registrations;
    private readonly ResilienceNames _names;
    private readonly ConcurrentDictionary<string, Resilience> _projected = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LiveState> _live = new(StringComparer.Ordinal);
    private readonly IDisposable? _subscription;

    public ResiliencePolicies(
        IOptionsMonitor<ResilienceOptions> options,
        IOptionsMonitor<ResiliencePolicyRegistration> registrations,
        ResilienceNames names)
    {
        _options = options;
        _registrations = registrations;
        _names = names;

        // The whole of hot reload. The projection cache is dropped for the changed name and the
        // next resolve rebuilds it; _live is untouched, which is what carries a breaker's state
        // across the edit.
        _subscription = options.OnChange((changed, name) =>
        {
            _ = changed;

            if (name is not null)
            {
                _projected.TryRemove(name, out Resilience? _);
            }
        });
    }

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

    public void Dispose() => _subscription?.Dispose();

    private string Registered()
    {
        string[] names = _names.Set.Keys.ToArray();
        Array.Sort(names, StringComparer.Ordinal);
        return names.Length == 0 ? "(nothing)" : string.Join(", ", names);
    }

    /// <summary>
    /// Builds the policy for a name, or returns the one already built.
    /// <para>
    /// <c>GetOrAdd</c> without a lock: a reload racing a resolve can build the policy twice, and
    /// building it twice is harmless because the live breaker and budget are taken from
    /// <see cref="_live"/> rather than created here. Two identical values, one of which is
    /// discarded, is the cheap outcome; a lock on every resolve is not.
    /// </para>
    /// </summary>
    private Resilience Project(string name) => _projected.GetOrAdd(name, Build, this);

    private static Resilience Build(string name, ResiliencePolicies self)
    {
        ResiliencePolicyRegistration registration = self._registrations.Get(name);
        ResilienceOptions options = self._options.Get(name);

        Resilience policy = options.ToPolicy(registration.Baseline);

        // The registration name wins over a name the policy value already carried, and only an
        // explicit ResilienceOptions.Name overrides it. The reason is Resilience.Http: it is named
        // "http", so `AddResilience("api", Resilience.Http)` - the single most likely line anybody
        // writes - would tag every metric and every log line "http", and a process with four such
        // clients could not tell them apart. A preset's name is a label on the preset, not a
        // decision about this registration.
        if (options.Name is null)
        {
            policy = policy with { Name = name };
        }

        policy = self.Reuse(name, policy);

        if (registration.Configure is { } configure)
        {
            // Last, so a callback can override anything configuration said - including handing the
            // policy a breaker shared with another policy, which is the one scope decision that
            // cannot be expressed in JSON.
            policy = configure(policy) ?? policy;
        }

        if (options.Telemetry ?? true)
        {
            policy = policy.WithTelemetry();
        }

        policy.Validate();
        return policy;
    }

    /// <summary>
    /// Re-attaches the breaker and the budget this name is already using, or adopts the ones this
    /// projection created.
    /// <para>
    /// This is what makes reload a swap of configuration rather than a reset of state. A breaker
    /// that is open because a dependency is down stays open when somebody edits an unrelated field
    /// in appsettings.json; handing the traffic straight back to a dead dependency because a file
    /// changed would be the worst possible reading of "hot reload".
    /// </para>
    /// </summary>
    private Resilience Reuse(string name, Resilience policy)
    {
        LiveState live = _live.GetOrAdd(name, static _ => new LiveState());

        lock (live)
        {
            if (policy.Breaker is { } breaker)
            {
                live.Breaker ??= breaker;
                policy = policy with { Breaker = live.Breaker };
            }

            if (policy.Budget is { } budget)
            {
                live.Budget ??= budget;
                policy = policy with { Budget = live.Budget };
            }
            else if (policy.Attempts > 1)
            {
                // A null budget means the core creates an automatic one keyed by policy *instance*,
                // and reload produces a new instance - so the accumulated traffic history would be
                // thrown away on every edit, silently, on the default configuration. Materialising
                // it here pins it to the name instead. RetryBudget.Of's defaults are the automatic
                // budget's defaults, so nothing about the policy's behaviour changes.
                live.Budget ??= RetryBudget.Of(time: policy.Time);
                policy = policy with { Budget = live.Budget };
            }
        }

        return policy;
    }
}
