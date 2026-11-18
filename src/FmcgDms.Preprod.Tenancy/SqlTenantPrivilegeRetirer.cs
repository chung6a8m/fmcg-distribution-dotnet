using Microsoft.Data.SqlClient;

namespace FmcgDms.Preprod.Tenancy;

/// <summary>
/// Independently credentialed, DBA-owned *retirement* operation. Never shares
/// Vault token or a SQL connection with the finalizer. Finalization remains
/// non-routable until this actor revokes effective DDL rights and SQL records
/// an independently checked owner/binding/epoch retirement witness.
/// </summary>
public sealed class SqlTenantPrivilegeRetirer(TenantRegistrySql registry,string dbaMasterConnectionString)
{
    internal Func<SqlConnection,CancellationToken,Task>? InitializeCiRetirerSession {get;init;}

    public async Task<TenantRegistryEntry> RetireAsync(Guid tenantId,CancellationToken ct)
    {
        var entry=await registry.GetAsync(tenantId,ct)
            ?? throw new TenantLifecycleDeniedException("TENANT_RETIRER_UNKNOWN");
        if(tenantId==Guid.Empty || entry.State!="FINALIZING"
           || entry.DatabaseName!=TenantRegistryEntry.ExpectedDatabase(tenantId)
           || entry.BindingId==Guid.Empty || entry.FinalizationOperationId is null
           || entry.FinalizationLeaseToken is null
           || string.IsNullOrWhiteSpace(entry.ApprovedOwnerLogin)
           || string.IsNullOrWhiteSpace(entry.ApprovedProvisionerLogin)
           || string.IsNullOrWhiteSpace(entry.ApprovedFinalizerLogin)
           || string.IsNullOrWhiteSpace(entry.ApprovedRetirerLogin)
           || entry.ApprovedRetirerLogin==entry.ApprovedFinalizerLogin)
            throw new TenantLifecycleDeniedException("TENANT_RETIRER_BINDING_DENIED");

        var builder=new SqlConnectionStringBuilder(dbaMasterConnectionString);
        if(!builder.InitialCatalog.Equals("master",StringComparison.OrdinalIgnoreCase)
           || !string.IsNullOrEmpty(builder.AttachDBFilename))
            throw new TenantLifecycleDeniedException("TENANT_RETIRER_MASTER_SCOPE_DENIED");

        await using(var master=new SqlConnection(builder.ConnectionString))
        {
            await master.OpenAsync(ct);
            if(InitializeCiRetirerSession is not null)
                await InitializeCiRetirerSession(master,ct);
            await using(var actorCheck=new SqlCommand(
                "SELECT CONVERT(sysname,SUSER_SNAME())",master))
            {
                var actor=(string?)await actorCheck.ExecuteScalarAsync(ct);
                if(!string.Equals(actor,entry.ApprovedRetirerLogin,StringComparison.OrdinalIgnoreCase))
                    throw new TenantLifecycleDeniedException("TENANT_RETIRER_PRINCIPAL_DENIED");
            }

            // The DBA operator must have authority for these scoped steps.
            // One SQL command revalidates owner SID against SQL-frozen login,
            // then disables the finalizer and removes BOTH durable DDL grants.
            // Failure at any point leaves registry FINALIZING/non-routable;
            // retry is safe and idempotent, never auto-DROP.
            const string sql=@"SET NOCOUNT ON;
                DECLARE @actual varbinary(85),@approved varbinary(85),@finSid varbinary(85);
                SELECT @actual=owner_sid FROM sys.databases WHERE name=@db;
                SELECT @approved=SUSER_SID(@owner),@finSid=SUSER_SID(@finalizer);
                IF @actual IS NULL OR @approved IS NULL OR @finSid IS NULL
                   OR @actual<>@approved OR @approved=@finSid
                   THROW 51540,'TENANT_RETIRER_OWNER_MISMATCH',1;
                DECLARE @disable nvarchar(max)=N'ALTER LOGIN '+QUOTENAME(@finalizer)+N' DISABLE';
                EXEC(@disable);
                DECLARE @revokeServer nvarchar(max)=N'REVOKE IMPERSONATE ON LOGIN::'
                    +QUOTENAME(@owner)+N' FROM '+QUOTENAME(@finalizer);
                EXEC(@revokeServer);
                DECLARE @dbSql nvarchar(max)=N'USE '+QUOTENAME(@db)
                    +N'; IF USER_ID(@login) IS NOT NULL BEGIN '
                    +N'DECLARE @sql nvarchar(max)=N''REVOKE TAKE OWNERSHIP ON DATABASE::''
                        +QUOTENAME(DB_NAME())+N'' FROM ''+QUOTENAME(@login);'
                    +N' EXEC(@sql); SET @sql=N''DROP USER ''+QUOTENAME(@login); EXEC(@sql); END;';
                EXEC sys.sp_executesql @dbSql,N'@login sysname',@login=@finalizer;
                -- A disabled login does NOT terminate already authenticated
                -- sessions. Terminate them before the registry may advance.
                DECLARE @session int;
                WHILE 1=1
                BEGIN
                    SELECT TOP (1) @session=session_id FROM sys.dm_exec_sessions
                    WHERE login_name=@finalizer AND session_id<>@@SPID
                    ORDER BY session_id;
                    IF @session IS NULL BREAK;
                    EXEC(N'KILL '+CONVERT(nvarchar(11),@session));
                    SET @session=NULL;
                END;";
            await using var cmd=new SqlCommand(sql,master){CommandTimeout=90};
            cmd.Parameters.AddWithValue("@db",entry.DatabaseName);
            cmd.Parameters.AddWithValue("@owner",entry.ApprovedOwnerLogin);
            cmd.Parameters.AddWithValue("@finalizer",entry.ApprovedFinalizerLogin);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        // New independent Control Plane connection: the retired finalizer
        // cannot execute cp_CompleteFinalization or impersonate this actor.
        await registry.CompleteFinalizationAsync(tenantId,
            entry.FinalizationLeaseToken.Value,entry.FinalizationOperationId!.Value,
            entry.BindingId,entry.SecurityEpoch,ct);
        var result=await registry.GetAsync(tenantId,ct)
            ?? throw new TenantLifecycleDeniedException("TENANT_RETIRER_READBACK_DENIED");
        if(result.State!="VERIFYING" ||
           !string.Equals(result.FinalizerRetiredBy,entry.ApprovedRetirerLogin,StringComparison.OrdinalIgnoreCase))
            throw new TenantLifecycleDeniedException("TENANT_RETIRER_READBACK_MISMATCH");
        return result;
    }
}
