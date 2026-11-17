using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using FmcgDms.Preprod.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Keys = FmcgDms.Preprod.Identity.IdentityOptions;

namespace FmcgDms.Preprod.Identity.SqlServer.Tests;

// An actual SQL Server engine (Windows runner LocalDB), never fake SQLite.
// No operator-owned PREPROD SQL instance and no real identity/provisioning authority.
public sealed class IdentitySqlServerTests
{
    private const string Password = "ValidPass9!Longer";

    [Fact]
    public async Task Versioned_migration_replays_and_independent_SQL_connections_fence_refresh_race()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = Environment.GetEnvironmentVariable("APP02B3_SQL_SERVER");
        Assert.False(string.IsNullOrWhiteSpace(server),
            "Mandatory CI-B3 SQL engine missing. Set APP02B3_SQL_SERVER on Windows CI.");
        var script = ReadMigration();
        var dbName = "FmcgB3CI_" + Guid.NewGuid().ToString("N");
        var admin = Connection(server!, "master");
        var data = Connection(server!, dbName);
        await CreateDatabase(admin, dbName, ct);
        try
        {
            await Execute(data, script, ct);
            await Execute(data, script, ct); // Same approved migration must be idempotent.
            Assert.Equal(1, await ScalarInt(data,
                "SELECT COUNT(*) FROM dbo.__PreprodIdentitySchemaHistory WHERE SchemaVersion = N'B3_20261117_001'", ct));
            Assert.Equal(9, await ScalarInt(data,
                "SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID(N'dbo') AND name IN (N'AspNetUsers',N'AspNetRoles',N'AspNetUserClaims',N'AspNetUserRoles',N'AspNetUserLogins',N'AspNetUserTokens',N'AspNetRoleClaims',N'PreprodLoginSessions',N'PreprodRefreshGrants')", ct));

            var keys = new Keys(
                "Server=private-control.invalid;Database=ci_identity;Encrypt=True;TrustServerCertificate=False",
                RandomNumberGenerator.GetBytes(64), "ci-b3-20261117",
                "https://preprod.example.invalid", "fmcg-preprod-identity");
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddPreprodIdentity(keys, options => options.UseSqlServer(data));
            await using var root = services.BuildServiceProvider();
            Guid userId;
            string firstRefresh;
            Guid sessionId;
            await using (var seed = root.CreateAsyncScope())
            {
                var users = seed.ServiceProvider.GetRequiredService<UserManager<PreprodUser>>();
                var user = new PreprodUser
                {
                    Id = Guid.NewGuid(), UserName = "operator",
                    Disabled = false, LockoutEnabled = true
                };
                Assert.True((await users.CreateAsync(user, Password)).Succeeded);
                userId = user.Id;
                var sessions = seed.ServiceProvider.GetRequiredService<IdentitySessionService>();
                var issued = await sessions.LoginAsync("operator", Password, ct);
                Assert.NotNull(issued);
                firstRefresh = issued.RefreshToken;
                sessionId = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(issued.AccessToken)
                    .Claims.Single(x => x.Type == JwtRegisteredClaimNames.Sid).Value);
            }

            // Two distinct EF scopes create two separate SQL connections/transactions.
            // The conditional UPDATE must linearize single-use grant consumption.
            await using var clientA = root.CreateAsyncScope();
            await using var clientB = root.CreateAsyncScope();
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<IssuedCredentials?> Rotate(IServiceProvider scope)
            {
                await barrier.Task.WaitAsync(ct);
                return await scope.GetRequiredService<IdentitySessionService>()
                    .RotateAsync(firstRefresh, ct);
            }
            var a = Rotate(clientA.ServiceProvider);
            var b = Rotate(clientB.ServiceProvider);
            barrier.SetResult();
            var outcome = await Task.WhenAll(a, b);
            Assert.Single(outcome.Where(x => x is not null));
            Assert.Single(outcome.Where(x => x is null));

