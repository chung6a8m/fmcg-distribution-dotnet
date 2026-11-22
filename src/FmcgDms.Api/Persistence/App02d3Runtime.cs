using Dapper;
using FmcgDms.Api.Tenancy;

namespace FmcgDms.Api.Persistence;

internal static class App02d3Runtime
{
    // Vehicle stock reuses one source balance across trips. Include completed
    // modern returns in addition to legacy returns in each posting reader.
    internal static string VehicleReturns(bool enabled) => enabled ? """
        UNION ALL
        SELECT ul.BaseQuantity,ul.CostAmount
        FROM dbo.D3TripUnloadLine ul WITH (HOLDLOCK)
        WHERE ul.TenantId=b.TenantId AND ul.VehicleLocationId=b.StockLocationId
            AND ul.SkuId=b.SkuId
        """ : "";

    internal static bool Enabled(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!configuration.GetValue<bool>("App02D3:Enabled")) return false;
        if (!App02d2Runtime.Enabled(configuration, environment))
            throw new InvalidOperationException("D.3 requires the guarded D.2 runtime.");
        return true;
    }

    internal static async Task VerifyTenantCatalog(TenantConnections connections, CancellationToken ct)
    {
        foreach (string tenant in new[] { "tenant-a", "tenant-b" })
        {
            await using var db = await connections.OpenApp02c4Async(App02a3Sql.WorkerPrincipal(tenant), ct);
            if (await db.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(*) FROM dbo.VersionInfo WHERE Version IN (202611220009,202611220010)",
                    cancellationToken: ct)) != 2)
                throw new InvalidOperationException("D.3 requires its migrations in each owned tenant database.");
        }
    }
}
