using System.Data;
using System.Text.Json;
using FmcgDms.Preprod.Authorization;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FmcgDms.Preprod.Authorization.SqlServer.Tests;

/// <summary>
/// SQL Server LocalDB only. B53 bridges are installed after the B3/B4/B5
/// versioned bases; no PREPROD admission, migration auto-apply or business routes.
/// The SQL snapshot rollback below is deliberately a simulated old-image
/// row restore, NOT a real off-host/PITR drill or a physical G-B01 witness.
/// </summary>
public sealed class B53SqlTests
{
    [Fact]
    public async Task Independent_monotonic_fence_commits_with_B3_B4_and_denies_old_snapshots()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await B5SqlTests.Fixture.CreateAsync(ct);
        var actor = Guid.NewGuid();
        var session = Guid.NewGuid();
        var bindingA = Guid.NewGuid();
        var bindingB = Guid.NewGuid();
        var role = Guid.NewGuid();
        var registry = f.Connection(f.RegistryDatabase);
        var identity = f.Connection(f.IdentityDatabase);
        var fence = f.FenceConnection;
        static SqlParameter P(string name, object value) => new(name, value);

        async Task ApplyAsync(string resourceSuffix, string connection)
        {
            var assembly = typeof(B5Schema).Assembly;
            var name = assembly.GetManifestResourceNames().Single(n =>
                n.EndsWith(resourceSuffix, StringComparison.Ordinal));
            using var stream = new StreamReader(assembly.GetManifestResourceStream(name)!);
            var script = (await stream.ReadToEndAsync(ct))
                .Replace("__B53_FENCE_DB__", f.FenceDatabase, StringComparison.Ordinal);
            await f.RunAsync(connection, script, ct);
            await f.RunAsync(connection, script, ct); // replay must be idempotent
        }

        // No default authority. The independent fence migration is applied by
        // a controlled CI DBA before installing the two versioned B3/B4 bridges.
        await ApplyAsync("20261118_004_monotonic_revocation.sql", fence);
        await f.RunAsync(fence, $"""
            CREATE USER [{f.RegistrySecurityLogin}] FOR LOGIN [{f.RegistrySecurityLogin}];
            ALTER ROLE b53_identity_bridge ADD MEMBER [{f.RuntimeLogin}];
            ALTER ROLE b53_registry_bridge ADD MEMBER [{f.RegistrySecurityLogin}];
            ALTER ROLE b53_runtime ADD MEMBER [{f.RuntimeLogin}];
            ALTER ROLE b53_reactivation_approver ADD MEMBER [{f.SecurityLogin}];
            """, ct);
        await f.RunAsync(identity, $"""
            GRANT SELECT,INSERT,UPDATE ON dbo.PreprodLoginSessions TO [{f.RuntimeLogin}];
            """, ct);
        await f.RunAsync(registry, $"""
            GRANT SELECT,INSERT,UPDATE ON dbo.PreprodTenantRegistry
              TO [{f.RegistrySecurityLogin}];
            """, ct);
        await ApplyAsync("20261118_005_identity_revocation_bridge.sql", identity);
        await ApplyAsync("20261118_006_registry_revocation_bridge.sql", registry);

        await f.RunAsync(identity, """
            INSERT dbo.AspNetUsers(Id,UserName,NormalizedUserName,EmailConfirmed,
                PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnabled,AccessFailedCount,
                Disabled,SecurityStamp)
            VALUES(@actor,N'B53-CI',N'B53-CI',0,0,0,0,0,0,N'B53-STAMP');
            """, ct, P("@actor", actor));

