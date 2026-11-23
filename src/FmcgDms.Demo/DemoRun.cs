using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal sealed class DemoRun(string id)
{
    public const string Instance = @"(localdb)\MSSQLLocalDB";
    public static readonly string[] Tenants = ["tenant-a", "tenant-b"];
    public string Id { get; } = Guid.TryParseExact(id, "N", out var nonce) && nonce.ToString("N") == id
        ? id : throw new ArgumentException("Invalid RunId.");

    internal string Name(string tenant) => tenant switch
    {
        "tenant-a" => $"Fmcg_App00_{Id}_A",
        "tenant-b" => $"Fmcg_App00_{Id}_B",
        _ => throw new ArgumentException("Unknown DEV tenant.", nameof(tenant))
    };

    public string Connection(string? tenant = null) => new SqlConnectionStringBuilder
    {
        DataSource = Instance, InitialCatalog = tenant is null ? "master" : Name(tenant),
        IntegratedSecurity = true, Encrypt = false, Pooling = false
    }.ConnectionString;

    internal async Task<bool> Exists(string tenant)
    {
        await using var master = new SqlConnection(Connection());
        return await master.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.databases WHERE name=@name",
            new { name = Name(tenant) }) != 0;
    }

    public async Task Create(string tenant, bool bindBeforeFeatureMigrations = false, bool beforeD1 = false, bool app03Session = false)
    {
        await using var master = new SqlConnection(Connection());
        if (await master.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.databases WHERE name=@name",
            new { name = Name(tenant) }) != 0)
            throw new InvalidOperationException("Database already exists.");
        await master.ExecuteAsync($"CREATE DATABASE [{Name(tenant)}]");
        await using var db = new SqlConnection(Connection(tenant));
        await db.OpenAsync();
        await using var ownership = await db.BeginTransactionAsync();
        await db.ExecuteAsync("""
            CREATE TABLE dbo.App00ScratchMarker(Nonce nvarchar(32) NOT NULL PRIMARY KEY);
            INSERT dbo.App00ScratchMarker VALUES (@Id);
            CREATE TABLE dbo.LocalDemoOwner(RunId char(32) PRIMARY KEY);
            INSERT dbo.LocalDemoOwner VALUES (@Id);
            CREATE TABLE dbo.LocalDemoScene(Slot int PRIMARY KEY CHECK (Slot=1), Data nvarchar(max) NOT NULL);
            CREATE TABLE dbo.LocalDemoCheckpoint(Step int PRIMARY KEY, Data nvarchar(max) NOT NULL);
            """, new { Id }, ownership);
        if (app03Session)
            await db.ExecuteAsync("""
                CREATE TABLE dbo.LocalApp03Session(RunId char(32) PRIMARY KEY,TenantId nvarchar(64) NOT NULL);
                INSERT dbo.LocalApp03Session(RunId,TenantId) VALUES(@Id,@tenant);
                """, new { Id, tenant }, ownership);
        await ownership.CommitAsync();
        if (beforeD1)
            ScratchMigrator.MigrateBeforeD1(Connection(tenant), tenant, Id, LogLevel.Warning);
        else
            ScratchMigrator.Migrate(Connection(tenant), tenant, Id, LogLevel.Warning, bindBeforeFeatureMigrations);
    }

    public async Task Verify(string tenant, bool app03Session = false, bool allowUnbound = false)
    {
        await using var db = new SqlConnection(Connection(tenant));
        var owned = await db.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.LocalDemoOwner o
            JOIN dbo.App00ScratchMarker m ON m.Nonce=o.RunId
            WHERE o.RunId=@Id AND DB_NAME()=@name
            """, new { Id, name = Name(tenant) });
        if (owned != 1) throw new InvalidOperationException("Demo database ownership mismatch.");
        if (app03Session && await db.ExecuteScalarAsync<int>("""
            IF OBJECT_ID(N'dbo.LocalApp03Session',N'U') IS NULL SELECT 0;
            ELSE SELECT COUNT(*) FROM dbo.LocalApp03Session WHERE RunId=@Id AND TenantId=@tenant;
            """, new { Id, tenant }) != 1)
            throw new InvalidOperationException("APP-03 persistent session ownership mismatch.");
        string? bound = await db.ExecuteScalarAsync<string?>("""
            IF OBJECT_ID(N'dbo.AppTenantBinding',N'U') IS NOT NULL
                SELECT TenantId FROM dbo.AppTenantBinding WHERE Slot=1;
            """);
        if (bound != tenant && !(bound is null && app03Session && allowUnbound))
            throw new InvalidOperationException("Demo database tenant binding mismatch.");
    }

    public async Task Remove(bool app03Session = false, bool allowUnbound = false)
    {
        await using var master = new SqlConnection(Connection());
        var existing = new List<string>();
        foreach (string tenant in Tenants)
        {
            if (await master.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.databases WHERE name=@name",
                new { name = Name(tenant) }) == 0) continue;
            await VerifyRemoval(tenant, app03Session, allowUnbound);
            existing.Add(tenant);
        }
        foreach (string tenant in existing)
        {
            string name = Name(tenant);
            await master.ExecuteAsync($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
        }
    }

    public async Task RemoveDevSlot(string tenant)
    {
        if (tenant is not ("tenant-a" or "tenant-b")) throw new ArgumentException("Unknown DEV tenant.", nameof(tenant));
        await using var master = new SqlConnection(Connection());
        string name = Name(tenant);
        if (await master.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.databases WHERE name=@name", new { name }) == 0)
            return;
        await VerifyRemoval(tenant, app03Session: false);
        await master.ExecuteAsync($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
    }

    private async Task VerifyRemoval(string tenant, bool app03Session, bool allowUnbound = false)
    {
        await Verify(tenant, app03Session, allowUnbound);
        if (app03Session) return;
        await using var db = new SqlConnection(Connection(tenant));
        if (await db.ExecuteScalarAsync<int?>("SELECT OBJECT_ID(N'dbo.LocalApp03Session',N'U')") is not null)
            throw new InvalidOperationException("Persistent APP-03 ownership requires the APP-03 Remove command.");
    }
}
