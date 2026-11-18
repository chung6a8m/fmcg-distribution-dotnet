using Microsoft.Data.SqlClient;

namespace FmcgDms.Preprod.Tenancy;

/// <summary>
/// Offline DBA-controlled, tenant-fenced ownership finalizer. A separate
/// per-tenant Vault credential carries no CREATE DATABASE or broad server
/// privileges. A SQL-authoritative 5-minute lease gates every completion.
/// </summary>
public sealed class SqlTenantFinalizer(TenantRegistrySql registry,string finalizerMasterConnection)
{
    internal Func<SqlConnection,CancellationToken,Task>? InitializeCiFinalizerSession {get;init;}

    public async Task<TenantRegistryEntry> FinalizeAsync(Guid tenantId,CancellationToken ct)
    {
        var token=Guid.NewGuid();
        var claim=await registry.ClaimFinalizationAsync(tenantId,token,ct);
        try
        {
            if(claim.State!="FINALIZING" || claim.TenantId!=tenantId
               || claim.DatabaseName!=TenantRegistryEntry.ExpectedDatabase(tenantId)
               || claim.FinalizationLeaseToken!=token || claim.FinalizationOperationId is null
               || claim.BindingId==Guid.Empty || claim.BindingVersion<1
               || string.IsNullOrWhiteSpace(claim.ApprovedOwnerLogin)
               || string.IsNullOrWhiteSpace(claim.ApprovedProvisionerLogin)
               || string.IsNullOrWhiteSpace(claim.ApprovedFinalizerLogin)
               || string.Equals(claim.ApprovedFinalizerLogin,claim.ApprovedProvisionerLogin,
                    StringComparison.OrdinalIgnoreCase)
               || string.Equals(claim.ApprovedFinalizerLogin,claim.ApprovedOwnerLogin,
                    StringComparison.OrdinalIgnoreCase))
                throw new TenantLifecycleDeniedException("TENANT_FINALIZER_BINDING_DENIED");

            var builder=new SqlConnectionStringBuilder(finalizerMasterConnection);
            if(!string.Equals(builder.InitialCatalog,"master",StringComparison.OrdinalIgnoreCase)
               || !string.IsNullOrEmpty(builder.AttachDBFilename))
                throw new TenantLifecycleDeniedException("TENANT_FINALIZER_MASTER_SCOPE_DENIED");
            await using var master=new SqlConnection(builder.ConnectionString);
            await master.OpenAsync(ct);
            if(InitializeCiFinalizerSession is not null)
                await InitializeCiFinalizerSession(master,ct);
            // A tenant-scoped TAKE OWNERSHIP grant and an independently DBA-approved
            // IMPERSONATE grant are sufficient. No ALTER ANY DATABASE / sysadmin.
            const string principalSql=@"SELECT CASE WHEN
                CONVERT(sysname,SUSER_SNAME())=@finalizer
                AND IS_SRVROLEMEMBER('sysadmin')=0 AND IS_SRVROLEMEMBER('dbcreator')=0
                AND COALESCE(HAS_PERMS_BY_NAME(NULL,NULL,'ALTER ANY DATABASE'),1)=0
                AND COALESCE(HAS_PERMS_BY_NAME(NULL,NULL,'CONTROL SERVER'),1)=0
                AND SUSER_SID(@owner) IS NOT NULL AND SUSER_SID(@provisioner) IS NOT NULL
                AND SUSER_SID(@owner)<>SUSER_SID(@provisioner)
                THEN 1 ELSE 0 END";
            await using(var permission=new SqlCommand(principalSql,master))
            {
                permission.Parameters.AddWithValue("@finalizer",claim.ApprovedFinalizerLogin);
                permission.Parameters.AddWithValue("@owner",claim.ApprovedOwnerLogin);
                permission.Parameters.AddWithValue("@provisioner",claim.ApprovedProvisionerLogin);
                if(Convert.ToInt32(await permission.ExecuteScalarAsync(ct) ?? 0)!=1)
                    throw new TenantLifecycleDeniedException("TENANT_FINALIZER_SQL_IDENTITY_DENIED");
            }
            var resource="FMCG_B4_FINALIZE_"+claim.DatabaseName;
            await using(var lockCmd=new SqlCommand(
                "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@name,@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=15000; SELECT @r",master))
            {
                lockCmd.Parameters.AddWithValue("@name",resource);
                if(Convert.ToInt32(await lockCmd.ExecuteScalarAsync(ct))<0)
                    throw new TenantLifecycleDeniedException("TENANT_FINALIZER_DDL_LOCKED");
            }
            try
            {
                const string ownership=@"DECLARE @actual varbinary(85),@owner varbinary(85),
                        @previous varbinary(85);
                    SELECT @actual=owner_sid FROM sys.databases WHERE name=@db;
                    SELECT @owner=SUSER_SID(@approvedOwner),
                        @previous=SUSER_SID(@provisioner);
                    IF @actual IS NULL OR @owner IS NULL OR @previous IS NULL
                        OR @owner=@previous
                        THROW 51521,'TENANT_FINALIZER_OWNER_UNKNOWN',1;
                    IF @actual=@previous
                    BEGIN
                        DECLARE @ddl nvarchar(max)=N'ALTER AUTHORIZATION ON DATABASE::'
                            +QUOTENAME(@db)+N' TO '+QUOTENAME(@approvedOwner);
                        EXEC(@ddl);
                    END
                    ELSE IF @actual<>@owner
                        THROW 51522,'TENANT_FINALIZER_FOREIGN_OWNER_DENIED',1;
                    IF NOT EXISTS (SELECT 1 FROM sys.databases
                        WHERE name=@db AND owner_sid=@owner)
                        THROW 51523,'TENANT_FINALIZER_OWNER_READBACK_DENIED',1;";
                await using var cmd=new SqlCommand(ownership,master){CommandTimeout=90};
                cmd.Parameters.AddWithValue("@db",claim.DatabaseName);
                cmd.Parameters.AddWithValue("@approvedOwner",claim.ApprovedOwnerLogin);
                cmd.Parameters.AddWithValue("@provisioner",claim.ApprovedProvisionerLogin);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            finally
            {
                await using var release=new SqlCommand(
                    "EXEC sys.sp_releaseapplock @Resource=@name,@LockOwner='Session'",master);
                release.Parameters.AddWithValue("@name",resource);
                await release.ExecuteNonQueryAsync(CancellationToken.None);
            }
            // This is NOT completion. The independent DBA-scoped retirement
            // operator must revoke TAKE OWNERSHIP and IMPERSONATE, retire the
            // finalizer login and kill active SQL sessions before it can commit
            // FINALIZING -> VERIFYING. No post-transfer self-attestation.
            var pending=await registry.GetAsync(tenantId,ct)
                ?? throw new TenantLifecycleDeniedException("TENANT_FINALIZER_READBACK_DENIED");
            if(pending.State!="FINALIZING" || pending.FinalizationLeaseToken!=token)
                throw new TenantLifecycleDeniedException("TENANT_FINALIZER_LEASE_SUPERSEDED");
            return pending;
        }
        catch
        {
            // No auto DROP or old-identity fallback. A post-DDL crash is resumed
            // with the same durable finalization operation and a new fenced lease.
            try {await registry.FailFinalizationAsync(tenantId,token,
                "FINALIZER_FAILED",CancellationToken.None);}
            catch(Exception){/* Preserve original failure; do not break the fence. */}
            throw;
        }
    }
}