        async Task AddSessionAsync(Guid sid)
        {
            await f.RunAsync(identity, $"""
                EXECUTE AS LOGIN=N'{f.RuntimeLogin}';
                INSERT dbo.PreprodLoginSessions(Id,UserId,Epoch,SecurityStamp,CreatedUtc)
                VALUES(@sid,@actor,0,N'B53-STAMP',SYSUTCDATETIME());
                REVERT;
                """, ct, P("@sid", sid), P("@actor", actor));
        }
        await AddSessionAsync(session);
        Assert.Equal(0L, Convert.ToInt64(await f.ScalarAsync(fence,
            "SELECT SessionEpoch FROM dbo.B53SessionState WHERE SessionId=@s", ct,
            P("@s", session))));

        // Both tenants bootstrap SUSPENDED regardless of the B4 request state.
        await f.RunAsync(registry, $"""
            EXECUTE AS LOGIN=N'{f.RegistrySecurityLogin}';
            INSERT dbo.PreprodTenantRegistry
              (TenantId,TenantKey,DatabaseName,BindingId,State,RequestRef,RequestedBy)
            VALUES(@a,N'b53-a',@dbA,@bindA,'REQUESTED',N'ci-source','ci-requester'),
                  (@b,N'b53-b',@dbB,@bindB,'REQUESTED',N'ci-source','ci-requester');
            REVERT;
            """, ct, P("@a", f.TenantA), P("@b", f.TenantB),
            P("@dbA", f.TenantADatabase), P("@dbB", f.TenantBDatabase),
            P("@bindA", bindingA), P("@bindB", bindingB));
        Assert.Equal("SUSPENDED", Convert.ToString(await f.ScalarAsync(fence,
            "SELECT State FROM dbo.B5TenantFence WHERE TenantId=@t", ct,
            P("@t", f.TenantA))));

        // Only the independently authenticated B53 approver can issue a
        // five-minute, exact-binding, exact-next-epoch permit for activation.
        await f.RunAsync(fence, $"""
            EXECUTE AS LOGIN=N'{f.SecurityLogin}';
            EXEC dbo.b53_ApproveActivation @TenantId=@tenant,
              @BindingId=@binding,@TargetRegistryEpoch=2,
              @EvidenceRef=N'B53-CI-INDEPENDENT-APPROVAL';
            REVERT;
            """, ct, P("@tenant", f.TenantA), P("@binding", bindingA));
        await f.RunAsync(registry, $"""
            EXECUTE AS LOGIN=N'{f.RegistrySecurityLogin}';
            UPDATE dbo.PreprodTenantRegistry
              SET State='ACTIVE',SecurityEpoch=SecurityEpoch+1
            WHERE TenantId=@tenant;
            REVERT;
            """, ct, P("@tenant", f.TenantA));
        Assert.Equal(2L, await f.EpochAsync(f.TenantA, ct));
        Assert.Equal(1L, await f.EpochAsync(f.TenantB, ct));

        // CI-only DBA seeds one A permission: no tenant B runtime grant.
        await f.RunAsync(fence, """
            INSERT dbo.B5Membership(TenantId,ActorId,MembershipEpoch)
              VALUES(@a,@actor,1);
            INSERT dbo.B5Role(TenantId,RoleId,Code,Enabled)
              VALUES(@a,@role,N'B53-WRITER',1);
            INSERT dbo.B5ActorRole(TenantId,ActorId,RoleId)
              VALUES(@a,@actor,@role);
            INSERT dbo.B5ActionCatalog(PermissionCode,ActionKind)
              VALUES(N'b53.write','WRITE');
            INSERT dbo.B5RolePermission(TenantId,RoleId,PermissionCode,ResourceKey)
              VALUES(@a,@role,N'b53.write',N'ci:test');
            """, ct, P("@a", f.TenantA), P("@actor", actor), P("@role", role));

