using Microsoft.Data.SqlClient;

namespace FmcgDms.Preprod.Tenancy;

/// <summary>
/// Privileged operator automation; never called from API or Worker. A crash
/// after CREATE DATABASE but before binding leaves a quarantined, non-routable
/// database for explicit DBA remediation. No automatic adoption/drop.
/// </summary>
public sealed class SqlTenantProvisioner(TenantRegistrySql registry,
    string approvedMasterConnectionString)
{
    // CI-only SQL EXECUTE AS LOGIN: the real operator opens its own restricted
    // CREATE ANY DATABASE SQL principal and never uses this internal hook.
    internal Func<SqlConnection,CancellationToken,Task>? InitializeCiDdlSession { get; init; }

    public async Task<TenantRegistryEntry> ProvisionAsync(Guid tenantId, CancellationToken ct)
    {
        var token=Guid.NewGuid();
        var claim=await registry.ClaimAsync(tenantId,token,ct);
        try
        {
            if (tenantId==Guid.Empty || claim.DatabaseName!=TenantRegistryEntry.ExpectedDatabase(tenantId)
                || claim.BindingId==Guid.Empty || claim.BindingVersion<1 || claim.LeaseToken!=token
                || claim.State!="PROVISIONING")
                throw new TenantLifecycleDeniedException("TENANT_REGISTRY_BINDING_DENIED");

            var masterBuilder=new SqlConnectionStringBuilder(approvedMasterConnectionString);
            if (!string.Equals(masterBuilder.InitialCatalog,"master",StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(masterBuilder.AttachDBFilename))
                throw new TenantLifecycleDeniedException("TENANT_ADMIN_CONNECTION_DENIED");

            await using var master=new SqlConnection(masterBuilder.ConnectionString);
            await master.OpenAsync(ct);
            if(InitializeCiDdlSession is not null)
                await InitializeCiDdlSession(master,ct);
            await using(var permission=new SqlCommand(@"SELECT CASE WHEN
                IS_SRVROLEMEMBER('sysadmin')=0
                AND IS_SRVROLEMEMBER('dbcreator')=0
                AND HAS_PERMS_BY_NAME(NULL,'SERVER','CREATE ANY DATABASE')=1
                AND HAS_PERMS_BY_NAME(NULL,'SERVER','ALTER ANY DATABASE')=0
                AND HAS_PERMS_BY_NAME(NULL,'SERVER','CONTROL SERVER')=0
                THEN 1 ELSE 0 END",master))
            {
                if(Convert.ToInt32(await permission.ExecuteScalarAsync(ct) ?? 0)!=1)
                    throw new TenantLifecycleDeniedException("TENANT_DDL_PRINCIPAL_OVERPRIVILEGED");
            }
            var lockKey="FMCG_B4_"+claim.DatabaseName;
            await using(var lockCmd=new SqlCommand(
                "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@name,@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=15000; SELECT @r",master))
            {
                lockCmd.Parameters.AddWithValue("@name",lockKey);
                if(Convert.ToInt32(await lockCmd.ExecuteScalarAsync(ct))<0)
                    throw new TenantLifecycleDeniedException("TENANT_DDL_LOCK_DENIED");
            }
            try
            {
                bool existed;
                await using(var check=new SqlCommand(
                    "SELECT CASE WHEN DB_ID(@name) IS NULL THEN 0 ELSE 1 END",master))
                {
                    check.Parameters.AddWithValue("@name",claim.DatabaseName);
                    existed=Convert.ToInt32(await check.ExecuteScalarAsync(ct))!=0;
                }
                if(!existed)
                {
                    // No interpolated caller input; DB identifier is always a
                    // known prefix plus GUID with 32 hex characters.
                    await using var create=new SqlCommand(
                        $"CREATE DATABASE [{claim.DatabaseName}]",master) { CommandTimeout=120 };
                    await create.ExecuteNonQueryAsync(ct);
                }
                var target=new SqlConnectionStringBuilder(masterBuilder.ConnectionString)
                    { InitialCatalog=claim.DatabaseName };
                await using var tenant=new SqlConnection(target.ConnectionString);
                await tenant.OpenAsync(ct);
                if(InitializeCiDdlSession is not null)
                    await InitializeCiDdlSession(tenant,ct);
                if(existed && !await HasApprovedMarkerAsync(tenant,claim,ct))
                    throw new TenantLifecycleDeniedException("TENANT_UNMARKED_DATABASE_QUARANTINED");
                await ApplyMarkerMigrationAsync(tenant,claim,ct);
                if(!await HasApprovedMarkerAsync(tenant,claim,ct))
                    throw new TenantLifecycleDeniedException("TENANT_MARKER_READBACK_DENIED");
            }
            finally
            {
                await using var release=new SqlCommand(
                    "EXEC sys.sp_releaseapplock @Resource=@name,@LockOwner='Session'",master);
                release.Parameters.AddWithValue("@name",lockKey);
                await release.ExecuteNonQueryAsync(CancellationToken.None);
            }
            await registry.CompleteAsync(tenantId,token,ct);
            return await registry.GetAsync(tenantId,ct)
                ?? throw new TenantLifecycleDeniedException("TENANT_REGISTRY_UNAVAILABLE");
        }
        catch
        {
            // Even on failure never DROP or adopt an unknown database. If a
            // competing lease has superseded ours, SQL refuses the stale write.
            try { await registry.FailAsync(tenantId,token,"PROVISION_FAILED",CancellationToken.None); }
            catch (Exception) { /* Original failure kept; no secondary fence override. */ }
            throw;
        }
    }

    public static async Task<bool> HasApprovedMarkerAsync(SqlConnection tenant,
        TenantRegistryEntry entry, CancellationToken ct)
    {
        const string sql=@"IF OBJECT_ID(N'dbo.__PreprodTenantBinding',N'U') IS NULL
                             OR OBJECT_ID(N'dbo.__PreprodTenantSchemaHistory',N'U') IS NULL
                           SELECT 0;
                           ELSE SELECT CASE WHEN DB_NAME()=@db
                               AND (SELECT COUNT_BIG(*) FROM dbo.__PreprodTenantBinding
                                   WHERE TenantId=@tenant AND BindingId=@binding
                                   AND BindingVersion=@version)=1
                               AND (SELECT COUNT_BIG(*) FROM dbo.__PreprodTenantBinding)=1
                               AND (SELECT COUNT_BIG(*) FROM dbo.__PreprodTenantSchemaHistory
                                    WHERE SchemaVersion=@schema)=1
                               AND (SELECT COUNT_BIG(*) FROM dbo.__PreprodTenantSchemaHistory)=1
                               THEN 1 ELSE 0 END;";
        await using var c=new SqlCommand(sql,tenant);
        c.Parameters.AddWithValue("@db",entry.DatabaseName);
        c.Parameters.AddWithValue("@tenant",entry.TenantId);
        c.Parameters.AddWithValue("@binding",entry.BindingId);
        c.Parameters.AddWithValue("@version",entry.BindingVersion);
        c.Parameters.AddWithValue("@schema",TenantSchemaVersions.Tenant);
        return Convert.ToInt32(await c.ExecuteScalarAsync(ct))==1;
    }

    private static async Task ApplyMarkerMigrationAsync(SqlConnection db,
        TenantRegistryEntry entry, CancellationToken ct)
    {
        var asm=typeof(SqlTenantProvisioner).Assembly;
        var name=asm.GetManifestResourceNames().Single(x=>
            x.EndsWith("20261118_001_tenant_marker.sql",StringComparison.Ordinal));
        await using var stream=asm.GetManifestResourceStream(name)!;
        using var reader=new StreamReader(stream);
        var script=await reader.ReadToEndAsync(ct);
        await using var c=new SqlCommand(script,db) {CommandTimeout=90};
        c.Parameters.AddWithValue("@TenantId",entry.TenantId);
        c.Parameters.AddWithValue("@BindingId",entry.BindingId);
        c.Parameters.AddWithValue("@BindingVersion",entry.BindingVersion);
        await c.ExecuteNonQueryAsync(ct);
    }
}
