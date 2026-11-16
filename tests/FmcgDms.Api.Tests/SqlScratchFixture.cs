using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FmcgDms.Api.Tests;

[CollectionDefinition("SqlScratch", DisableParallelization = true)]
public sealed class SqlScratchCollection : ICollectionFixture<SqlScratchFixture> { }

public sealed class SqlScratchFixture : IAsyncLifetime
{
    public string Nonce { get; } = Guid.NewGuid().ToString("N");
    public string SqlServer { get; } = Environment.GetEnvironmentVariable("APP00A_SQL_SERVER") ?? @"(localdb)\MSSQLLocalDB";
    public string TenantADb => $"Fmcg_App00_{Nonce}_A";
    public string TenantBDb => $"Fmcg_App00_{Nonce}_B";
    public string TenantAConnectionString => ConnectionString(TenantADb);
    public string TenantBConnectionString => ConnectionString(TenantBDb);
    public const string TestKey = "SyntheticKey_For_APP00A_Test_Only_Change_Me_2026_!";

    public string ConnectionString(string db) => new SqlConnectionStringBuilder
    {
        DataSource = SqlServer, InitialCatalog = db, IntegratedSecurity = true,
        Encrypt = false, TrustServerCertificate = true
    }.ConnectionString;

    public async ValueTask InitializeAsync()
    {
        if (!SqlServer.StartsWith(@"(localdb)\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("APP-00A tests only permit disposable SQL Server LocalDB.");
        await CreateDatabaseAsync(TenantADb);
        await CreateDatabaseAsync(TenantBDb);
        ScratchMigrator.Migrate(TenantAConnectionString, "tenant-a", Nonce);
        ScratchMigrator.Migrate(TenantBConnectionString, "tenant-b", Nonce);
    }

    private async Task CreateDatabaseAsync(string db)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(db, @"\AFmcg_App00_[a-f0-9]{32}_[AB]\z"))
            throw new InvalidOperationException("Unsafe scratch database name.");
        await using var master = new SqlConnection(ConnectionString("master"));
        await master.OpenAsync();
        if (await master.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.databases WHERE name=@name", new { name = db }) != 0)
            throw new InvalidOperationException("Scratch database already exists; will not reuse.");
        await master.ExecuteAsync($"CREATE DATABASE [{db}]");
        await using var scratch = new SqlConnection(ConnectionString(db));
        await scratch.OpenAsync();
        await scratch.ExecuteAsync("CREATE TABLE dbo.App00ScratchMarker(Nonce nvarchar(32) NOT NULL PRIMARY KEY)");
        await scratch.ExecuteAsync("INSERT INTO dbo.App00ScratchMarker(Nonce) VALUES(@nonce)", new { nonce = Nonce });
    }

    public async ValueTask DisposeAsync()
    {
        // Never DROP an existing DB without checking both generated name and exact nonce.
        foreach (string name in new[] { TenantADb, TenantBDb })
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"\AFmcg_App00_[a-f0-9]{32}_[AB]\z"))
                throw new InvalidOperationException("Unsafe scratch database DROP target.");
            await using var master = new SqlConnection(ConnectionString("master"));
            await master.OpenAsync();
            if (await master.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sys.databases WHERE name=@name", new { name }) == 0) continue;
            await using (var scratch = new SqlConnection(ConnectionString(name)))
            {
                await scratch.OpenAsync();
                var marker = await scratch.ExecuteScalarAsync<string?>(
                    "IF OBJECT_ID(N'dbo.App00ScratchMarker',N'U') IS NOT NULL SELECT TOP(1) Nonce FROM dbo.App00ScratchMarker");
                if (marker != Nonce) throw new InvalidOperationException($"Refusing DROP without matching marker: {name}");
            }
            await master.ExecuteAsync($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]");
        }
    }

    public TestApiFactory CreateFactory(string? tenantAOverride = null,
        bool syntheticEnabled = true, string environment = "IntegrationTest") =>
        new(this, tenantAOverride, syntheticEnabled, environment);

    public static string IssueSyntheticToken(string tenantId,
        string actor = "synthetic-actor", string[]? roles = null, string? subject = null)
    {
        roles ??= ["warehouse", "sales", "auditor"]; // legacy composite TEST actor only
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string head = Base64Url(JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" }));
        string body = Base64Url(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["iss"] = "fmcg-synthetic-app00", ["aud"] = "fmcg-synthetic-app00",
            ["sub"] = subject ?? actor, ["actor_id"] = actor,
            ["synthetic_role"] = roles,
            ["tenant_id"] = tenantId, ["iat"] = now, ["nbf"] = now - 1, ["exp"] = now + 900
        }));
        string data = head + "." + body;
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(TestKey), Encoding.ASCII.GetBytes(data));
        return data + "." + Base64Url(mac);
    }

    private static string Base64Url(string plain) => Base64Url(Encoding.UTF8.GetBytes(plain));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static HttpClient WithTenant(HttpClient client, string tenantId,
        string actor = "synthetic-actor", string[]? roles = null)
    {
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", IssueSyntheticToken(tenantId, actor, roles));
        return client;
    }
}

public sealed class TestApiFactory(
    SqlScratchFixture fixture, string? tenantAOverride = null,
    bool syntheticEnabled = true, string environment = "IntegrationTest") : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SyntheticAuth:Enabled"] = syntheticEnabled ? "true" : "false",
                ["SyntheticAuth:SigningKey"] = SqlScratchFixture.TestKey,
                ["SyntheticAuth:Issuer"] = "fmcg-synthetic-app00",
                ["SyntheticAuth:Audience"] = "fmcg-synthetic-app00",
                ["TenantDatabases:tenant-a"] = tenantAOverride ?? fixture.TenantAConnectionString,
                ["TenantDatabases:tenant-b"] = fixture.TenantBConnectionString
            }));
    }
}
