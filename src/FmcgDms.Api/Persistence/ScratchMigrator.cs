using Dapper;
using FluentMigrator.Runner;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace FmcgDms.Api.Persistence;

/// <summary>Only migrate owned, synthetic LocalDB scratch databases in APP-00A.</summary>
public static class ScratchMigrator
{
    public static void Migrate(string connectionString, string tenantId, string expectedNonce,
        LogLevel minimumLogLevel = LogLevel.Information, bool bindBeforeFeatureMigrations = false) =>
        MigrateCore(connectionString, tenantId, expectedNonce, minimumLogLevel, bindBeforeFeatureMigrations, throughVersion: null);

    public static void MigrateBeforeD1(string connectionString, string tenantId, string expectedNonce,
        LogLevel minimumLogLevel = LogLevel.Information) =>
        MigrateCore(connectionString, tenantId, expectedNonce, minimumLogLevel, bindBeforeFeatureMigrations: true, throughVersion: 202611220004);

    public static void MigrateBeforeD2(string connectionString, string tenantId, string expectedNonce,
        LogLevel minimumLogLevel = LogLevel.Information) =>
        MigrateCore(connectionString, tenantId, expectedNonce, minimumLogLevel, bindBeforeFeatureMigrations: true,
            throughVersion: 202611220007);

    public static void MigrateBeforeD3(string connectionString, string tenantId, string expectedNonce,
        LogLevel minimumLogLevel = LogLevel.Information) =>
        MigrateCore(connectionString, tenantId, expectedNonce, minimumLogLevel, bindBeforeFeatureMigrations: true,
            throughVersion: 202611220008);

    private static void MigrateCore(string connectionString, string tenantId, string expectedNonce,
        LogLevel minimumLogLevel, bool bindBeforeFeatureMigrations, long? throughVersion)
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
        if (bindBeforeFeatureMigrations && (!string.Equals(cb.DataSource, @"(localdb)\MSSQLLocalDB", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(cb.AttachDBFilename) || !cb.IntegratedSecurity || cb.UserInstance
            || !string.IsNullOrEmpty(cb.FailoverPartner)))
            throw new InvalidOperationException("The feature upgrade fixture requires the default integrated LocalDB scratch target.");

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
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();

        // The disposable C.1 launcher must be able to prove the tenant binding
        // before cleanup even if a later feature migration fails.
        if (bindBeforeFeatureMigrations && bound is null)
        {
            runner.MigrateUp(202611150001);
            BindTenant();
        }
        if (throughVersion is long version)
            runner.MigrateUp(version);
        else
            runner.MigrateUp();
        BindTenant();

        void BindTenant()
        {
            if (conn.ExecuteScalar<int>("SELECT COUNT(1) FROM dbo.AppTenantBinding WHERE Slot=1") == 0)
                conn.Execute("INSERT INTO dbo.AppTenantBinding(Slot,TenantId,BoundAtUtc) VALUES (1,@TenantId,SYSUTCDATETIME())",
                    new { TenantId = tenantId });
            if (conn.ExecuteScalar<string>("SELECT TenantId FROM dbo.AppTenantBinding WHERE Slot=1") != tenantId)
                throw new InvalidOperationException("Unexpected tenant binding after migration.");
        }
    }
}
