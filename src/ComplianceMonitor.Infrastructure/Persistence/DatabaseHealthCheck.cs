using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ComplianceMonitor.Infrastructure.Persistence;

/// <summary>
/// Readiness: the database can be reached and its schema is up to date. Deliberately no Hugging Face call: an
/// upstream outage shouldn't take every instance out of service, and /analyze already reports it as 503.
/// </summary>
public sealed class DatabaseHealthCheck(ComplianceDbContext db) : IHealthCheck
{
    public const string Name = "database";
    public const string ReadyTag = "ready";

    private readonly ComplianceDbContext _db = db;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await _db.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("The database can't be reached.");
            }

            var pending = (await _db.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
            return pending == 0
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"{pending} database migration(s) not applied.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("The database can't be reached.");
        }
    }
}
