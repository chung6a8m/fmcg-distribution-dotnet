using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Tests;

/// <summary>
/// APP-01E.3 test-only fault immediately after a named SQL DML statement,
/// while the real service's SERIALIZABLE transaction is still in progress.
/// The trigger is never installed by application migrations or DI.
/// </summary>
internal sealed class App01e3SqlFault(
    string connectionString, string tenant) : IAsyncDisposable
{
    private const string Trigger = "APP01E3_ThrowBeforeCommit";
    public const int ErrorNumber = 51063;

    // Explicit allowlist: a matrix cell cannot create an arbitrary DDL trigger.
    private static readonly HashSet<string> Targets = new(StringComparer.Ordinal)
    {
        "BusinessCommand:INSERT", "BusinessCommand:UPDATE",
        "BusinessSource:INSERT", "BusinessSourceLine:INSERT",
        "StockPool:UPDATE", "StockRight:INSERT", "StockRight:UPDATE",
        "BQuota:INSERT", "AOrder:UPDATE", "AConfirmedLineSnapshot:INSERT",
        "BCreditPool:UPDATE", "BCreditGrant:INSERT",
        "TripManifest:UPDATE", "TripManifestLine:UPDATE",
        "PhysicalStockBalance:UPDATE", "PhysicalStockMovement:INSERT",
        "SaleDelivery:INSERT", "ReceivableOpenItem:INSERT",
        "CashReceipt:INSERT", "ReceiptAllocation:INSERT",
        "BGrantConsumption:INSERT", "LedgerEffect:INSERT",
        "CashHandoverOffer:INSERT", "CashHandoverAcceptance:INSERT",
        "TripUnload:INSERT", "TripUnloadLine:INSERT",
        "Trip:UPDATE"
    };

    public static async Task<App01e3SqlFault> ArmAsync(
        string connectionString, string tenant, string table, string operation)
    {
        if (!Targets.Contains(table + ":" + operation))
            throw new ArgumentException("APP01E3 fault target is not allowlisted.");
        await using var db = await OpenOwnedScratchAsync(connectionString, tenant);
        if (await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sys.triggers WHERE name=@Name AND parent_class=1",
                new { Name = Trigger }) != 0)
            throw new InvalidOperationException("APP01E3 trigger already armed; refusing overwrite.");

        // Table and verb originate solely from the explicit static allowlist.
        // Only a correctly-bound physical tenant LocalDB can arm this fault.
        await db.ExecuteAsync($"""
            CREATE TRIGGER dbo.{Trigger} ON dbo.[{table}] AFTER {operation}
            AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM inserted WHERE TenantId = N'{tenant}')
                    THROW {ErrorNumber}, 'APP01E3_INJECT_PRECOMMIT', 1;
            END
            """);
        return new App01e3SqlFault(connectionString, tenant);
    }

    internal static async Task<SqlConnection> OpenOwnedScratchAsync(
        string connectionString, string tenant)
    {
        if (tenant is not ("tenant-a" or "tenant-b"))
            throw new InvalidOperationException("Unknown APP01E3 tenant.");
        var builder = new SqlConnectionStringBuilder(connectionString);
        var match = Regex.Match(builder.InitialCatalog,
            @"\AFmcg_App00_([a-f0-9]{32})_([AB])\z", RegexOptions.CultureInvariant);
        if (!builder.DataSource.StartsWith(@"(localdb)\", StringComparison.OrdinalIgnoreCase)
            || !builder.IntegratedSecurity || !match.Success
            || (tenant == "tenant-a" && match.Groups[2].Value != "A")
            || (tenant == "tenant-b" && match.Groups[2].Value != "B"))
            throw new InvalidOperationException("APP01E3 requires an owned tenant LocalDB scratch database.");

        var db = new SqlConnection(builder.ConnectionString);
        try
        {
            await db.OpenAsync();
            string? marker = await db.QuerySingleOrDefaultAsync<string>(
                "SELECT TOP(1) Nonce FROM dbo.App00ScratchMarker");
            string? binding = await db.QuerySingleOrDefaultAsync<string>(
                "SELECT TenantId FROM dbo.AppTenantBinding WHERE Slot=1");
            if (!string.Equals(marker, match.Groups[1].Value, StringComparison.Ordinal)
                || !string.Equals(binding, tenant, StringComparison.Ordinal))
                throw new InvalidOperationException("APP01E3 nonce/tenant binding is not owned.");
            return db;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await using var db = await OpenOwnedScratchAsync(connectionString, tenant);
        // Never destroy an unexpected trigger merely because the name matches.
        var target = await db.QuerySingleOrDefaultAsync<(string TableName, string Definition)>("""
            SELECT OBJECT_NAME(t.parent_id) AS TableName,
                   OBJECT_DEFINITION(t.object_id) AS Definition
            FROM sys.triggers t WHERE t.name=@Name AND t.parent_class=1
            """, new { Name = Trigger });
        if (target.TableName is null)
            throw new InvalidOperationException("APP01E3 armed trigger disappeared unexpectedly.");
        if (!Targets.Any(t => t.StartsWith(target.TableName + ":", StringComparison.Ordinal))
            || !target.Definition.Contains("APP01E3_INJECT_PRECOMMIT", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to drop an unrecognized trigger.");
        await db.ExecuteAsync($"DROP TRIGGER dbo.{Trigger}");
    }
}
