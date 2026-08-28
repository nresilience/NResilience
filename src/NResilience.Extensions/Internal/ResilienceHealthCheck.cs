using System.Globalization;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NResilience.Extensions.Internal;

/// <summary>
///     Reports every breaker and retry budget this process is guarding a dependency with.
///     <para>
///         A read, not a probe. Nothing here contacts a dependency, so the check costs a few dictionary
///         walks and cannot itself time out, hang, or add load to the service it is reporting on - which
///         is what disqualifies the obvious alternative of having the health check make a real call.
///     </para>
/// </summary>
internal sealed class ResilienceHealthCheck(
    ResilienceHealthOptions options,
    IResiliencePolicies? policies,
    ResilienceHandlerRegistry? handlers) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        var tally = default(Tally);

        if (policies is ResiliencePolicies registered)
        {
            foreach (var guard in registered.Guards())
                Inspect(guard.Name, guard.Breaker, guard.Budget, data, ref tally);
        }

        if (options.IncludeHttpClients && handlers is not null)
        {
            foreach (var entry in handlers.Live())
            {
                foreach (var breaker in entry.Value.BreakersByHost())
                    Inspect($"{entry.Key}:{breaker.Key}", breaker.Value, null, data, ref tally);

                foreach (var budget in entry.Value.BudgetsByHost())
                    Inspect($"{entry.Key}:{budget.Key}", null, budget.Value, data, ref tally);
            }
        }

        foreach (var watched in options.Watched)
            Inspect(watched.Name, watched.Breaker, watched.Budget, data, ref tally);

        // HealthStatus orders Unhealthy (0) before Degraded (1) before Healthy (2), so the worst
        // status is the smallest one.
        var status = HealthStatus.Healthy;

        if (tally.OpenBreakers > 0)
            status = Worse(status, options.BreakerOpenStatus);

        if (tally.ExhaustedBudgets > 0)
            status = Worse(status, options.BudgetExhaustedStatus);

        return Task.FromResult(new HealthCheckResult(status, Describe(tally), data: data));
    }

    private void Inspect(string name, Breaker? breaker, RetryBudget? budget, Dictionary<string, object> data, ref Tally tally)
    {
        if (breaker is not null)
        {
            var state = breaker.State;
            tally.Breakers++;

            if (state is BreakerState.Open or BreakerState.Isolated)
                tally.OpenBreakers++;

            data[$"breaker:{name}"] = breaker.OpenedAt is { } since
                ? $"{state} since {since.ToString("O", CultureInfo.InvariantCulture)}"
                : state.ToString();

            // What an adaptive breaker currently thinks a healthy call to this dependency costs. Only
            // present when it is configured that way and has enough samples to have an opinion, which
            // is the same condition under which its slow-call trip is armed at all.
            if (breaker.NormalLatency is { } normal)
                data[$"breaker:{name}:normal"] = normal.TotalMilliseconds;
        }

        // RetryBudget.None and the Automatic marker both report zero utilization and neither is a
        // bucket, so reporting them would fill the payload with rows that can never move.
        if (budget is null || budget.IsNone || budget.IsAutomatic)
            return;

        var utilization = budget.Utilization;
        tally.Budgets++;

        if (utilization >= options.BudgetThreshold)
            tally.ExhaustedBudgets++;

        data[$"budget:{name}"] = utilization;
    }

    private static HealthStatus Worse(HealthStatus current, HealthStatus candidate) =>
        candidate < current ? candidate : current;

    private static string Describe(in Tally tally)
    {
        if (tally.Breakers == 0 && tally.Budgets == 0)
        {
            return "No breakers or retry budgets are registered. Either nothing is configured with one, "
                   + "or the policies that have them are not registered in this container.";
        }

        if (tally.OpenBreakers == 0 && tally.ExhaustedBudgets == 0)
            return $"{tally.Breakers} breaker(s) closed, {tally.Budgets} retry budget(s) funding retries.";

        var problems = new List<string>(2);

        if (tally.OpenBreakers > 0)
            problems.Add($"{tally.OpenBreakers} of {tally.Breakers} breaker(s) open or isolated");

        if (tally.ExhaustedBudgets > 0)
            problems.Add($"{tally.ExhaustedBudgets} of {tally.Budgets} retry budget(s) exhausted");

        return string.Join("; ", problems) + ".";
    }

    /// <summary>What was found, counted as it is found. A struct so counting allocates nothing.</summary>
    private struct Tally
    {
        public int Breakers;
        public int OpenBreakers;
        public int Budgets;
        public int ExhaustedBudgets;
    }
}
