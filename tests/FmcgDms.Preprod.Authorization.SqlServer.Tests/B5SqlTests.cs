using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using FmcgDms.Preprod.Authorization;
using FmcgDms.Preprod.Identity;
using FmcgDms.Preprod.Tenancy;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FmcgDms.Preprod.Authorization.SqlServer.Tests;

public sealed class B5SqlTests
{
    [Fact]
    public async Task Two_physical_tenants_independent_fence_revoke_race_and_negative_access()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(ct);
        var actor = Guid.NewGuid();
        var session = Guid.NewGuid();
        var role = Guid.NewGuid();
        var bindingA = Guid.NewGuid();
        var bindingB = Guid.NewGuid();
        await f.SeedAsync(actor, session, role, bindingA, bindingB, ct);
        var a = new AuthorizationContext(f.TenantA, f.TenantA, actor, session,
            bindingA, 1, 1, 1, 0, "tenant.write", "stock:ci-only");
        var b = a with { TenantId = f.TenantB, ResourceTenantId = f.TenantB,
            BindingId = bindingB };
        var fence = new SqlAuthorizationFence(f.FenceConnection)
        {
            InitializeCiSession = (db, token) => f.ImpersonateAsync(db, f.RuntimeLogin, token)
        };
        var guard = new SqlControlPlaneGuards(f.IdentityDatabase, f.RegistryDatabase);
        await f.AssertDirectExecWithoutRoleDeniedAsync(a, ct);

