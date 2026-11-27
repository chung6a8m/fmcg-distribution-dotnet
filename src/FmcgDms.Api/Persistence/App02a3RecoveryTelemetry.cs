using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

/// <summary>Allowlisted best-effort observations for the synthetic worker only.</summary>
internal static class App02a3RecoveryTelemetry
{
    internal static void Sweep(ILogger logger, string tenant, string phase,
        string point, int count, long elapsedMs)
    {
        try
        {
            logger.LogInformation("REL75_SWEEP tenant={Tenant} phase={Phase} point={Point} count={Count} elapsedMs={ElapsedMs} utcMs={UtcMs}",
                tenant, phase, point, count, elapsedMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch (Exception) { /* A diagnostic sink must not change processing. */ }
    }

    internal static void Technical(ILogger? logger, string tenant, string phase,
        Guid id, Exception exception)
    {
        if (logger is null) return;
        try
        {
            if (exception is SqlException sql)
            {
                foreach (SqlError error in sql.Errors.Cast<SqlError>().Take(8))
                    logger.LogWarning("REL75_TECH tenant={Tenant} phase={Phase} id={Id} kind=sql number={Number} state={State} severity={Severity} utcMs={UtcMs}",
                        tenant, phase, id, error.Number, error.State, error.Class,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
            else if (exception is TimeoutException)
                logger.LogWarning("REL75_TECH tenant={Tenant} phase={Phase} id={Id} kind=timeout number=0 state=0 severity=0 utcMs={UtcMs}",
                    tenant, phase, id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch (Exception) { /* Never replace the exception being diagnosed. */ }
    }
}
