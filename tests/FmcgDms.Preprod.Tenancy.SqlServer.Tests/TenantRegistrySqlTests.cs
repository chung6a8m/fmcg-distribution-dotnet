using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FmcgDms.Preprod.Tenancy;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FmcgDms.Preprod.Tenancy.SqlServer.Tests;

public sealed class TenantRegistrySqlTests
{
    [Fact]
    public async Task Registry_migration_is_replay_safe_and_rejects_unknown_or_partial_schema()
    {
        var ct=TestContext.Current.CancellationToken;
        await using var f=await B4SqlFixture.CreateAsync(ct);
        Assert.Equal(1,Convert.ToInt32(await f.ScalarAsync(f.Control,
            "SELECT COUNT(*) FROM dbo.__PreprodTenantRegistrySchemaHistory WHERE SchemaVersion=N'B4_20261118_001'",ct)));
        Assert.Equal(12,Convert.ToInt32(await f.ScalarAsync(f.Control,
            "SELECT COUNT(*) FROM sys.procedures WHERE schema_id=SCHEMA_ID(N'dbo') AND name LIKE 'cp_%'",ct)));
        await f.ExecAsync(f.Control,
            "UPDATE dbo.__PreprodTenantRegistrySchemaHistory SET SchemaVersion=N'UNKNOWN'",ct);
        await Assert.ThrowsAsync<SqlException>(()=>
            f.ExecAsync(f.Control,B4SqlFixture.ReadRegistryMigration(),ct));
        await f.ExecAsync(f.Control,
            "UPDATE dbo.__PreprodTenantRegistrySchemaHistory SET SchemaVersion=N'B4_20261118_001'",ct);
        await f.ExecAsync(f.Control,B4SqlFixture.ReadRegistryMigration(),ct);

        var partial="FmcgB4Partial_"+Guid.NewGuid().ToString("N")[..12];
        await f.CreateDbAsync(partial,ct);
        var cs=f.Connection(partial);
        await f.ExecAsync(cs,"CREATE TABLE dbo.PreprodTenantRegistry (Id int NOT NULL)",ct);
        await Assert.ThrowsAsync<SqlException>(()=>
            f.ExecAsync(cs,B4SqlFixture.ReadRegistryMigration(),ct));
        Assert.Equal(0,Convert.ToInt32(await f.ScalarAsync(cs,
            "SELECT COUNT(*) FROM sys.tables WHERE name=N'__PreprodTenantRegistrySchemaHistory'",ct)));
        await Assert.ThrowsAsync<SqlException>(()=>
            f.ExecAsync(f.Master,B4SqlFixture.ReadRegistryMigration(),ct));
    }

