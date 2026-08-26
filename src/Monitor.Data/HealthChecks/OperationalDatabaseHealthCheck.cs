using Microsoft.Extensions.Diagnostics.HealthChecks;
using Monitor.Data.Repositories;

namespace Monitor.Data.HealthChecks;

/// <summary>
/// Surfaces <see cref="IOperationalDb"/>'s "never take down the primary
/// request path" contract through ASP.NET Core's health-check pipeline. The
/// source had no equivalent generic health check at all — the closest
/// analogue is <c>monitor.service.ts</c>'s <c>isDbAvailable()</c>
/// (<c>SELECT 1 AS ok</c> via <c>prodQuery</c>), which is a per-request
/// dashboard concern, not a startup/liveness signal. This check exists so a
/// future readiness/liveness endpoint (wired up by whichever slice owns
/// Program.cs's health surface) can report the optional operational replica
/// as <see cref="HealthStatus.Degraded"/> — never
/// <see cref="HealthStatus.Unhealthy"/> — because an unreachable or
/// unconfigured operational DB must not flip the whole service's aggregate
/// health to failing; <see cref="Repositories.IPrimaryDb"/> traffic does not
/// depend on it.
/// </summary>
public sealed class OperationalDatabaseHealthCheck : IHealthCheck
{
    private readonly IOperationalDb _operationalDb;

    public OperationalDatabaseHealthCheck(IOperationalDb operationalDb)
    {
        _operationalDb = operationalDb;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_operationalDb.IsConfigured)
        {
            // Not configured is an expected, first-class state for this optional
            // source (see DatabaseOptions.Operational) — not a health problem.
            return HealthCheckResult.Healthy("Operational database is not configured");
        }

        var result = await _operationalDb.QueryAsync<int>(
            "SELECT 1 AS ok",
            cancellationToken: cancellationToken);

        return result.Available
            ? HealthCheckResult.Healthy("Operational database is reachable")
            : HealthCheckResult.Degraded(
                "Operational database is unreachable or its circuit breaker is open; " +
                "degraded, not failing, since it is optional and read-only");
    }
}
