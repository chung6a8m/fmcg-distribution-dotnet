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

    private string Name(string tenant) => $"Fmcg_App00_{Id}_{(tenant == "tenant-a" ? "A" : "B")}";

    public string Connection(string? tenant = null) => new SqlConnectionStringBuilder
    {
        DataSource = Instance, InitialCatalog = tenant is null ? "master" : Name(tenant),
        IntegratedSecurity = true, Encrypt = false, Pooling = false
    }.ConnectionString;

    public async Task Create(string tenant, bool bindBeforeFeatureMigrations = false)
    {
        await using var master = new SqlConnection(Connection());
        if (await master.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.databases WHERE name=@name",
            new { name = Name(tenant) }) != 0)
            throw new InvalidOperationException("Database already exists.");
        await master.ExecuteAsync($"CREATE DATABASE [{Name(tenant)}]");
        await using var db = new SqlConnection(Connection(tenant));
        await db.ExecuteAsync("""
            CREATE TABLE dbo.App00ScratchMarker(Nonce nvarchar(32) NOT NULL PRIMARY KEY);
            INSERT dbo.App00ScratchMarker VALUES (@Id);
            CREATE TABLE dbo.LocalDemoOwner(RunId char(32) PRIMARY KEY);
            INSERT dbo.LocalDemoOwner VALUES (@Id);
            CREATE TABLE dbo.LocalDemoScene(Slot int PRIMARY KEY CHECK (Slot=1), Data nvarchar(max) NOT NULL);
            CREATE TABLE dbo.LocalDemoCheckpoint(Step int PRIMARY KEY, Data nvarchar(max) NOT NULL);
            """, new { Id });
        ScratchMigrator.Migrate(Connection(tenant), tenant, Id, LogLevel.Warning, bindBeforeFeatureMigrations);
    }

    public async Task Verify(string tenant)
    {
        await using var db = new SqlConnection(Connection(tenant));
        var owned = await db.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.LocalDemoOwner o
            JOIN dbo.App00ScratchMarker m ON m.Nonce=o.RunId
            JOIN dbo.AppTenantBinding t ON t.Slot=1
            WHERE o.RunId=@Id AND t.TenantId=@tenant AND DB_NAME()=@name
            """, new { Id, tenant, name = Name(tenant) });
        if (owned != 1) throw new InvalidOperationException("Demo database ownership mismatch.");
    }

    public async Task Remove()
    {
        await using var master = new SqlConnection(Connection());
        var existing = new List<string>();
        foreach (string tenant in Tenants)
        {
            if (await master.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.databases WHERE name=@name",
                new { name = Name(tenant) }) == 0) continue;
            await Verify(tenant);
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
        await Verify(tenant);
        await master.ExecuteAsync($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
    }
}