    [Fact]
    public async Task Two_physical_tenants_require_distinct_approval_and_fenced_activation()
    {
        var ct=TestContext.Current.CancellationToken;
        await using var f=await B4SqlFixture.CreateAsync(ct);
        var tenantA=Guid.NewGuid();
        var tenantB=Guid.NewGuid();
        var orphan=Guid.NewGuid();
        var registry=new TenantRegistrySql(f.Control);
        var ddlLogin=await f.CreateDdlLoginAsync(ct);
        // SQL engine permission profile is asserted under the real isolated
        // CI login, never inferred from the administrator/fixture connection.
        var ddlProfile=Convert.ToString(await f.ScalarAsLoginAsync(ddlLogin,
            @"SELECT CONCAT(COALESCE(CONVERT(varchar(5),IS_SRVROLEMEMBER('sysadmin')),'null'),
                    '|',COALESCE(CONVERT(varchar(5),IS_SRVROLEMEMBER('dbcreator')),'null'),
                    '|',COALESCE(CONVERT(varchar(5),HAS_PERMS_BY_NAME(NULL,NULL,'ALTER ANY DATABASE')),'null'),
                    '|',COALESCE(CONVERT(varchar(5),HAS_PERMS_BY_NAME(NULL,NULL,'CONTROL SERVER')),'null'))",ct));
        Assert.Equal("0|0|0|0",ddlProfile);
        var provisioner=new SqlTenantProvisioner(registry,f.Master)
        {
            InitializeCiDdlSession=(conn,c)=>B4SqlFixture.ImpersonateAsync(conn,ddlLogin,c)
        };
        var ownerA=await f.CreateBareLoginAsync("b4owner",ct);
        var ownerB=await f.CreateBareLoginAsync("b4owner",ct);
        var ownerOrphan=await f.CreateBareLoginAsync("b4owner",ct);
        var finalizerA=await f.CreateFinalizerAsync(ct);
        var finalizerB=await f.CreateFinalizerAsync(ct);
        var finalizerOrphan=await f.CreateFinalizerAsync(ct);
        var retirerA=await f.CreateRetirerAsync(ct);
        var retirerB=await f.CreateRetirerAsync(ct);
        var retirerOrphan=await f.CreateRetirerAsync(ct);
        await f.GrantOwnerImpersonationAsync(finalizerA,ownerA,ct);
        await f.GrantOwnerImpersonationAsync(finalizerB,ownerB,ct);
        var finalizerRegistryA=new TenantRegistrySql(f.Control)
        {
            InitializeCiSession=(conn,c)=>B4SqlFixture.ImpersonateAsync(conn,finalizerA,c)
        };
        var finalizerServiceA=new SqlTenantFinalizer(finalizerRegistryA,f.Master)
        {
            InitializeCiFinalizerSession=(conn,c)=>B4SqlFixture.ImpersonateAsync(conn,finalizerA,c)
        };
        var finalizerRegistryB=new TenantRegistrySql(f.Control)
        {
            InitializeCiSession=(conn,c)=>B4SqlFixture.ImpersonateAsync(conn,finalizerB,c)
        };
        var retirerRegistryA=new TenantRegistrySql(f.Control)
        {
            InitializeCiSession=(conn,c)=>B4SqlFixture.ImpersonateAsync(conn,retirerA,c)
        };
        var retirerServiceA=new SqlTenantPrivilegeRetirer(retirerRegistryA,f.Master)
        {
            InitializeCiRetirerSession=(conn,c)=>B4SqlFixture.ImpersonateAsync(conn,retirerA,c)
        };
        var probeA=await f.CreateBareLoginAsync("b4probe",ct);
        var verifierRegistry=new TenantRegistrySql(f.Control)
        {
            InitializeCiSession=(conn,c)=>B4SqlFixture.ImpersonateAsync(conn,f.Verifier,c)
        };
        var verifier=new TenantProbeVerifier(verifierRegistry)
        {
            InitializeCiScopedSession=(conn,c)=>B4SqlFixture.ImpersonateAsync(conn,probeA,c)
        };

        async Task RequestApprove(Guid id,string key,string ownerLogin,string finalizerLogin,
            string retirerLogin)
        {
            var reqArgs=new[]
            {
                new SqlParameter("@id",id), new SqlParameter("@key",key),
                new SqlParameter("@ticket","CHG-B4-CI-"+id.ToString("N"))
            };
            const string request="EXEC dbo.cp_RequestTenant @TenantId=@id,@TenantKey=@key,@RequestRef=@ticket";
            await f.RunAsAsync(f.Requester,request,ct,reqArgs);
            await f.RunAsAsync(f.Requester,request,ct,
                new SqlParameter("@id",id),new SqlParameter("@key",key),
                new SqlParameter("@ticket","CHG-B4-CI-"+id.ToString("N")));
            Assert.Equal(1,Convert.ToInt32(await f.ScalarAsync(f.Control,
                "SELECT COUNT(*) FROM dbo.PreprodTenantLifecycleEvents WHERE TenantId=@id AND ActionCode='REQUEST'",
                ct,new SqlParameter("@id",id))));
            await Assert.ThrowsAsync<SqlException>(()=>
                f.RunAsAsync(f.Requester,
                    "EXEC dbo.cp_ApproveTenant @TenantId=@id,@ApprovalRef=@ref,@ApprovedOwnerLogin=@owner,@ProvisionerLogin=@provisioner,@FinalizerLogin=@finalizer,@RetirerLogin=@retirer",ct,
                    new SqlParameter("@id",id),new SqlParameter("@ref","CHG-CHECK-"+id.ToString("N")),
                new SqlParameter("@owner",ownerLogin),new SqlParameter("@provisioner",ddlLogin),
                new SqlParameter("@finalizer",finalizerLogin),
                new SqlParameter("@retirer",retirerLogin)));
            await f.RunAsAsync(f.Checker,
                "EXEC dbo.cp_ApproveTenant @TenantId=@id,@ApprovalRef=@ref,@ApprovedOwnerLogin=@owner,@ProvisionerLogin=@provisioner,@FinalizerLogin=@finalizer,@RetirerLogin=@retirer",ct,
                new SqlParameter("@id",id),new SqlParameter("@ref","CHG-CHECK-"+id.ToString("N")),
                new SqlParameter("@owner",ownerLogin),new SqlParameter("@provisioner",ddlLogin),
                new SqlParameter("@finalizer",finalizerLogin),
                new SqlParameter("@retirer",retirerLogin));
        }

        async Task RecordVerification(TenantRegistryEntry target)
        {
            // Exercise the real verifier, including the independently authenticated
            // Control Plane SQL procedures and SQL-engine cross-db negative probes.
            var scoped=new TenantVerifierProbeSecrets(f.Connection(target.DatabaseName),
                ownerA,ddlLogin);
            var verified=await verifier.VerifyAsync(target.TenantId,scoped,ct);
            Assert.Equal(f.Verifier,verified.VerifiedBy);
        }

        async Task Transition(string login,Guid id,string action,byte[]? row=null,string? evidence=null)
        {
            await f.RunAsAsync(login,
                "EXEC dbo.cp_TenantTransition @TenantId=@id,@Action=@action,@ExpectedRowVer=@row,@EvidenceRef=@ref",ct,
                new SqlParameter("@id",id), new SqlParameter("@action",action),
                new SqlParameter("@row",SqlDbType.Binary,8){Value=(object?)row ?? DBNull.Value},
                new SqlParameter("@ref",(object?)evidence ?? DBNull.Value));
        }

        await RequestApprove(tenantA,"tenant-ci-a",ownerA,finalizerA,retirerA);
        await RequestApprove(tenantB,"tenant-ci-b",ownerB,finalizerB,retirerB);
        await RequestApprove(orphan,"tenant-ci-orphan",ownerOrphan,finalizerOrphan,retirerOrphan);
        // Owner/DDL identities are immutable approval content: an idempotent
        // replay with changed owner may not silently rewrite SQL authority.
        await Assert.ThrowsAsync<SqlException>(()=>f.RunAsAsync(f.Checker,
            "EXEC dbo.cp_ApproveTenant @TenantId=@id,@ApprovalRef=@ref,@ApprovedOwnerLogin=@owner,@ProvisionerLogin=@provisioner,@FinalizerLogin=@finalizer,@RetirerLogin=@retirer",ct,
            new SqlParameter("@id",tenantA),
            new SqlParameter("@ref","CHG-CHECK-"+tenantA.ToString("N")),
            new SqlParameter("@owner",ownerB),new SqlParameter("@provisioner",ddlLogin),
            new SqlParameter("@finalizer",finalizerA),
            new SqlParameter("@retirer",retirerA)));

        // Exclusive Control Plane lease; failure preserves stable operation ID.
        var lease=Guid.NewGuid();
        var first=await registry.ClaimAsync(tenantA,lease,ct);
        Assert.NotNull(first.OperationId);
        await Assert.ThrowsAsync<SqlException>(()=>registry.ClaimAsync(tenantA,Guid.NewGuid(),ct));
        // A stale worker must not commit after a newly acquired lease.
        await f.ExecAsync(f.Control,
            "UPDATE dbo.PreprodTenantRegistry SET LeaseUntilUtc=DATEADD(minute,-1,SYSUTCDATETIME()) WHERE TenantId=@id",
            ct,new SqlParameter("@id",tenantA));
        // Expiration fences failure reporting too, not only successful completion.
        // A delayed worker must not reset state before the next lease takeover.
        await Assert.ThrowsAsync<SqlException>(()=>registry.FailAsync(
            tenantA,lease,"EXPIRED_LEASE",ct));
        Assert.Equal("PROVISIONING",(await registry.GetAsync(tenantA,ct))!.State);
        var takeover=Guid.NewGuid();
        var second=await registry.ClaimAsync(tenantA,takeover,ct);
        Assert.Equal(first.OperationId,second.OperationId);
        await Assert.ThrowsAsync<SqlException>(()=>registry.CompleteAsync(tenantA,lease,ct));
        await Assert.ThrowsAsync<SqlException>(()=>registry.FailAsync(tenantA,lease,"STALE_LEASE",ct));
        await registry.FailAsync(tenantA,takeover,"TEST_RETRY",ct);
        var expectedOperation=first.OperationId;

        f.TrackDb(TenantRegistryEntry.ExpectedDatabase(tenantA));
        f.TrackDb(TenantRegistryEntry.ExpectedDatabase(tenantB));
        var a=await provisioner.ProvisionAsync(tenantA,ct);
        var b=await provisioner.ProvisionAsync(tenantB,ct);
        Assert.Equal("VERIFYING",a.State);
        Assert.Equal("VERIFYING",b.State);
        Assert.Equal(expectedOperation,a.OperationId);
        Assert.NotEqual(a.DatabaseName,b.DatabaseName);
        Assert.NotEqual(a.BindingId,b.BindingId);
        Assert.NotEqual(
            Convert.ToInt32(await f.ScalarAsync(f.Connection(a.DatabaseName),"SELECT DB_ID()",ct)),
            Convert.ToInt32(await f.ScalarAsync(f.Connection(b.DatabaseName),"SELECT DB_ID()",ct)));

        await using(var peer=new SqlConnection(f.Connection(b.DatabaseName)))
        {
            await peer.OpenAsync(ct);
            Assert.False(await SqlTenantProvisioner.HasApprovedMarkerAsync(peer,a,ct));
        }
        // A database created by the DDL principal must remain VERIFYING until
        // the independent DBA-controlled ownership transfer is observed by SQL.
        var scoped=new TenantVerifierProbeSecrets(f.Connection(a.DatabaseName),
            ownerA,ddlLogin);
        var missingOwner=await Assert.ThrowsAsync<TenantLifecycleDeniedException>(()=>
            verifier.VerifyAsync(tenantA,scoped,ct));
        Assert.Equal("TENANT_DB_OWNER_NOT_ATTESTED",missingOwner.Code);
        // Bypass attempt: even a CanVerify SQL principal must not submit
        // an arbitrary nonzero hash while the original DDL owner is in charge.
        await Assert.ThrowsAsync<SqlException>(()=>f.RunAsAsync(f.Verifier,
            "EXEC dbo.cp_RecordVerification @TenantId=@id,@ExpectedRowVer=@row,@ObservedBindingId=@binding,@WitnessHash=@hash",ct,
            new SqlParameter("@id",tenantA),
            new SqlParameter("@row",SqlDbType.Binary,8){Value=a.RowVersion},
            new SqlParameter("@binding",a.BindingId),
            new SqlParameter("@hash",SqlDbType.Binary,32){Value=SHA256.HashData([1,2,3,4])}));
        // A finalizer approved for tenant A must not own/change tenant B,
        // including attempts made outside the Control Plane stored procedure.
        var peerOwnerBefore=(byte[])(await f.ScalarAsync(f.Master,
            "SELECT owner_sid FROM sys.databases WHERE name=@name",ct,
            new SqlParameter("@name",b.DatabaseName)))!;
        await Assert.ThrowsAsync<SqlException>(()=>f.RunAsFromAsync(finalizerA,f.Master,
            $"ALTER AUTHORIZATION ON DATABASE::[{b.DatabaseName}] TO [{ownerA}]",ct));
        Assert.Equal(peerOwnerBefore,(byte[])(await f.ScalarAsync(f.Master,
            "SELECT owner_sid FROM sys.databases WHERE name=@name",ct,
            new SqlParameter("@name",b.DatabaseName)))!);
        // A finalizer must not read peer Control Plane metadata or claim it.
        await Assert.ThrowsAsync<SqlException>(()=>finalizerRegistryA.GetAsync(tenantB,ct));
        await Assert.ThrowsAsync<SqlException>(()=>
            finalizerRegistryA.ClaimFinalizationAsync(tenantB,Guid.NewGuid(),ct));
        var firstFinalizationToken=Guid.NewGuid();
        var firstFinalization=await finalizerRegistryA.ClaimFinalizationAsync(
            tenantA,firstFinalizationToken,ct);
        Assert.Equal("FINALIZING",firstFinalization.State);
        Assert.False(firstFinalization.IsRoutable);
        Assert.NotNull(firstFinalization.FinalizationOperationId);
        await Assert.ThrowsAsync<SqlException>(()=>
            finalizerRegistryA.ClaimFinalizationAsync(tenantA,Guid.NewGuid(),ct));
        // Exact active lease expiration and stale completion/failure are denied.
        await f.ExecAsync(f.Control,
            "UPDATE dbo.PreprodTenantRegistry SET FinalizationLeaseUntilUtc=DATEADD(minute,-1,SYSUTCDATETIME()) WHERE TenantId=@id",
            ct,new SqlParameter("@id",tenantA));
        await Assert.ThrowsAsync<SqlException>(()=>
            finalizerRegistryA.FailFinalizationAsync(tenantA,firstFinalizationToken,
                "EXPIRED_FINALIZER",ct));
        var secondFinalizationToken=Guid.NewGuid();
        var takeoverFinalization=await finalizerRegistryA.ClaimFinalizationAsync(
            tenantA,secondFinalizationToken,ct);
        Assert.Equal(firstFinalization.FinalizationOperationId,takeoverFinalization.FinalizationOperationId);
        await Assert.ThrowsAsync<SqlException>(()=>
            finalizerRegistryA.CompleteFinalizationAsync(tenantA,firstFinalizationToken,
                takeoverFinalization.FinalizationOperationId!.Value,
                takeoverFinalization.BindingId,takeoverFinalization.SecurityEpoch,ct));
        await finalizerRegistryA.FailFinalizationAsync(tenantA,secondFinalizationToken,
            "CI_SAFE_RETRY",ct);
        Assert.Equal("VERIFYING",(await registry.GetAsync(tenantA,ct))!.State);
        var transferPending=await finalizerServiceA.FinalizeAsync(tenantA,ct);
        Assert.Equal("FINALIZING",transferPending.State);
        Assert.Null(transferPending.FinalizedBy);
        Assert.Null(transferPending.FinalizerRetiredBy);
        Assert.False(transferPending.IsRoutable);
        // Finalizer can NEVER self-attest completion without the separate DBA
        // retirement actor and effective permission cleanup.
        await Assert.ThrowsAsync<SqlException>(()=>
            finalizerRegistryA.CompleteFinalizationAsync(tenantA,
                transferPending.FinalizationLeaseToken!.Value,
                transferPending.FinalizationOperationId!.Value,
                transferPending.BindingId,transferPending.SecurityEpoch,ct));
        await Assert.ThrowsAsync<SqlException>(()=>retirerRegistryA.GetAsync(tenantB,ct));
        var finalized=await retirerServiceA.RetireAsync(tenantA,ct);
        Assert.Equal("VERIFYING",finalized.State);
        Assert.Equal(finalizerA,finalized.FinalizedBy);
        Assert.Equal(retirerA,finalized.FinalizerRetiredBy);
        Assert.NotNull(finalized.FinalizerRetiredUtc);
        Assert.Equal(takeoverFinalization.FinalizationOperationId,finalized.FinalizationOperationId);
        Assert.False(finalized.IsRoutable);
        Assert.Equal(1,Convert.ToInt32(await f.ScalarAsync(f.Master,
            "SELECT CONVERT(int,is_disabled) FROM sys.server_principals WHERE name=@fin",
            ct,new SqlParameter("@fin",finalizerA))));
        Assert.Equal(0,Convert.ToInt32(await f.ScalarAsync(f.Connection(a.DatabaseName),
            "SELECT COUNT(*) FROM sys.database_principals WHERE name=@fin",
            ct,new SqlParameter("@fin",finalizerA))));
        Assert.Equal(0,Convert.ToInt32(await f.ScalarAsync(f.Master,
            @"SELECT COUNT(*) FROM sys.server_permissions AS p
                 JOIN sys.server_principals sp ON sp.principal_id=p.grantee_principal_id
                 WHERE sp.name=@fin AND p.permission_name='IMPERSONATE'
                   AND p.state IN ('G','W')",ct,new SqlParameter("@fin",finalizerA))));
        await Assert.ThrowsAsync<SqlException>(()=>
            f.RunAsFromAsync(finalizerA,f.Master,
                $"ALTER AUTHORIZATION ON DATABASE::[{a.DatabaseName}] TO [{finalizerA}]",ct));
        await Assert.ThrowsAsync<SqlException>(()=>
            finalizerRegistryA.ClaimFinalizationAsync(tenantA,Guid.NewGuid(),ct));
        // Revoke during finalization of the peer must cancel its lease and
        // prevent completion even if a delayed SQL owner transfer occurs.
        var peerFinalizationToken=Guid.NewGuid();
        var peerClaim=await finalizerRegistryB.ClaimFinalizationAsync(tenantB,peerFinalizationToken,ct);
        await Transition(f.Security,tenantB,"REVOKE");
        await Assert.ThrowsAsync<SqlException>(()=>
            finalizerRegistryB.CompleteFinalizationAsync(tenantB,peerFinalizationToken,
                peerClaim.FinalizationOperationId!.Value,peerClaim.BindingId,
                peerClaim.SecurityEpoch,ct));
        Assert.Equal("SUSPENDED",(await registry.GetAsync(tenantB,ct))!.State);
        await Assert.ThrowsAsync<SqlException>(()=>
            finalizerRegistryB.ClaimFinalizationAsync(tenantB,Guid.NewGuid(),ct));
        await f.ExecAsync(f.Connection(a.DatabaseName),
            $"CREATE USER [{probeA}] FOR LOGIN [{probeA}]; GRANT SELECT ON dbo.__PreprodTenantBinding TO [{probeA}]; GRANT SELECT ON dbo.__PreprodTenantSchemaHistory TO [{probeA}];",ct);
        var ownerSid=(byte[])(await f.ScalarAsync(f.Master,
            "SELECT owner_sid FROM sys.databases WHERE name=@db",ct,
            new SqlParameter("@db",a.DatabaseName)))!;
        var expectedOwnerSid=(byte[])(await f.ScalarAsync(f.Master,
            "SELECT SUSER_SID(@login)",ct,new SqlParameter("@login",ownerA)))!;
        Assert.Equal(expectedOwnerSid,ownerSid);
        // The actual non-sysadmin CREATE ANY DATABASE principal now loses dbo.
        // Prove it cannot SELECT or UPDATE its formerly owned database.
        await Assert.ThrowsAsync<SqlException>(()=>f.RunAsFromAsync(ddlLogin,
            f.Connection(a.DatabaseName),
            $"SELECT COUNT_BIG(*) FROM dbo.__PreprodTenantBinding",ct));
        await Assert.ThrowsAsync<SqlException>(()=>f.RunAsFromAsync(ddlLogin,
            f.Connection(a.DatabaseName),
            $"UPDATE dbo.__PreprodTenantBinding SET BindingVersion=BindingVersion",ct));
        var wrongOwner=await Assert.ThrowsAsync<TenantLifecycleDeniedException>(()=>
            verifier.VerifyAsync(tenantA,
                new TenantVerifierProbeSecrets(f.Connection(a.DatabaseName),
                    ddlLogin,ownerA),ct));
        Assert.Equal("TENANT_DB_OWNER_NOT_ATTESTED",wrongOwner.Code);
        // The test login can access own marker read-only; cannot access peer,
        // control, or change marker. All proofs execute under EXECUTE AS LOGIN.
        await f.RunAsFromAsync(probeA,f.Connection(a.DatabaseName),
            "SELECT COUNT_BIG(*) FROM dbo.__PreprodTenantBinding",ct);
        await Assert.ThrowsAsync<SqlException>(()=>f.RunAsFromAsync(probeA,
            f.Connection(a.DatabaseName),
            $"SELECT COUNT_BIG(*) FROM [{b.DatabaseName}].dbo.__PreprodTenantBinding",ct));
        var cpName=new SqlConnectionStringBuilder(f.Control).InitialCatalog;
        await Assert.ThrowsAsync<SqlException>(()=>f.RunAsFromAsync(probeA,
            f.Connection(a.DatabaseName),
            $"SELECT COUNT_BIG(*) FROM [{cpName}].dbo.PreprodTenantRegistry",ct));
        await Assert.ThrowsAsync<SqlException>(()=>f.RunAsFromAsync(probeA,
            f.Connection(a.DatabaseName),
            "UPDATE dbo.__PreprodTenantBinding SET BindingVersion=BindingVersion",ct));
        // SQL impersonation proves a scoped A principal can read A but cannot
        // SELECT peer B or control-plane rows or UPDATE its own marker.
        await f.ExecAsync(f.Connection(a.DatabaseName),
            $"CREATE USER [{f.Requester}] FOR LOGIN [{f.Requester}]; GRANT SELECT ON dbo.__PreprodTenantBinding TO [{f.Requester}];",ct);
        await f.RunAsAsync(f.Requester,
            $"SELECT COUNT_BIG(*) FROM [{a.DatabaseName}].dbo.__PreprodTenantBinding",ct);
        await Assert.ThrowsAsync<SqlException>(()=>f.RunAsAsync(f.Requester,
            $"SELECT COUNT_BIG(*) FROM [{b.DatabaseName}].dbo.__PreprodTenantBinding",ct));
        await Assert.ThrowsAsync<SqlException>(()=>f.RunAsAsync(f.Requester,
            "SELECT COUNT_BIG(*) FROM dbo.PreprodTenantRegistry",ct));
        // A role can retrieve its registered tenant through a guarded
        // ownership-chained procedure while direct SELECT remains DENIED.
        await f.RunAsAsync(f.Requester,
            "EXEC dbo.cp_GetTenant @TenantId=@id",ct,
            new SqlParameter("@id",tenantA));
        await Assert.ThrowsAsync<SqlException>(()=>f.RunAsAsync(f.Requester,
            "EXEC dbo.cp_GetPeerForVerification @TenantId=@id",ct,
            new SqlParameter("@id",tenantA)));
        await Assert.ThrowsAsync<SqlException>(()=>f.RunAsAsync(f.Requester,
            $"UPDATE [{a.DatabaseName}].dbo.__PreprodTenantBinding SET BindingVersion=BindingVersion",ct));

        await Assert.ThrowsAsync<SqlException>(()=>Transition(f.Checker,tenantA,"ACTIVATE",a.RowVersion));
        await RecordVerification(a);
        var verifiedA=(await registry.GetAsync(tenantA,ct))!;
        await Transition(f.Checker,tenantA,"ACTIVATE",verifiedA.RowVersion);
        var active=(await registry.GetAsync(tenantA,ct))!;
        Assert.True(active.IsRoutable);

        await Transition(f.Security,tenantA,"SUSPEND");
        var suspended=(await registry.GetAsync(tenantA,ct))!;
        Assert.False(suspended.IsRoutable);
        Assert.True(suspended.SecurityEpoch>active.SecurityEpoch);
        await Assert.ThrowsAsync<SqlException>(()=>registry.ClaimAsync(tenantA,Guid.NewGuid(),ct));

        await Transition(f.Checker,tenantA,"START_REACTIVATION",evidence:"REACTIVATE-CI-1");
        var verifying=(await registry.GetAsync(tenantA,ct))!;
        Assert.Equal("REACTIVATION_VERIFYING",verifying.State);
        Assert.False(verifying.IsRoutable);
        await RecordVerification(verifying);
        var stale=(await registry.GetAsync(tenantA,ct))!;
        await Transition(f.Security,tenantA,"SUSPEND"); // concurrent revoke wins
        await Assert.ThrowsAsync<SqlException>(()=>
            Transition(f.Checker,tenantA,"ACTIVATE",stale.RowVersion));
        Assert.Equal("SUSPENDED",(await registry.GetAsync(tenantA,ct))!.State);
        await Assert.ThrowsAsync<SqlException>(()=>registry.ClaimAsync(tenantA,Guid.NewGuid(),ct));

        await Transition(f.Checker,tenantA,"START_REACTIVATION",evidence:"REACTIVATE-CI-2");
        await RecordVerification((await registry.GetAsync(tenantA,ct))!);
        await Transition(f.Checker,tenantA,"ACTIVATE",
            (await registry.GetAsync(tenantA,ct))!.RowVersion);
        Assert.True((await registry.GetAsync(tenantA,ct))!.IsRoutable);

        // An existing unrelated database is never auto-adopted, rewritten, or dropped.
        var orphanDb=TenantRegistryEntry.ExpectedDatabase(orphan);
        await f.CreateDbAsync(orphanDb,ct);
        await f.ExecAsync(f.Connection(orphanDb),
            "CREATE TABLE dbo.UnrelatedExistingData(Id int NOT NULL)",ct);
        await Assert.ThrowsAnyAsync<Exception>(()=>provisioner.ProvisionAsync(orphan,ct));
        Assert.Equal("FAILED_RETRYABLE",(await registry.GetAsync(orphan,ct))!.State);
        Assert.Equal(1,Convert.ToInt32(await f.ScalarAsync(f.Connection(orphanDb),
            "SELECT COUNT(*) FROM sys.tables WHERE name='UnrelatedExistingData'",ct)));

        // No decommission SQL DROP. Audit and security epoch must be durable.
        await Transition(f.Checker,tenantA,"DECOMMISSION",evidence:"ARCHIVE-APPROVED-CI");
        Assert.False((await registry.GetAsync(tenantA,ct))!.IsRoutable);
        await Transition(f.Verifier,tenantA,"COMPLETE_DECOMMISSION",evidence:"ARCHIVE-WITNESSED-CI");
        var final=(await registry.GetAsync(tenantA,ct))!;
        Assert.Equal("DECOMMISSIONED",final.State);
        Assert.Equal(1,Convert.ToInt32(await f.ScalarAsync(f.Connection(a.DatabaseName),
            "SELECT COUNT(*) FROM dbo.__PreprodTenantBinding",ct)));
        Assert.Equal(1,Convert.ToInt32(await f.ScalarAsync(f.Connection(b.DatabaseName),
            "SELECT COUNT(*) FROM dbo.__PreprodTenantBinding",ct)));
        Assert.True(Convert.ToInt32(await f.ScalarAsync(f.Control,
            "SELECT COUNT(*) FROM dbo.PreprodTenantLifecycleEvents WHERE TenantId=@id",
            ct,new SqlParameter("@id",tenantA)))>=10);

        var evidence=new
        {
            TestRunId=Environment.GetEnvironmentVariable("APP02B4_TEST_RUN_ID"),
            SourceHeadSha=Environment.GetEnvironmentVariable("APP02B4_SOURCE_HEAD_SHA"),
            SqlEngine="GITHUB_HOSTED_WINDOWS_LOCALDB_NOT_PREPROD",
            ControlSchemaVersion=TenantSchemaVersions.Registry,
            TenantSchemaVersion=TenantSchemaVersions.Tenant,
            PhysicalTenantDatabases=2,
            DifferentDatabaseIds=true,MakerCheckerDenied=true,LeaseCollisionDenied=true,
            SameOperationRetry=true,PeerBindingDenied=true,
            OwnerTransferEnforced=true,OwnerSidMatchesApproved=true,
            SqlOwnerApprovalImmutable=true,DirectForgedOwnerWitnessDenied=true,
            ScopedFinalizerAutomation=true,FinalizerDirectPeerDdlDenied=true,
            FinalizerLeaseCollisionDenied=true,
            FinalizerStableOperationRetry=true,FinalizerPeerClaimDenied=true,
            FinalizerStaleCompletionDenied=true,FinalizerRevokeDuringClaimDenied=true,
            IndependentDbaRetirement=true,FinalizerSelfCompletionDenied=true,
            FinalizerLoginDisabled=true,FinalizerDbUserRemoved=true,
            FinalizerImpersonationRevoked=true,FinalizerPostTransferRetakeDenied=true,
            SqlImpersonatedEndToEndVerifier=true,SqlScopeDenied=true,
            DdlCreatorDirectReadDenied=true,
            SuspendedEpochAdvanced=true,ReactivationRaceDenied=true,
            UnmarkedDatabaseQuarantined=true,DecommissionPreservesSql=true,
            FullPreprodGate="NOT_VERIFIED"
        };
        Directory.CreateDirectory("TestResults");
        await File.WriteAllTextAsync(
            "TestResults/APP-02B4-SQL-WITNESS.json",
            JsonSerializer.Serialize(evidence,new JsonSerializerOptions{WriteIndented=true}),ct);
    }
}
