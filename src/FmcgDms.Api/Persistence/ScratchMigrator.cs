using Dapper;
using FluentMigrator.Runner;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace FmcgDms.Api.Persistence;

/// <summary>Only migrate owned, synthetic LocalDB scratch databases in APP-00A.</summary>
public static class ScratchMigrator
{
    public static void Migrate(string connectionString, string tenantId, string expectedNonce,
        LogLevel minimumLogLevel = LogLevel.Information)
    {
        if (string.IsNullOrWhiteSpace(expectedNonce) || !Guid.TryParseExact(expectedNonce, "N", out _))
            throw new ArgumentException("Expected GUID-N nonce.", nameof(expectedNonce));
        if (tenantId is not ("tenant-a" or "tenant-b"))
            throw new ArgumentException("Only synthetic tenants allowed.", nameof(tenantId));

        var cb = new SqlConnectionStringBuilder(connectionString);
        if (!System.Text.RegularExpressions.Regex.IsMatch(cb.InitialCatalog, @"\AFmcg_App00_[a-f0-9]{32}_[AB]\z"))
            throw new InvalidOperationException("Refusing to migrate non-scratch database.");
        string expectedDatabase = $"Fmcg_App00_{expectedNonce}_{(tenantId == "tenant-a" ? "A" : "B")}";
        if (!string.Equals(cb.InitialCatalog, expectedDatabase, StringComparison.Ordinal))
            throw new InvalidOperationException("Scratch database name does not match nonce and tenant.");
        if (!cb.DataSource.StartsWith(@"(localdb)\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("APP-00A supports LocalDB scratch only.");

        using var conn = new SqlConnection(connectionString);
        conn.Open();
        string? nonce = conn.ExecuteScalar<string?>(
            "IF OBJECT_ID(N'dbo.App00ScratchMarker', N'U') IS NOT NULL SELECT TOP(1) Nonce FROM dbo.App00ScratchMarker");
        if (!string.Equals(nonce, expectedNonce, StringComparison.Ordinal))
            throw new InvalidOperationException("Scratch marker mismatch; refusing migration.");

        string? bound = conn.ExecuteScalar<string?>(
            "IF OBJECT_ID(N'dbo.AppTenantBinding', N'U') IS NOT NULL SELECT TOP(1) TenantId FROM dbo.AppTenantBinding");
        if (bound is not null && bound != tenantId)
            throw new InvalidOperationException("DB is already bound to a different tenant.");

        using var provider = new ServiceCollection()
            .AddLogging(x => x.SetMinimumLevel(minimumLogLevel).AddConsole())
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb.AddSqlServer().WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(AppSchema).Assembly).For.Migrations())
            .BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        if (conn.ExecuteScalar<int>("SELECT COUNT(1) FROM dbo.AppTenantBinding WHERE Slot=1") == 0)
            conn.Execute("INSERT INTO dbo.AppTenantBinding(Slot,TenantId,BoundAtUtc) VALUES (1,@TenantId,SYSUTCDATETIME())",
                new { TenantId = tenantId });
        if (conn.ExecuteScalar<string>("SELECT TenantId FROM dbo.AppTenantBinding WHERE Slot=1") != tenantId)
            throw new InvalidOperationException("Unexpected tenant binding after migration.");
    }
}
