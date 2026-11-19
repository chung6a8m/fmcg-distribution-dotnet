using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Preprod.Tenancy;

/// <summary>
/// Independently opens a tenant-scoped SQL credential and attempts denied
/// reads of the peer/control DB and writes to the own immutable marker.
/// A human verifier must have a different authenticated Control Plane login
/// from the provisioner and approval actor (enforced by SQL procedures).
/// </summary>
public sealed class TenantProbeVerifier(TenantRegistrySql registry)
{
    // Test-only: SQL EXECUTE AS LOGIN after opening a disposable LocalDB connection.
    // This never changes how the public operator CLI authenticates.
    internal Func<SqlConnection,CancellationToken,Task>? InitializeCiScopedSession { get; init; }

    public async Task<TenantRegistryEntry> VerifyAsync(Guid tenantId,
        TenantVerifierProbeSecrets scope, CancellationToken ct)
    {
        var entry=await registry.GetAsync(tenantId,ct)
            ?? throw new TenantLifecycleDeniedException("TENANT_UNKNOWN");
        if (entry.State is not ("VERIFYING" or "REACTIVATION_VERIFYING")
            || entry.DatabaseName!=TenantRegistryEntry.ExpectedDatabase(tenantId))
            throw new TenantLifecycleDeniedException("TENANT_VERIFICATION_STATE_DENIED");
        var registryCs=new SqlConnectionStringBuilder(registry.ConnectionString);
        if(string.IsNullOrWhiteSpace(scope.ApprovedOwnerLogin)
            || string.IsNullOrWhiteSpace(scope.ProvisionerLogin)
            || string.Equals(scope.ApprovedOwnerLogin,scope.ProvisionerLogin,
                StringComparison.OrdinalIgnoreCase))
            throw new TenantLifecycleDeniedException("TENANT_DB_OWNER_APPROVAL_MISSING");
        var probeCs=new SqlConnectionStringBuilder(scope.ConnectionString);
        if (!string.Equals(probeCs.DataSource,registryCs.DataSource,StringComparison.OrdinalIgnoreCase)
            || !string.Equals(probeCs.InitialCatalog,entry.DatabaseName,StringComparison.OrdinalIgnoreCase)
            || (probeCs.IntegratedSecurity && registryCs.IntegratedSecurity
                && InitializeCiScopedSession is null)
            || (!probeCs.IntegratedSecurity && string.Equals(probeCs.UserID,registryCs.UserID,
                StringComparison.OrdinalIgnoreCase)))
            throw new TenantLifecycleDeniedException("TENANT_PROBE_IDENTITY_NOT_SCOPED");

        await using var registryDb=new SqlConnection(registry.ConnectionString);
        await registryDb.OpenAsync(ct);
        await registry.OpenCiSessionAsync(registryDb,ct);
        // Fail closed until a separately authorized DBA transfers the DB away
        // from its original CREATE DATABASE principal. The verifier never performs DDL.
        await AssertApprovedOwnerAsync(registryDb,entry,scope,ct);
        string? peer;
        await using(var lookup=new SqlCommand("dbo.cp_GetPeerForVerification",registryDb)
            {CommandType=CommandType.StoredProcedure})
        {
            lookup.Parameters.AddWithValue("@TenantId",tenantId);
            peer=(string?)await lookup.ExecuteScalarAsync(ct);
        }
        // A second physical tenant is required for meaningful negative probes.
        if(peer is null || peer==entry.DatabaseName)
            throw new TenantLifecycleDeniedException("TENANT_PEER_PROBE_MISSING");
        // Denied SQL access is not isolation proof if the peer DB is absent.
        await using(var exists = new SqlCommand("SELECT DB_ID(@peer)",registryDb))
        {
            exists.Parameters.AddWithValue("@peer",peer);
            if(await exists.ExecuteScalarAsync(ct) is DBNull or null)
                throw new TenantLifecycleDeniedException("TENANT_PHYSICAL_PEER_MISSING");
        }

        await using var own=new SqlConnection(probeCs.ConnectionString);
        await own.OpenAsync(ct);
        if(InitializeCiScopedSession is not null)
            await InitializeCiScopedSession(own,ct);
        if(!await SqlTenantProvisioner.HasApprovedMarkerAsync(own,entry,ct))
            throw new TenantLifecycleDeniedException("TENANT_PROBE_MARKER_MISMATCH");
        var identity=await ReadIdentityAsync(own,ct);
        if(identity.Length==0) throw new TenantLifecycleDeniedException("TENANT_PROBE_UNKNOWN_IDENTITY");
        await AssertDeniedAsync(probeCs,peer,
            "SELECT COUNT_BIG(*) FROM dbo.__PreprodTenantBinding",ct);
        await AssertDeniedAsync(probeCs,registryCs.InitialCatalog,
            "SELECT COUNT_BIG(*) FROM dbo.PreprodTenantRegistry",ct);
        // Even the own-tenant marker must be READ ONLY for this identity.
        try
        {
            await using var mutation=new SqlCommand(
                "UPDATE dbo.__PreprodTenantBinding SET BindingVersion=BindingVersion WHERE 1=0",own);
            await mutation.ExecuteNonQueryAsync(ct);
            throw new TenantLifecycleDeniedException("TENANT_PROBE_WRITE_PERMITTED");
        }
        catch(SqlException ex) when(IsSqlPermissionDenied(ex))
        {
            // Expected: a distinct least-privilege SQL identity, not dbo/sysadmin.
        }

        var witness=SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{tenantId:N}|{entry.BindingId:N}|{entry.BindingVersion}|{entry.SecurityEpoch}|{entry.DatabaseName}|{peer}|{identity}|{scope.ApprovedOwnerLogin}|{scope.ProvisionerLogin}|{TenantSchemaVersions.Tenant}"));
        return await registry.VerifyAsync(tenantId,entry.RowVersion,entry.BindingId,witness,ct);
    }

