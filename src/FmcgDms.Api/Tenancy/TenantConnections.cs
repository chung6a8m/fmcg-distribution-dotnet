using System.Security.Claims;
using System.Text.RegularExpressions;
using Dapper;
using FmcgDms.Api.Authentication;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Tenancy;

/// <summary>Server-side catalog for APP-00A synthetic diagnostics only; not production tenant access.</summary>
public sealed class TenantConnections(IConfiguration config, IHostEnvironment host)
{
    private static readonly Regex ScratchDatabaseName = new(
        @"\AFmcg_App00_(?<nonce>[a-f0-9]{32})_(?<slot>[AB])\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string? clockPath = App02c6Clock.GetConfiguredPath(config, host);
    internal bool UsesApp02c6Clock => clockPath is not null;
    internal bool UsesApp02d1 { get; } = App02d1Runtime.Enabled(config, host);

    internal bool UsesApp02d2 { get; } = App02d2Runtime.Enabled(config, host);
    internal bool UsesApp02d3 { get; } = App02d3Runtime.Enabled(config, host);

    private string Resolve(ClaimsPrincipal principal)
    {
        string tenantId = SyntheticAuth.GetTenantId(principal);
        if (!Regex.IsMatch(tenantId, @"\A[a-zA-Z0-9_-]{1,64}\z"))
            throw new UnauthorizedAccessException("Invalid tenant identity.");
        string? connectionString = config[$"TenantDatabases:{tenantId}"];
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new UnauthorizedAccessException("Unknown tenant.");
        return connectionString;
    }

    /// <summary>
    /// Fail closed unless the signed synthetic tenant maps to its own, marked LocalDB scratch.
    /// Production APP-01 tenant connections must use a separate real-authenticated path.
    /// </summary>
    public Task<SqlConnection> OpenSyntheticScratchAsync(ClaimsPrincipal principal, CancellationToken ct) =>
        OpenScratchAsync(principal, ct, c1: false);

    internal Task<SqlConnection> OpenApp02c1Async(ClaimsPrincipal principal, CancellationToken ct) =>
        OpenScratchAsync(principal, ct, c1: true);

    internal Task<SqlConnection> OpenApp02c2Async(ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!config.GetValue<bool>("App02C2:Enabled"))
            throw new UnauthorizedAccessException("C.2 DEV commands are disabled.");
        return OpenApp02c1Async(principal, ct);
    }

    internal Task<SqlConnection> OpenApp02c3Async(ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!config.GetValue<bool>("App02C3:Enabled"))
            throw new UnauthorizedAccessException("C.3 DEV commands are disabled.");
        return OpenApp02c2Async(principal, ct);
    }

    internal Task<SqlConnection> OpenApp02c4Async(ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!config.GetValue<bool>("App02C4:Enabled"))
            throw new UnauthorizedAccessException("C.4 DEV commands are disabled.");
        return OpenApp02c3Async(principal, ct);
    }

    private async Task<SqlConnection> OpenScratchAsync(ClaimsPrincipal principal, CancellationToken ct, bool c1)
    {
        if ((!host.IsDevelopment() && !host.IsEnvironment("IntegrationTest"))
            || !config.GetValue<bool>("SyntheticAuth:Enabled"))
            throw new UnauthorizedAccessException("Synthetic diagnostics are disabled.");

        string tenantId = SyntheticAuth.GetTenantId(principal);
        if (tenantId is not ("tenant-a" or "tenant-b"))
            throw new UnauthorizedAccessException("Only synthetic test tenants are allowed.");

        var target = new SqlConnectionStringBuilder(Resolve(principal));
        var name = ScratchDatabaseName.Match(target.InitialCatalog);
        string expectedSlot = tenantId == "tenant-a" ? "A" : "B";
        if (!target.DataSource.StartsWith(@"(localdb)\", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(target.AttachDBFilename)
            || !name.Success
            || !string.Equals(name.Groups["slot"].Value, expectedSlot, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Synthetic diagnostics require a LocalDB scratch database.");

        string expectedNonce = name.Groups["nonce"].Value;
        if ((c1 || UsesApp02c6Clock) && (!config.GetValue<bool>("App02C1:Enabled")
            || !string.Equals(target.DataSource, @"(localdb)\MSSQLLocalDB", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(config["App02C1:Nonce"], expectedNonce, StringComparison.Ordinal)
            || !target.IntegratedSecurity || target.UserInstance
            || !string.IsNullOrEmpty(target.FailoverPartner)))
            throw new UnauthorizedAccessException("C.1 requires its explicitly owned DEV session.");

        // A C.6 connection has one immutable test instant for its lifetime.
        if (clockPath is not null)
        {
            target.Pooling = false;
            target.MultipleActiveResultSets = false;
        }
        var connection = new SqlConnection(target.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            string? markerNonce = await connection.ExecuteScalarAsync<string?>(
                new CommandDefinition(
                    "IF OBJECT_ID(N'dbo.App00ScratchMarker', N'U') IS NOT NULL SELECT TOP(1) Nonce FROM dbo.App00ScratchMarker",
                    cancellationToken: ct));
            if (!string.Equals(markerNonce, expectedNonce, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Scratch ownership marker mismatch.");

            string? boundTenant = await connection.QuerySingleOrDefaultAsync<string>(
                new CommandDefinition(
                    "SELECT TenantId FROM dbo.AppTenantBinding WHERE Slot=1",
                    cancellationToken: ct));
            if (!string.Equals(boundTenant, tenantId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Tenant is not bound to this database.");
            if (clockPath is not null)
                await App02c6Clock.SetSessionAsync(connection, clockPath, ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