            var winner = outcome.Single(x => x is not null)!;
            // Independently observed raw SQL state, never the service's own "PASS".
            await using (var c = new SqlConnection(data))
            {
                await c.OpenAsync(ct);
                await using var command = new SqlCommand(
                    "SELECT Epoch, RevokedUtc FROM dbo.PreprodLoginSessions WHERE Id=@sid AND UserId=@actor",
                    c);
                command.Parameters.AddWithValue("@sid", sessionId);
                command.Parameters.AddWithValue("@actor", userId);
                await using var read = await command.ExecuteReaderAsync(ct);
                Assert.True(await read.ReadAsync(ct));
                Assert.True(read.GetInt64(0) >= 1);
                Assert.False(read.IsDBNull(1));
                Assert.False(await read.ReadAsync(ct));
            }
            Assert.Equal(2, await ScalarInt(data,
                "SELECT COUNT(*) FROM dbo.PreprodRefreshGrants", ct));
            Assert.Equal(1, await ScalarInt(data,
                "SELECT COUNT(*) FROM dbo.PreprodRefreshGrants WHERE ConsumedUtc IS NOT NULL", ct));
            await using (var observer = root.CreateAsyncScope())
            {
                var service = observer.ServiceProvider.GetRequiredService<IdentitySessionService>();
                var claims = new JwtSecurityTokenHandler().ReadJwtToken(winner.AccessToken).Claims;
                Assert.False(await service.ValidateAccessAsync(
                    new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")), ct));
                Assert.Null(await service.RotateAsync(winner.RefreshToken, ct));
            }
            // Owner's code-only merge evidence: this is real SQL Server on a CI VM,
            // not a PREPROD operator's Control Plane/epoch/backup/revocation fence.
            var result = new
            {
                TestRunId = Environment.GetEnvironmentVariable("APP02B3_TEST_RUN_ID"),
                SourceHeadSha = Environment.GetEnvironmentVariable("APP02B3_SOURCE_HEAD_SHA"),
                SqlEngine = "GitHubHostedWindows_SQLServerLocalDB_NOT_PREPROD",
                SchemaVersion = PreprodIdentitySchemaContract.Version,
                MigrationFirstApply = "PASS",
                MigrationReplay = "PASS",
                ConcurrentIndependentSqlClients = 2,
                RotationWins = outcome.Count(x => x is not null),
                RotationDenials = outcome.Count(x => x is null),
                FamilyRevokedInSql = true,
                SessionEpochInSqlAtLeast = 1,
                SuccessorAccessRejected = true,
                FullPreprodGate = "NOT_VERIFIED"
            };
            Directory.CreateDirectory("TestResults");
            await File.WriteAllTextAsync(
                Path.Combine("TestResults", "APP-02B3-SQL-WITNESS.json"),
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), ct);

            // Existing version ledger must fail closed on unknown version.
            await Execute(data, "UPDATE dbo.__PreprodIdentitySchemaHistory SET SchemaVersion = N'UNKNOWN' WHERE SchemaVersion = N'B3_20261117_001'", ct);
            await Assert.ThrowsAsync<SqlException>(() => Execute(data, script, ct));
            await Execute(data, "UPDATE dbo.__PreprodIdentitySchemaHistory SET SchemaVersion = N'B3_20261117_001' WHERE SchemaVersion = N'UNKNOWN'", ct);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await DropDatabase(admin, dbName);
        }
    }

    [Fact]
    public async Task Migration_denies_unapproved_partial_control_plane_schema()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = Environment.GetEnvironmentVariable("APP02B3_SQL_SERVER");
        Assert.False(string.IsNullOrWhiteSpace(server),
            "Mandatory CI-B3 SQL engine missing. Set APP02B3_SQL_SERVER on Windows CI.");
        var dbName = "FmcgB3Partial_" + Guid.NewGuid().ToString("N");
        var admin = Connection(server!, "master");
        var data = Connection(server!, dbName);
        await CreateDatabase(admin, dbName, ct);
        try
        {
            await Execute(data, "CREATE TABLE dbo.AspNetUsers(Id uniqueidentifier NOT NULL)", ct);
            await Assert.ThrowsAsync<SqlException>(() => Execute(data, ReadMigration(), ct));
            Assert.Equal(0, await ScalarInt(data,
                "SELECT COUNT(*) FROM sys.tables WHERE name=N'__PreprodIdentitySchemaHistory'", ct));
            Assert.Equal(1, await ScalarInt(data,
                "SELECT COUNT(*) FROM sys.tables WHERE name=N'AspNetUsers'", ct));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await DropDatabase(admin, dbName);
        }
    }

    private static string ReadMigration()
    {
        var assembly = typeof(PreprodIdentityDb).Assembly;
        var name = Assert.Single(assembly.GetManifestResourceNames()
            .Where(x => x.EndsWith("20261117_001_identity_control_plane.sql", StringComparison.Ordinal)));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Connection(string server, string database) =>
        new SqlConnectionStringBuilder
        {
            DataSource = server, InitialCatalog = database,
            IntegratedSecurity = true, TrustServerCertificate = true,
            Encrypt = SqlConnectionEncryptOption.Optional,
            ConnectTimeout = 20
        }.ConnectionString; // CI-only LocalDB, never used for runtime/Vault SQL.

    private static async Task CreateDatabase(string master, string database, CancellationToken ct)
    {
        await Execute(master, $"CREATE DATABASE [{database}]", ct);
    }

    private static async Task DropDatabase(string master, string database)
    {
        try
        {
            await Execute(master,
                $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END",
                CancellationToken.None);
        }
        catch (SqlException) { /* Disposable CI nonce; runner teardown removes it. */ }
    }

    private static async Task Execute(string cs, string sql, CancellationToken ct)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync(ct);
        await using var command = new SqlCommand(sql, c) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> ScalarInt(string cs, string sql, CancellationToken ct)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync(ct);
        await using var command = new SqlCommand(sql, c);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }
}