        var auth = new AuthorizationContext(f.TenantA,f.TenantA,actor,session,
            bindingA,2,2,1,0,"b53.write","ci:test");
        var sqlFence = new SqlAuthorizationFence(fence,f.TenantADatabase)
        {
            InitializeCiSession=(db,token)=>f.ImpersonateAsync(db,f.RuntimeLogin,token),
            DurableGuard=new DurableRevocationGuard(f.FenceDatabase,f.IdentityDatabase,f.RegistryDatabase)
        };
        var guards = new SqlControlPlaneGuards(f.IdentityDatabase,f.RegistryDatabase);
        // A preflight intentionally labels only the failing procedure and SQL
        // error number; it never writes credentials, SQL contents, or auth data.
        async Task ProbeB53Async(string name,string procedure,params SqlParameter[] parameters)
        {
            await using var connection=new SqlConnection(fence);
            await connection.OpenAsync(ct);
            await f.ImpersonateAsync(connection,f.RuntimeLogin,ct);
            await using var transaction=(SqlTransaction)await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,ct);
            try
            {
                await using var command=new SqlCommand(procedure,connection,transaction)
                    {CommandType=CommandType.StoredProcedure,CommandTimeout=15};
                command.Parameters.AddRange(parameters);
                await command.ExecuteNonQueryAsync(ct);
                await transaction.RollbackAsync(ct);
            }
            catch(SqlException ex)
            {
                await transaction.RollbackAsync(ct);
                throw new InvalidOperationException(
                    "B53_LOCALDB_GUARD_"+name+"_DENIED_SQL_"+ex.Number,ex);
            }
        }
        await ProbeB53Async("IDENTITY_BRIDGE",
            "["+f.IdentityDatabase+"].dbo.b53_AssertIdentityBridgeInstalled");
        await ProbeB53Async("REGISTRY_BRIDGE",
            "["+f.RegistryDatabase+"].dbo.b53_AssertRegistryBridgeInstalled");
        await ProbeB53Async("SESSION_WATERMARK",
            "["+f.FenceDatabase+"].dbo.b53_AssertFreshSession",
            P("@SessionId",session),P("@ActorId",actor),P("@SessionEpoch",0L));
        Assert.Equal(1,await sqlFence.ExecuteAsync(auth,guards,
            (db,tx,token)=>f.WriteAsync(db,tx,f.TenantADatabase,token),ct));
        var peerBefore = await f.TenantSqlHashAsync(f.TenantBDatabase,ct);

        // A restored B3/B4 snapshot may not contain the installed bridge.
        // Even with currently valid row contents, a disabled bridge must
        // deny further guarded effects; a history row alone is not proof.
        await f.RunAsync(identity,
            "DISABLE TRIGGER dbo.b53_SessionMonotonicTrigger ON dbo.PreprodLoginSessions;",ct);
        var missingIdentityBridge = await Assert.ThrowsAsync<AuthorizationDeniedException>(()=>
            sqlFence.ExecuteAsync(auth,guards,
                (db,tx,token)=>f.WriteAsync(db,tx,f.TenantADatabase,token),ct));
        Assert.Equal("B53_DURABLE_AUTHORITY_DENIED",missingIdentityBridge.Code);
        await f.RunAsync(identity,
            "ENABLE TRIGGER dbo.b53_SessionMonotonicTrigger ON dbo.PreprodLoginSessions;",ct);
        await f.RunAsync(registry,
            "DISABLE TRIGGER dbo.b53_TenantMonotonicTrigger ON dbo.PreprodTenantRegistry;",ct);
        var missingRegistryBridge = await Assert.ThrowsAsync<AuthorizationDeniedException>(()=>
            sqlFence.ExecuteAsync(auth,guards,
                (db,tx,token)=>f.WriteAsync(db,tx,f.TenantADatabase,token),ct));
        Assert.Equal("B53_DURABLE_AUTHORITY_DENIED",missingRegistryBridge.Code);
        await f.RunAsync(registry,
            "ENABLE TRIGGER dbo.b53_TenantMonotonicTrigger ON dbo.PreprodTenantRegistry;",ct);
        Assert.Equal(1,await f.CountAsync(f.TenantADatabase,ct));

        // B3 revoke is one statement/transaction with independent B53 advance.
        async Task RevokeSessionAsync(Guid sid) => await f.RunAsync(identity,$"""
            EXECUTE AS LOGIN=N'{f.RuntimeLogin}';
            UPDATE dbo.PreprodLoginSessions
              SET Epoch=Epoch+1,RevokedUtc=SYSUTCDATETIME()
            WHERE Id=@sid AND RevokedUtc IS NULL;
            REVERT;
            """,ct,P("@sid",sid));
        await RevokeSessionAsync(session);
        Assert.Equal("REVOKED",Convert.ToString(await f.ScalarAsync(fence,
            "SELECT State FROM dbo.B53SessionState WHERE SessionId=@sid",ct,
            P("@sid",session))));
        Assert.Equal(1L,Convert.ToInt64(await f.ScalarAsync(fence,
            "SELECT SessionEpoch FROM dbo.B53SessionState WHERE SessionId=@sid",ct,
            P("@sid",session))));

        // Re-create the old B3 row image using DBA-only trigger disabling,
        // simulating a restored older Identity snapshot. This does not mutate
        // the independent B53 DB, and MUST NOT reissue the old JWT authority.
        await f.RunAsync(identity,"""
            DISABLE TRIGGER dbo.b53_SessionMonotonicTrigger ON dbo.PreprodLoginSessions;
            UPDATE dbo.PreprodLoginSessions SET Epoch=0,RevokedUtc=NULL
              WHERE Id=@sid;
            ENABLE TRIGGER dbo.b53_SessionMonotonicTrigger ON dbo.PreprodLoginSessions;
            """,ct,P("@sid",session));
        var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(()=>
            sqlFence.ExecuteAsync(auth,guards,
                (db,tx,token)=>f.WriteAsync(db,tx,f.TenantADatabase,token),ct));
        Assert.Equal("B53_DURABLE_AUTHORITY_DENIED",denied.Code);
        Assert.Equal(1,await f.CountAsync(f.TenantADatabase,ct));

        // Missing independent B53 EXEC grant must roll the original B3
        // statement back: no B3-only revoke that leaves a stale B53 row.
        var safeSession = Guid.NewGuid();
        await AddSessionAsync(safeSession);
        await f.RunAsync(fence,$"DENY EXECUTE ON OBJECT::dbo.b53_RevokeSession TO [{f.RuntimeLogin}];",ct);
        await Assert.ThrowsAsync<SqlException>(()=>RevokeSessionAsync(safeSession));
        Assert.Equal(0L,Convert.ToInt64(await f.ScalarAsync(identity,
            "SELECT Epoch FROM dbo.PreprodLoginSessions WHERE Id=@sid",ct,
            P("@sid",safeSession))));
        Assert.Equal(0L,Convert.ToInt64(await f.ScalarAsync(fence,
            "SELECT SessionEpoch FROM dbo.B53SessionState WHERE SessionId=@sid",ct,
            P("@sid",safeSession))));
        await f.RunAsync(fence,$"REVOKE EXECUTE ON OBJECT::dbo.b53_RevokeSession FROM [{f.RuntimeLogin}];",ct);

        // B4 suspend atomically advances separately durable B5 fence epoch.
        await f.RunAsync(registry,$"""
            EXECUTE AS LOGIN=N'{f.RegistrySecurityLogin}';
            UPDATE dbo.PreprodTenantRegistry
              SET State='SUSPENDED',SecurityEpoch=SecurityEpoch+1
            WHERE TenantId=@tenant;
            REVERT;
            """,ct,P("@tenant",f.TenantA));
        Assert.Equal(3L,await f.EpochAsync(f.TenantA,ct));
        Assert.Equal("SUSPENDED",Convert.ToString(await f.ScalarAsync(fence,
            "SELECT State FROM dbo.B5TenantFence WHERE TenantId=@tenant",ct,
            P("@tenant",f.TenantA))));

        // Simulated B4 old snapshot: privileged restore may bring back an
        // ACTIVE row and even an old B4 trigger; B5 independently retains its
        // newer SUSPENDED watermark and never permits an old-epoch write.
        await f.RunAsync(registry,"""
            DISABLE TRIGGER dbo.b53_TenantMonotonicTrigger ON dbo.PreprodTenantRegistry;
            UPDATE dbo.PreprodTenantRegistry
              SET State='ACTIVE',SecurityEpoch=2 WHERE TenantId=@tenant;
            ENABLE TRIGGER dbo.b53_TenantMonotonicTrigger ON dbo.PreprodTenantRegistry;
            """,ct,P("@tenant",f.TenantA));
        var freshAuth = auth with { SessionId=safeSession };
        await Assert.ThrowsAsync<AuthorizationDeniedException>(()=>
            sqlFence.ExecuteAsync(freshAuth,guards,
              (db,tx,token)=>f.WriteAsync(db,tx,f.TenantADatabase,token),ct));
        await Assert.ThrowsAsync<SqlException>(()=>f.RunAsync(registry,$"""
            EXECUTE AS LOGIN=N'{f.RegistrySecurityLogin}';
            UPDATE dbo.PreprodTenantRegistry
              SET State='SUSPENDED',SecurityEpoch=SecurityEpoch+1
            WHERE TenantId=@tenant;
            REVERT;
            """,ct,P("@tenant",f.TenantA)));
        Assert.Equal(3L,await f.EpochAsync(f.TenantA,ct));
        Assert.Equal(peerBefore,await f.TenantSqlHashAsync(f.TenantBDatabase,ct));
        Assert.Equal(0,await f.CountAsync(f.TenantBDatabase,ct));

        // Replay must detect a disabled physical bridge, not trust history alone.
        await f.RunAsync(identity,"DISABLE TRIGGER dbo.b53_SessionMonotonicTrigger ON dbo.PreprodLoginSessions;",ct);
        await Assert.ThrowsAsync<SqlException>(()=>ApplyAsync(
            "20261118_005_identity_revocation_bridge.sql",identity));
        await f.RunAsync(identity,"ENABLE TRIGGER dbo.b53_SessionMonotonicTrigger ON dbo.PreprodLoginSessions;",ct);

        var head=Environment.GetEnvironmentVariable("APP02B53_SOURCE_HEAD_SHA")
             ?? throw new InvalidOperationException("B53 source HEAD missing");
        if(head.Length!=40)throw new InvalidOperationException("B53 source SHA invalid");
        var run=Environment.GetEnvironmentVariable("APP02B53_TEST_RUN_ID")
             ?? throw new InvalidOperationException("B53 TestRunId missing");
        Directory.CreateDirectory("TestResults");
        await File.WriteAllTextAsync("TestResults/APP-02B53-LOCALDB.json",
            JsonSerializer.Serialize(new
            {
                TestRunId=run,SourceHeadSha=head,
                EvidenceClass="GITHUB_HOSTED_LOCALDB_ONLY",
                IndependentFenceDatabase=true,SeparatePhysicalTenantDatabases=2,
                SessionRevokeSameCommit=true,TenantSuspendSameCommit=true,
                DeniedB3OnlyCommitOnFencePrivilegeLoss=true,
                RestoredOldSessionJwtDenied=true,RestoredOldRegistryEpochDenied=true,
                CorruptedBridgeHistoryDenied=true,DisabledB3B4BridgesDenied=true,PeerTenantNoWrite=true,
                PeerSqlHashBefore=peerBefore,
                PeerSqlHashAfter=await f.TenantSqlHashAsync(f.TenantBDatabase,ct),
                SimulatedOldSnapshotOnly=true,RealPhysicalPitr="NOT_VERIFIED",
                RuntimeActivation="NO_GO",GB01ToGB05="NOT_RUN"
            },new JsonSerializerOptions { WriteIndented=true }),ct);
    }
}