    private static async Task AssertApprovedOwnerAsync(SqlConnection control,
        TenantRegistryEntry entry,TenantVerifierProbeSecrets scope,CancellationToken ct)
    {
        // The owner and original DDL-login identifiers are explicitly pinned in
        // the independently ACL-scoped per-tenant verifier Vault record.
        // SQL metadata (not an application-supplied assertion) is authoritative.
        const string sql=@"SELECT CASE WHEN d.owner_sid=SUSER_SID(@approved)
              AND d.owner_sid<>SUSER_SID(@provisioner)
              AND SUSER_SID(@approved) IS NOT NULL
              AND SUSER_SID(@provisioner) IS NOT NULL
              AND IS_SRVROLEMEMBER('sysadmin',@approved)=0
              AND IS_SRVROLEMEMBER('dbcreator',@approved)=0
              AND d.owner_sid<>SUSER_SID(SUSER_SNAME())
              THEN 1 ELSE 0 END
            FROM sys.databases AS d WHERE d.name=@db";
        await using var command=new SqlCommand(sql,control);
        command.Parameters.AddWithValue("@approved",scope.ApprovedOwnerLogin);
        command.Parameters.AddWithValue("@provisioner",scope.ProvisionerLogin);
        command.Parameters.AddWithValue("@db",entry.DatabaseName);
        if(Convert.ToInt32(await command.ExecuteScalarAsync(ct) ?? 0)!=1)
            throw new TenantLifecycleDeniedException("TENANT_DB_OWNER_NOT_ATTESTED");
    }

    private static async Task<string> ReadIdentityAsync(SqlConnection conn,CancellationToken ct)
    {
        await using var c=new SqlCommand("SELECT CONVERT(nvarchar(128),SUSER_SNAME())",conn);
        return (string?)await c.ExecuteScalarAsync(ct) ?? "";
    }

    private async Task AssertDeniedAsync(SqlConnectionStringBuilder baseCs,
        string dbName, string sql, CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(dbName))
            throw new TenantLifecycleDeniedException("TENANT_PROBE_DATABASE_INVALID");
        var cs=new SqlConnectionStringBuilder(baseCs.ConnectionString) {InitialCatalog=dbName};
        try
        {
            await using var conn=new SqlConnection(cs.ConnectionString);
            await conn.OpenAsync(ct);
            if(InitializeCiScopedSession is not null)
                await InitializeCiScopedSession(conn,ct);
            await using var command=new SqlCommand(sql,conn);
            await command.ExecuteScalarAsync(ct);
            throw new TenantLifecycleDeniedException("TENANT_PROBE_CROSS_SCOPE_PERMITTED");
        }
        catch(SqlException ex) when(IsSqlPermissionDenied(ex))
        {
            // The connection or read must be denied by the actual SQL engine.
        }
    }

    private static bool IsSqlPermissionDenied(SqlException ex) =>
        ex.Errors.Cast<SqlError>().Any(x=>x.Number is 916 or 229 or 4060 or 18456);
}
