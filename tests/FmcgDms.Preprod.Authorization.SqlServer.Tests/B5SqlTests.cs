using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using FmcgDms.Preprod.Authorization;
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
        await f.SeedAsync(actor, role, bindingA, bindingB, ct);
        var a = new AuthorizationContext(f.TenantA, f.TenantA, actor, session,
            bindingA, 1, 1, 1, "tenant.write", "stock:ci-only");
        var b = a with { TenantId = f.TenantB, ResourceTenantId = f.TenantB,
            BindingId = bindingB };
        var fence = new SqlAuthorizationFence(f.FenceConnection)
        {
            InitializeCiSession = (db, token) => f.ImpersonateAsync(db, f.RuntimeLogin, token)
        };
        CurrentSessionCheck current = (user, sid, _) =>
            Task.FromResult(user == actor && sid == session);
        static Task<bool> DenySession(Guid _, Guid __, CancellationToken ___) =>
            Task.FromResult(false);

        Assert.Equal(1, await fence.ExecuteAsync(a, current,
            (db, tx, token) => f.WriteAsync(db, tx, f.TenantADatabase, token), ct));
        Assert.Equal(1, await f.CountAsync(f.TenantADatabase, ct));

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => fence.ExecuteAsync(
            b, current, (db, tx, token) => f.WriteAsync(db, tx, f.TenantBDatabase, token), ct));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => fence.ExecuteAsync(
            a with { BindingId = Guid.NewGuid() }, current,
            (db, tx, token) => f.WriteAsync(db, tx, f.TenantADatabase, token), ct));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => fence.ExecuteAsync(
            a with { ActorId = Guid.NewGuid() },
            (_, _, _) => Task.FromResult(true),
            (db, tx, token) => f.WriteAsync(db, tx, f.TenantADatabase, token), ct));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => fence.ExecuteAsync(
            a, DenySession,
            (db, tx, token) => f.WriteAsync(db, tx, f.TenantADatabase, token), ct));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => fence.ExecuteAsync(
            a with { MakerId = actor }, current,
            (db, tx, token) => f.WriteAsync(db, tx, f.TenantADatabase, token), ct));
        Assert.Equal(0, await f.CountAsync(f.TenantBDatabase, ct));
        Assert.Equal(1, await f.CountAsync(f.TenantADatabase, ct));

        // The fenced effect holds the tenant row lock UNTIL its SQL COMMIT.
        // An independently authenticated security login cannot overtake it.
        var inside = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var protectedWork = fence.ExecuteAsync(a, current, async (db, tx, token) =>
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
            a, current, (db, tx, token) => f.WriteAsync(db, tx, f.TenantADatabase, token), ct));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
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
            b, (_, _, _) => Task.FromResult(true),
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
        public string RuntimeLogin { get; }
        public string SecurityLogin { get; }
        public string FenceConnection => Connection(FenceDatabase);
        private string Master => Connection("master");

        private Fixture(string sqlServer, string nonce)
        {
            server = sqlServer;
            FenceDatabase = "FmcgB5Fence_" + nonce;
            TenantADatabase = "FmcgB5TenA_" + nonce;
            TenantBDatabase = "FmcgB5TenB_" + nonce;
            RuntimeLogin = "b5rt_" + nonce;
            SecurityLogin = "b5sec_" + nonce;
        }

        public static async Task<Fixture> CreateAsync(CancellationToken ct)
        {
            var server = Environment.GetEnvironmentVariable("APP02B5_SQL_SERVER");
            if (string.IsNullOrWhiteSpace(server) ||
                !server.Contains("(localdb)", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("B.5 SQL verification requires disposable LocalDB");
            var f = new Fixture(server, Guid.NewGuid().ToString("N")[..12]);
            foreach (var name in new[] { f.FenceDatabase, f.TenantADatabase, f.TenantBDatabase })
                await f.CreateDatabaseAsync(name, ct);
            var migrationName = typeof(B5Schema).Assembly.GetManifestResourceNames().Single(
                n => n.EndsWith("20261118_001_independent_fence.sql", StringComparison.Ordinal));
            using var stream = new StreamReader(typeof(B5Schema).Assembly
                .GetManifestResourceStream(migrationName)!);
            var script = await stream.ReadToEndAsync(ct);
            await f.RunAsync(f.FenceConnection, script, ct);
            await f.RunAsync(f.FenceConnection, script, ct);
            await f.CreateLoginAsync(f.RuntimeLogin, ct);
            await f.CreateLoginAsync(f.SecurityLogin, ct);
            await f.RunAsync(f.FenceConnection,
                "CREATE USER [" + f.RuntimeLogin + "] FOR LOGIN [" + f.RuntimeLogin + "];" +
                "ALTER ROLE b5_runtime ADD MEMBER [" + f.RuntimeLogin + "];" +
                "CREATE USER [" + f.SecurityLogin + "] FOR LOGIN [" + f.SecurityLogin + "];" +
                "ALTER ROLE b5_revoker ADD MEMBER [" + f.SecurityLogin + "];", ct);
            foreach (var database in new[] { f.TenantADatabase, f.TenantBDatabase })
                await f.RunAsync(f.Connection(database),
                    "CREATE TABLE dbo.B5WriteWitness(Id bigint IDENTITY(1,1) PRIMARY KEY);" +
                    "CREATE USER [" + f.RuntimeLogin + "] FOR LOGIN [" + f.RuntimeLogin + "];" +
                    "GRANT INSERT ON dbo.B5WriteWitness TO [" + f.RuntimeLogin + "];", ct);
            return f;
        }

        public async Task SeedAsync(Guid actor, Guid role, Guid bindingA, Guid bindingB,
            CancellationToken ct)
        {
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

        public async Task RevokeAsync(Guid actor, CancellationToken ct) =>
            await SecurityCommandAsync("dbo.b5_RevokeMember", TenantA, ct,
                P("@ExpectedFenceEpoch", 1L), P("@ActorId", actor),
                P("@EvidenceRef", "ci-revoke-commit-race"));

        public async Task SuspendAsync(Guid tenant, CancellationToken ct) =>
            await SecurityCommandAsync("dbo.b5_SuspendTenant", tenant, ct,
                P("@ExpectedFenceEpoch", 1L), P("@EvidenceRef", "ci-tenant-suspend"));

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
            if (login != RuntimeLogin && login != SecurityLogin)
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
            return a != b && a != fence && b != fence;
        }

        private string Connection(string database) =>
            new SqlConnectionStringBuilder
            {
                DataSource=server, InitialCatalog=database, IntegratedSecurity=true,
                Encrypt=SqlConnectionEncryptOption.Optional,
                TrustServerCertificate=true, Pooling=false
            }.ConnectionString;

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
