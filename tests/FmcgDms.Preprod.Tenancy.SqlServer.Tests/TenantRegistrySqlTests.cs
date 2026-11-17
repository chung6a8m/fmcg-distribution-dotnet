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
        Assert.Equal(9,Convert.ToInt32(await f.ScalarAsync(f.Control,
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
        var provisioner=new SqlTenantProvisioner(registry,f.Master);

        async Task RequestApprove(Guid id,string key)
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
                    "EXEC dbo.cp_ApproveTenant @TenantId=@id,@ApprovalRef=@ref",ct,
                    new SqlParameter("@id",id),new SqlParameter("@ref","CHG-CHECK-"+id.ToString("N"))));
            await f.RunAsAsync(f.Checker,
                "EXEC dbo.cp_ApproveTenant @TenantId=@id,@ApprovalRef=@ref",ct,
                new SqlParameter("@id",id),new SqlParameter("@ref","CHG-CHECK-"+id.ToString("N")));
        }

        async Task RecordVerification(TenantRegistryEntry target)
        {
            var cs=f.Connection(target.DatabaseName);
            await using var c=new SqlConnection(cs);
            await c.OpenAsync(ct);
            Assert.True(await SqlTenantProvisioner.HasApprovedMarkerAsync(c,target,ct));
            var witness=SHA256.HashData(Encoding.UTF8.GetBytes(
                target.TenantId.ToString("N")+target.BindingId.ToString("N")+target.SecurityEpoch));
            await f.RunAsAsync(f.Verifier,
                "EXEC dbo.cp_RecordVerification @TenantId=@id,@ExpectedRowVer=@row,@ObservedBindingId=@binding,@WitnessHash=@hash",ct,
                new SqlParameter("@id",target.TenantId),
                new SqlParameter("@row",SqlDbType.Binary,8){Value=target.RowVersion},
                new SqlParameter("@binding",target.BindingId),
                new SqlParameter("@hash",SqlDbType.Binary,32){Value=witness});
        }

        async Task Transition(string login,Guid id,string action,byte[]? row=null,string? evidence=null)
        {
            await f.RunAsAsync(login,
                "EXEC dbo.cp_TenantTransition @TenantId=@id,@Action=@action,@ExpectedRowVer=@row,@EvidenceRef=@ref",ct,
                new SqlParameter("@id",id), new SqlParameter("@action",action),
                new SqlParameter("@row",SqlDbType.Binary,8){Value=(object?)row ?? DBNull.Value},
                new SqlParameter("@ref",(object?)evidence ?? DBNull.Value));
        }

        await RequestApprove(tenantA,"tenant-ci-a");
        await RequestApprove(tenantB,"tenant-ci-b");
        await RequestApprove(orphan,"tenant-ci-orphan");

        // Exclusive Control Plane lease; failure preserves stable operation ID.
        var lease=Guid.NewGuid();
        var first=await registry.ClaimAsync(tenantA,lease,ct);
        Assert.NotNull(first.OperationId);
        await Assert.ThrowsAsync<SqlException>(()=>registry.ClaimAsync(tenantA,Guid.NewGuid(),ct));
        // A stale worker must not commit after a newly acquired lease.
        await f.ExecAsync(f.Control,
            "UPDATE dbo.PreprodTenantRegistry SET LeaseUntilUtc=DATEADD(minute,-1,SYSUTCDATETIME()) WHERE TenantId=@id",
            ct,new SqlParameter("@id",tenantA));
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