        Assert.Equal(1, await fence.ExecuteAsync(a, guard,
            (db, tx, token) => f.WriteAsync(db, tx, f.TenantADatabase, token), ct));
        Assert.Equal(1, await f.CountAsync(f.TenantADatabase, ct));

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => fence.ExecuteAsync(
            b, guard, (db, tx, token) => f.WriteAsync(db, tx, f.TenantBDatabase, token), ct));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => fence.ExecuteAsync(
            a with { BindingId = Guid.NewGuid() }, guard,
            (db, tx, token) => f.WriteAsync(db, tx, f.TenantADatabase, token), ct));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => fence.ExecuteAsync(
            a with { ActorId = Guid.NewGuid() }, guard,
            (db, tx, token) => f.WriteAsync(db, tx, f.TenantADatabase, token), ct));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => fence.ExecuteAsync(
            a with { SessionId = Guid.NewGuid() }, guard,
            (db, tx, token) => f.WriteAsync(db, tx, f.TenantADatabase, token), ct));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => fence.ExecuteAsync(
            a with { MakerId = actor }, guard,
            (db, tx, token) => f.WriteAsync(db, tx, f.TenantADatabase, token), ct));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => fence.ExecuteAsync(
            a with { SessionEpoch = 1 }, guard,
            (db, tx, token) => f.WriteAsync(db, tx, f.TenantADatabase, token), ct));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => fence.ExecuteAsync(
            a with { RegistryEpoch = 2 }, guard,
            (db, tx, token) => f.WriteAsync(db, tx, f.TenantADatabase, token), ct));
        Assert.Equal(0, await f.CountAsync(f.TenantBDatabase, ct));
        Assert.Equal(1, await f.CountAsync(f.TenantADatabase, ct));

        // The fenced effect holds the tenant row lock UNTIL its SQL COMMIT.
        // An independently authenticated security login cannot overtake it.
        var inside = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var protectedWork = fence.ExecuteAsync(a, guard, async (db, tx, token) =>
        {
            var count = await f.WriteAsync(db, tx, f.TenantADatabase, token);
            inside.TrySetResult(true);
            await release.Task.WaitAsync(token);
            return count;
        }, ct);
        await inside.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        var revocation = f.RevokeAsync(actor, ct);
        try
        {
            await Task.Delay(350, ct);
            Assert.False(revocation.IsCompleted);
        }
        finally { release.TrySetResult(true); }
        Assert.Equal(1, await protectedWork);
        await revocation;

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => fence.ExecuteAsync(
            a, guard, (db, tx, token) => f.WriteAsync(db, tx, f.TenantADatabase, token), ct));
        await Assert.ThrowsAsync<SqlException>(() =>
            f.RuntimeReadMembershipAsync(ct));

        // After the durable revoke the protected tenant A has two legal
        // committed effects, B still has zero. Restoring an old tenant snapshot
        // cannot lower the independently persisted fence epoch.
        Assert.Equal(2, await f.CountAsync(f.TenantADatabase, ct));
        Assert.Equal(0, await f.CountAsync(f.TenantBDatabase, ct));
        Assert.Equal(2L, await f.EpochAsync(f.TenantA, ct));
        Assert.Equal(1L, await f.EpochAsync(f.TenantB, ct));

        await f.SuspendAsync(f.TenantB, ct);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => fence.ExecuteAsync(
            b, guard,
            (db, tx, token) => f.WriteAsync(db, tx, f.TenantBDatabase, token), ct));
        Assert.Equal(0, await f.CountAsync(f.TenantBDatabase, ct));

        // CI-only evidence cannot be labeled an actual PREPROD acceptance.
        var source = Environment.GetEnvironmentVariable("APP02B5_SOURCE_HEAD_SHA");
        if (string.IsNullOrWhiteSpace(source) || source.Length != 40)
            throw new InvalidOperationException("Missing source-bound CI provenance");
        var testRunId = Environment.GetEnvironmentVariable("APP02B5_TEST_RUN_ID") ??
            throw new InvalidOperationException("Missing CI TestRunId");
        Directory.CreateDirectory("TestResults");
        var evidence = new
        {
            TestRunId = testRunId, SourceHeadSha = source,
            SchemaVersion = B5Schema.Version, EvidenceClass = "GITHUB_HOSTED_LOCALDB_ONLY",
            PhysicalTenantDatabases = 2, IndependentFenceDatabase = true,
            DifferentDatabaseIds = await f.DifferentDatabaseIdsAsync(ct),
            CrossTenantDenied = true, WrongBindingDenied = true, MakerCheckerDenied = true,
            SessionUnavailableDenied = true, RuntimeDirectSqlReadDenied = true,
            RevokeWaitedForProtectedCommit = true, StaleAuthorityDenied = true,
            SuspendedTenantDenied = true, TenantAEffects = 2, PeerTenantEffects = 0,
            RealPreprod = "NOT_VERIFIED", GB01ToGB05 = "NOT_RUN"
        };
        await File.WriteAllTextAsync("TestResults/APP-02B5-SQL-WITNESS.json",
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), ct);
    }

    [Fact]
    public async Task Current_B3_session_and_B4_registry_revocations_wait_for_protected_COMMIT()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(ct);
        var actor = Guid.NewGuid();
        var session = Guid.NewGuid();
        var bindingA = Guid.NewGuid();
        await f.SeedAsync(actor, session, Guid.NewGuid(), bindingA, Guid.NewGuid(), ct);
        var guard = new SqlControlPlaneGuards(f.IdentityDatabase, f.RegistryDatabase);
        var fence = new SqlAuthorizationFence(f.FenceConnection)
        {
            InitializeCiSession = (db, token) => f.ImpersonateAsync(db, f.RuntimeLogin, token)
        };
        var request = new AuthorizationContext(f.TenantA, f.TenantA, actor, session,
            bindingA, 1, 1, 1, 0, "tenant.write", "stock:ci-only");

        async Task CheckLinearizationAsync(AuthorizationContext current,
            Func<Task> revoke, string operation)
        {
            var inside = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var protectedWork = fence.ExecuteAsync(current, guard, async (db, tx, token) =>
            {
                var row = await f.WriteAsync(db, tx, f.TenantADatabase, token);
                inside.TrySetResult(true);
                await release.Task.WaitAsync(token);
                return row;
            }, ct);
            await inside.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            var pending = revoke();
            try
            {
                await Task.Delay(300, ct);
                Assert.False(pending.IsCompleted, operation + " overtook fenced SQL COMMIT");
            }
            finally { release.TrySetResult(true); }
            Assert.Equal(1, await protectedWork);
            await pending;
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
                fence.ExecuteAsync(current, guard,
                    (db, tx, token) => f.WriteAsync(db, tx, f.TenantADatabase, token), ct));
        }

        await CheckLinearizationAsync(request,
            () => f.RevokeIdentitySessionAsync(session, ct), "B3 session revoke");
        var replacementSession = Guid.NewGuid();
        await f.AddIdentitySessionAsync(actor, replacementSession, ct);
        await CheckLinearizationAsync(request with { SessionId = replacementSession },
            () => f.SuspendRegistryTenantAsync(f.TenantA, ct), "B4 tenant suspend");
        Assert.Equal(2, await f.CountAsync(f.TenantADatabase, ct));
        Assert.Equal(0, await f.CountAsync(f.TenantBDatabase, ct));
        Assert.Equal(1L, await f.EpochAsync(f.TenantA, ct)); // external fence independent
        var source = Environment.GetEnvironmentVariable("APP02B5_SOURCE_HEAD_SHA")
            ?? throw new InvalidOperationException("Source SHA missing");
        var testRunId = Environment.GetEnvironmentVariable("APP02B5_TEST_RUN_ID")
            ?? throw new InvalidOperationException("TestRunId missing");
        Directory.CreateDirectory("TestResults");
        await File.WriteAllTextAsync("TestResults/APP-02B5-CONTROL-RACES.json",
            JsonSerializer.Serialize(new {
                TestRunId=testRunId,SourceHeadSha=source,
                ActualB3SqlSessionRevokeSerialized=true,
                ActualB4TransitionSerialized=true,
                StaleJwtAndRegistryEpochDenied=true,
                PeerTenantEffects=0,
                RestoreResurrectionProof="NOT_VERIFIED",
                RealPreprod="NOT_VERIFIED"
            },new JsonSerializerOptions{WriteIndented=true}),ct);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string server;
        private readonly List<string> databases = [];
        private readonly List<string> logins = [];
        public Guid TenantA { get; } = Guid.NewGuid();
        public Guid TenantB { get; } = Guid.NewGuid();
        public string TenantADatabase { get; }
        public string TenantBDatabase { get; }
        public string FenceDatabase { get; }
        public string IdentityDatabase { get; }
        public string RegistryDatabase { get; }
        public string RuntimeLogin { get; }
        public string SecurityLogin { get; }
        public string RegistrySecurityLogin { get; }
        public string UnroledLogin { get; }
        public string FenceConnection => Connection(FenceDatabase);
        private string Master => Connection("master");

        private Fixture(string sqlServer, string nonce)
        {
            server = sqlServer;
            FenceDatabase = "FmcgB5Fence_" + nonce;
            IdentityDatabase = "FmcgB5Identity_" + nonce;
            RegistryDatabase = "FmcgB5Registry_" + nonce;
            TenantADatabase = "FmcgB5TenA_" + nonce;
            TenantBDatabase = "FmcgB5TenB_" + nonce;
            RuntimeLogin = "b5rt_" + nonce;
            SecurityLogin = "b5sec_" + nonce;
            RegistrySecurityLogin = "b5b4sec_" + nonce;
            UnroledLogin = "b5un_" + nonce;
        }

        public static async Task<Fixture> CreateAsync(CancellationToken ct)
        {
            var server = Environment.GetEnvironmentVariable("APP02B5_SQL_SERVER");
            if (string.IsNullOrWhiteSpace(server) ||
                !server.Contains("(localdb)", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("B.5 SQL verification requires disposable LocalDB");
            var f = new Fixture(server, Guid.NewGuid().ToString("N")[..12]);
            foreach (var name in new[] { f.FenceDatabase, f.IdentityDatabase, f.RegistryDatabase,
                f.TenantADatabase, f.TenantBDatabase })
                await f.CreateDatabaseAsync(name, ct);
            // Apply exact production source migrations to distinct CI databases.
            await f.ApplyTwiceAsync(f.FenceConnection, typeof(B5Schema),
                "20261118_001_independent_fence.sql", ct);
            await f.ApplyTwiceAsync(f.Connection(f.IdentityDatabase), typeof(PreprodIdentityDb),
                "20261117_001_identity_control_plane.sql", ct);
            await f.ApplyTwiceAsync(f.Connection(f.RegistryDatabase), typeof(TenantRegistrySql),
                "20261118_001_registry_control_plane.sql", ct);
            await f.ApplyTwiceAsync(f.Connection(f.IdentityDatabase), typeof(B5Schema),
                "20261118_002_identity_guard.sql", ct);
            await f.ApplyTwiceAsync(f.Connection(f.RegistryDatabase), typeof(B5Schema),
                "20261118_003_registry_guard.sql", ct);
            await f.CreateLoginAsync(f.RuntimeLogin, ct);
            await f.CreateLoginAsync(f.SecurityLogin, ct);
            await f.CreateLoginAsync(f.RegistrySecurityLogin, ct);
            await f.CreateLoginAsync(f.UnroledLogin, ct);
            await f.RunAsync(f.FenceConnection,
                "CREATE USER [" + f.RuntimeLogin + "] FOR LOGIN [" + f.RuntimeLogin + "];" +
                "ALTER ROLE b5_runtime ADD MEMBER [" + f.RuntimeLogin + "];" +
                "CREATE USER [" + f.SecurityLogin + "] FOR LOGIN [" + f.SecurityLogin + "];" +
                "ALTER ROLE b5_revoker ADD MEMBER [" + f.SecurityLogin + "];" +
                "CREATE USER [" + f.UnroledLogin + "] FOR LOGIN [" + f.UnroledLogin + "];" +
                "GRANT EXECUTE ON OBJECT::dbo.b5_AssertAuthority TO [" + f.UnroledLogin + "];" +
                "GRANT EXECUTE ON OBJECT::dbo.b5_RevokeMember TO [" + f.UnroledLogin + "];", ct);
            await f.RunAsync(f.Connection(f.IdentityDatabase),
                "CREATE USER [" + f.RuntimeLogin + "] FOR LOGIN [" + f.RuntimeLogin + "];" +
                "ALTER ROLE b5_session_runtime ADD MEMBER [" + f.RuntimeLogin + "];" +
                "CREATE USER [" + f.UnroledLogin + "] FOR LOGIN [" + f.UnroledLogin + "];" +
                "GRANT EXECUTE ON OBJECT::dbo.b5_AssertCurrentSession TO [" + f.UnroledLogin + "];", ct);
            await f.RunAsync(f.Connection(f.RegistryDatabase),
                "CREATE USER [" + f.RuntimeLogin + "] FOR LOGIN [" + f.RuntimeLogin + "];" +
                "ALTER ROLE b5_registry_runtime ADD MEMBER [" + f.RuntimeLogin + "];" +
                "CREATE USER [" + f.RegistrySecurityLogin + "] FOR LOGIN [" + f.RegistrySecurityLogin + "];" +
                "GRANT EXECUTE ON OBJECT::dbo.cp_TenantTransition TO [" +
                    f.RegistrySecurityLogin + "];" +
                "CREATE USER [" + f.UnroledLogin + "] FOR LOGIN [" + f.UnroledLogin + "];" +
                "GRANT EXECUTE ON OBJECT::dbo.b5_AssertCurrentTenant TO [" + f.UnroledLogin + "];", ct);
            await f.RunAsync(f.Connection(f.RegistryDatabase),
                "INSERT dbo.PreprodTenantOperators(LoginName,Enabled,CanSuspend) " +
                "VALUES(N'" + f.RegistrySecurityLogin + "',1,1)", ct);
            foreach (var database in new[] { f.TenantADatabase, f.TenantBDatabase })
                await f.RunAsync(f.Connection(database),
                    "CREATE TABLE dbo.B5WriteWitness(Id bigint IDENTITY(1,1) PRIMARY KEY);" +
                    "CREATE USER [" + f.RuntimeLogin + "] FOR LOGIN [" + f.RuntimeLogin + "];" +
                    "GRANT INSERT ON dbo.B5WriteWitness TO [" + f.RuntimeLogin + "];", ct);
            return f;
        }

        public async Task SeedAsync(Guid actor, Guid session, Guid role, Guid bindingA, Guid bindingB,
            CancellationToken ct)
        {
            await RunAsync(Connection(IdentityDatabase), """
                INSERT dbo.AspNetUsers(Id,UserName,NormalizedUserName,EmailConfirmed,
                    PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnabled,AccessFailedCount,
                    Disabled,SecurityStamp)
                VALUES(@actor,N'CI_USER',N'CI_USER',0,0,0,0,0,0,N'CI_STAMP');
                INSERT dbo.PreprodLoginSessions(Id,UserId,Epoch,SecurityStamp,CreatedUtc)
                VALUES(@session,@actor,0,N'CI_STAMP',SYSUTCDATETIME());
                """, ct, P("@actor", actor), P("@session", session));
            await RunAsync(Connection(RegistryDatabase), """
                INSERT dbo.PreprodTenantRegistry
                    (TenantId,TenantKey,DatabaseName,BindingId,SecurityEpoch,
                    State,RequestRef,RequestedBy)
                VALUES(@a,N'CI_A',@dbA,@bindingA,1,N'ACTIVE',N'ci-source-only',N'ci-dba'),
                      (@b,N'CI_B',@dbB,@bindingB,1,N'ACTIVE',N'ci-source-only',N'ci-dba');
                """, ct, P("@a", TenantA), P("@b", TenantB),
                P("@dbA", TenantADatabase), P("@dbB", TenantBDatabase),
                P("@bindingA", bindingA), P("@bindingB", bindingB));
            await RunAsync(FenceConnection, """
                INSERT dbo.B5TenantFence(TenantId,BindingId,RegistryEpoch,FenceEpoch,State)
                VALUES(@a,@bindingA,1,1,N'ACTIVE'),(@b,@bindingB,1,1,N'ACTIVE');
                INSERT dbo.B5Membership(TenantId,ActorId,MembershipEpoch)
                VALUES(@a,@actor,1);
                INSERT dbo.B5Role(TenantId,RoleId,Code,Enabled)
                VALUES(@a,@role,N'READER_WRITER_TEST',1);
                INSERT dbo.B5ActorRole(TenantId,ActorId,RoleId)
                VALUES(@a,@actor,@role);
                INSERT dbo.B5RolePermission(TenantId,RoleId,PermissionCode,ResourceKey)
                VALUES(@a,@role,N'tenant.write',N'stock:ci-only');
                """, ct,
                P("@a", TenantA), P("@b", TenantB),
                P("@bindingA", bindingA), P("@bindingB", bindingB),
                P("@actor", actor), P("@role", role));
        }

        public async Task<int> WriteAsync(SqlConnection db, SqlTransaction transaction,
            string database, CancellationToken ct)
        {
            if (database != TenantADatabase && database != TenantBDatabase)
                throw new InvalidOperationException("Unknown CI tenant target");
            await using var cmd = new SqlCommand("INSERT [" + database +
                "].dbo.B5WriteWitness DEFAULT VALUES", db, transaction);
            return await cmd.ExecuteNonQueryAsync(ct);
        }

        public Task RevokeIdentitySessionAsync(Guid session, CancellationToken ct) =>
            RunAsync(Connection(IdentityDatabase), """
                UPDATE dbo.PreprodLoginSessions
                   SET Epoch=Epoch+1, RevokedUtc=SYSUTCDATETIME()
                 WHERE Id=@session AND RevokedUtc IS NULL;
                """, ct, P("@session",session));

        public Task AddIdentitySessionAsync(Guid actor, Guid session, CancellationToken ct) =>
            RunAsync(Connection(IdentityDatabase), """
                INSERT dbo.PreprodLoginSessions(Id,UserId,Epoch,SecurityStamp,CreatedUtc)
                VALUES(@session,@actor,0,N'CI_STAMP',SYSUTCDATETIME());
                """, ct, P("@session",session),P("@actor",actor));

        public async Task SuspendRegistryTenantAsync(Guid tenant, CancellationToken ct)
        {
            await using var db = new SqlConnection(Connection(RegistryDatabase));
            await db.OpenAsync(ct);
            await ImpersonateAsync(db, RegistrySecurityLogin, ct);
            await using var command = new SqlCommand("dbo.cp_TenantTransition", db)
            { CommandType=CommandType.StoredProcedure, CommandTimeout=30 };
            command.Parameters.AddRange([
                P("@TenantId",tenant),P("@Action","SUSPEND"),
                P("@EvidenceRef","ci-b5-current-registry-serial")
            ]);
            await command.ExecuteNonQueryAsync(ct);
        }

        public async Task RevokeAsync(Guid actor, CancellationToken ct) =>
            await SecurityCommandAsync("dbo.b5_RevokeMember", TenantA, ct,
                P("@ExpectedFenceEpoch", 1L), P("@ActorId", actor),
                P("@EvidenceRef", "ci-revoke-commit-race"));

        public async Task SuspendAsync(Guid tenant, CancellationToken ct) =>
            await SecurityCommandAsync("dbo.b5_SuspendTenant", tenant, ct,
                P("@ExpectedFenceEpoch", 1L), P("@EvidenceRef", "ci-tenant-suspend"));

        public async Task AssertDirectExecWithoutRoleDeniedAsync(AuthorizationContext a,
            CancellationToken ct)
        {
            await UnroledDeniedAsync(FenceDatabase,"dbo.b5_AssertAuthority",ct,
                P("@TenantId",a.TenantId),P("@ResourceTenantId",a.ResourceTenantId),
                P("@ActorId",a.ActorId),P("@SessionId",a.SessionId),
                P("@BindingId",a.BindingId),P("@RegistryEpoch",a.RegistryEpoch),
                P("@FenceEpoch",a.FenceEpoch),P("@MembershipEpoch",a.MembershipEpoch),
                P("@PermissionCode",a.PermissionCode),P("@ResourceKey",a.ResourceKey));
            await UnroledDeniedAsync(FenceDatabase,"dbo.b5_RevokeMember",ct,
                P("@TenantId",a.TenantId),P("@ActorId",a.ActorId),
                P("@ExpectedFenceEpoch",1L),P("@EvidenceRef","ci-no-role-denied"));
            await UnroledDeniedAsync(IdentityDatabase,"dbo.b5_AssertCurrentSession",ct,
                P("@ActorId",a.ActorId),P("@SessionId",a.SessionId),
                P("@SessionEpoch",a.SessionEpoch));
            await UnroledDeniedAsync(RegistryDatabase,"dbo.b5_AssertCurrentTenant",ct,
                P("@TenantId",a.TenantId),P("@BindingId",a.BindingId),
                P("@RegistryEpoch",a.RegistryEpoch));
        }

        private async Task UnroledDeniedAsync(string dbName,string procedure,CancellationToken ct,
            params SqlParameter[] parameters)
        {
            await using var connection=new SqlConnection(Connection(dbName));
            await connection.OpenAsync(ct);
            await ImpersonateAsync(connection,UnroledLogin,ct);
            await using var tx=(SqlTransaction)await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,ct);
            await using var cmd=new SqlCommand(procedure,connection,tx)
            {CommandType=CommandType.StoredProcedure,CommandTimeout=30};
            cmd.Parameters.AddRange(parameters);
            var denied=await Assert.ThrowsAsync<SqlException>(()=>cmd.ExecuteNonQueryAsync(ct));
            Assert.Contains("DENIED",denied.Message,StringComparison.OrdinalIgnoreCase);
        }

        private async Task SecurityCommandAsync(string procedure, Guid tenant,
            CancellationToken ct, params SqlParameter[] parameters)
        {
            await using var db = new SqlConnection(FenceConnection);
            await db.OpenAsync(ct);
            await ImpersonateAsync(db, SecurityLogin, ct);
            await using var command = new SqlCommand(procedure, db)
            { CommandType = CommandType.StoredProcedure, CommandTimeout = 30 };
            command.Parameters.Add(P("@TenantId", tenant));
            command.Parameters.AddRange(parameters);
            await command.ExecuteNonQueryAsync(ct);
        }

        public async Task RuntimeReadMembershipAsync(CancellationToken ct)
        {
            await using var db = new SqlConnection(FenceConnection);
            await db.OpenAsync(ct);
            await ImpersonateAsync(db, RuntimeLogin, ct);
            await using var query = new SqlCommand("SELECT COUNT(*) FROM dbo.B5Membership", db);
            await query.ExecuteScalarAsync(ct);
        }

        public async Task ImpersonateAsync(SqlConnection db, string login, CancellationToken ct)
        {
            if (login != RuntimeLogin && login != SecurityLogin && login != RegistrySecurityLogin &&
                login != UnroledLogin)
                throw new InvalidOperationException("Unrecognized CI principal");
            await using var cmd = new SqlCommand(
                "EXECUTE AS LOGIN=N'" + login + "'", db);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<int> CountAsync(string db, CancellationToken ct) =>
            Convert.ToInt32(await ScalarAsync(Connection(db),
                "SELECT COUNT(*) FROM dbo.B5WriteWitness", ct));

        public async Task<long> EpochAsync(Guid tenant, CancellationToken ct) =>
            Convert.ToInt64(await ScalarAsync(FenceConnection,
                "SELECT FenceEpoch FROM dbo.B5TenantFence WHERE TenantId=@tenant",
                ct, P("@tenant", tenant)));

        public async Task<bool> DifferentDatabaseIdsAsync(CancellationToken ct)
        {
            var a = Convert.ToInt32(await ScalarAsync(Master,
                "SELECT DB_ID(N'" + TenantADatabase + "')", ct));
            var b = Convert.ToInt32(await ScalarAsync(Master,
                "SELECT DB_ID(N'" + TenantBDatabase + "')", ct));
            var fence = Convert.ToInt32(await ScalarAsync(Master,
                "SELECT DB_ID(N'" + FenceDatabase + "')", ct));
            var identity = Convert.ToInt32(await ScalarAsync(Master,
                "SELECT DB_ID(N'" + IdentityDatabase + "')", ct));
            var registry = Convert.ToInt32(await ScalarAsync(Master,
                "SELECT DB_ID(N'" + RegistryDatabase + "')", ct));
            return new HashSet<int> { a, b, fence, identity, registry }.Count == 5;
        }

        private string Connection(string database) =>
            new SqlConnectionStringBuilder
            {
                DataSource=server, InitialCatalog=database, IntegratedSecurity=true,
                Encrypt=SqlConnectionEncryptOption.Optional,
                TrustServerCertificate=true, Pooling=false
            }.ConnectionString;

        private async Task ApplyTwiceAsync(string databaseConnection, Type assemblyType,
            string resourceSuffix, CancellationToken ct)
        {
            var assembly = assemblyType.Assembly;
            var name = assembly.GetManifestResourceNames().Single(n =>
                n.EndsWith(resourceSuffix, StringComparison.Ordinal));
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            var text = await reader.ReadToEndAsync(ct);
            await RunAsync(databaseConnection, text, ct);
            await RunAsync(databaseConnection, text, ct);
        }

        private async Task CreateDatabaseAsync(string name, CancellationToken ct)
        {
            if (!name.StartsWith("FmcgB5", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe CI scratch DB");
            await RunAsync(Master, "CREATE DATABASE [" + name + "]", ct);
            databases.Add(name);
        }

        private async Task CreateLoginAsync(string login, CancellationToken ct)
        {
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(20)) + "aA1!";
            await RunAsync(Master, "CREATE LOGIN [" + login +
                "] WITH PASSWORD=N'" + password + "',CHECK_POLICY=OFF", ct);
            logins.Add(login);
        }

        private static SqlParameter P(string name, object value) => new(name, value);

        private async Task<object?> ScalarAsync(string connection, string sql,
            CancellationToken ct, params SqlParameter[] parameters)
        {
            await using var db = new SqlConnection(connection);
            await db.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, db);
            cmd.Parameters.AddRange(parameters);
            return await cmd.ExecuteScalarAsync(ct);
        }

        private async Task RunAsync(string connection, string sql,
            CancellationToken ct, params SqlParameter[] parameters)
        {
            await using var db = new SqlConnection(connection);
            await db.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, db) { CommandTimeout = 90 };
            cmd.Parameters.AddRange(parameters);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            SqlConnection.ClearAllPools();
            foreach (var db in databases.AsEnumerable().Reverse())
            {
                try
                {
                    await RunAsync(Master, "IF DB_ID(N'" + db + "') IS NOT NULL BEGIN " +
                        "ALTER DATABASE [" + db + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;" +
                        "DROP DATABASE [" + db + "]; END", CancellationToken.None);
                }
                catch (SqlException) { /* Disposable CI runner only */ }
            }
            foreach (var login in logins)
            {
                try { await RunAsync(Master, "DROP LOGIN [" + login + "]",
                    CancellationToken.None); }
                catch (SqlException) { /* Disposable CI runner only */ }
            }
        }
    }
}
