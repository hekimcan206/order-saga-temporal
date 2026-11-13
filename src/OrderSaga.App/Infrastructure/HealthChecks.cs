using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Temporalio.Client;

namespace OrderSaga.App.Infrastructure;

/// <summary>Ready only when the Temporal frontend answers: without it no order can be placed or progressed.</summary>
internal sealed class TemporalHealthCheck(ITemporalClient client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await client.Connection.CheckHealthAsync(options: new() { CancellationToken = timeout.Token });
            return HealthCheckResult.Healthy();
        }
#pragma warning disable CA1031 // A health check reports any failure as unhealthy instead of throwing.
        catch (Exception)
#pragma warning restore CA1031
        {
            return HealthCheckResult.Unhealthy("temporal unreachable");
        }
    }
}

internal sealed class DatabaseHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var cmd = dataSource.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (NpgsqlException)
        {
            return HealthCheckResult.Unhealthy("database unreachable");
        }
    }
}
