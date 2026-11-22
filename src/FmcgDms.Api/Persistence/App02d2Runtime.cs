using Dapper;
using FmcgDms.Api.Tenancy;

namespace FmcgDms.Api.Persistence;

internal static class App02d2Runtime
{
    internal static bool Enabled(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!configuration.GetValue<bool>("App02D2:Enabled")) return false;
        if (!App02d1Runtime.Enabled(configuration, environment))
            throw new InvalidOperationException("D.2 requires the guarded D.1 runtime.");
        return true;
    }

    internal static async Task VerifyTenantCatalog(TenantConnections connections, CancellationToken ct)
    {
        foreach (string tenant in new[] { "tenant-a", "tenant-b" })
        {
            await using var db = await connections.OpenApp02c4Async(App02a3Sql.WorkerPrincipal(tenant), ct);
            if (await db.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM dbo.VersionInfo WHERE Version=202611220008", cancellationToken: ct)) != 1)
                throw new InvalidOperationException("D.2 requires its migration in each owned tenant database.");
        }
    }
}
