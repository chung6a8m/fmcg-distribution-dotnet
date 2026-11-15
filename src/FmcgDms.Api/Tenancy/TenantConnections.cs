using System.Security.Claims;
using Dapper;
using FmcgDms.Api.Authentication;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Tenancy;

/// <summary>Server-side tenant catalog. No client-selected DB or connection string.</summary>
public sealed class TenantConnections(IConfiguration config)
{
    public string Resolve(ClaimsPrincipal principal)
    {
        string tenantId = SyntheticAuth.GetTenantId(principal);
        if (!System.Text.RegularExpressions.Regex.IsMatch(tenantId, @"\A[a-zA-Z0-9_-]{1,64}\z"))
            throw new UnauthorizedAccessException("Invalid tenant identity.");
        string? connectionString = config[$"TenantDatabases:{tenantId}"];
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new UnauthorizedAccessException("Unknown tenant.");
        return connectionString;
    }

    public async Task<SqlConnection> OpenAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        string tenantId = SyntheticAuth.GetTenantId(principal);
        var connection = new SqlConnection(Resolve(principal));
        try
        {
            await connection.OpenAsync(ct);
            // Enforce the signed principal to DB binding inside the shared connection factory,
            // not only in individual endpoints which future handlers might forget.
            string? boundTenant = await connection.QuerySingleOrDefaultAsync<string>(
                new CommandDefinition(
                    "SELECT TenantId FROM dbo.AppTenantBinding WHERE Slot=1",
                    cancellationToken: ct));
            if (!string.Equals(boundTenant, tenantId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Tenant is not bound to this database.");
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
