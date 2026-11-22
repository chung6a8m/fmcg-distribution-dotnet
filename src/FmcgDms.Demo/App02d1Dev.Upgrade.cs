using System.Security.Cryptography;
using System.Text;
using Dapper;
using FmcgDms.Api.Persistence;
using FmcgDms.Api.Tenancy;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal sealed partial class App02d1Dev
{
    private readonly Dictionary<string, UpgradeEvidence> upgrade = [];

    private sealed record HistoricalFacts(string Fingerprint, int LegacyLoaded, int C3Staged,
        int Lines, int Sources, int Effects, int LinkedLoadLines);
    private sealed record UpgradeEvidence(long BeforeVersion, long AfterVersion, bool WrongMarkerDenied,
        bool WrongBindingDenied, HistoricalFacts Before, HistoricalFacts After);

    internal async Task PrepareUpgrade(string tenant, bool leaveAtD1 = false)
    {
        await run.Verify(tenant);
        string connection = run.Connection(tenant);
        await using var owner = new SqlConnection(connection);
        await owner.OpenAsync();
        long beforeVersion = await owner.QuerySingleAsync<long>("SELECT MAX(Version) FROM dbo.VersionInfo");
        if (beforeVersion != 202611220004)
            throw new InvalidOperationException("D.1 historical fixtures require the exact C.6 migration boundary.");

        string wrongNonce = Key();
        bool markerDenied;
        if (await owner.ExecuteAsync("UPDATE dbo.App00ScratchMarker SET Nonce=@wrongNonce WHERE Nonce=@Id",
            new { wrongNonce, run.Id }) != 1)
            throw new InvalidOperationException("Original scratch marker is missing.");
        try { markerDenied = RefusedMigration(connection, tenant, "Scratch marker mismatch; refusing migration."); }
        finally
        {
            if (await owner.ExecuteAsync("UPDATE dbo.App00ScratchMarker SET Nonce=@Id WHERE Nonce=@wrongNonce",
                new { wrongNonce, run.Id }) != 1)
                throw new InvalidOperationException("Could not restore the owned scratch marker.");
        }

        string peer = tenant == "tenant-a" ? "tenant-b" : "tenant-a";
        if (await owner.ExecuteAsync("UPDATE dbo.AppTenantBinding SET TenantId=@peer WHERE Slot=1 AND TenantId=@tenant",
            new { peer, tenant }) != 1)
            throw new InvalidOperationException("Original tenant binding is missing.");
        bool bindingDenied;
        try { bindingDenied = RefusedMigration(connection, tenant, "DB is already bound to a different tenant."); }
        finally
        {
            if (await owner.ExecuteAsync("UPDATE dbo.AppTenantBinding SET TenantId=@tenant WHERE Slot=1 AND TenantId=@peer",
                new { tenant, peer }) != 1)
                throw new InvalidOperationException("Could not restore the owned tenant binding.");
        }
        await run.Verify(tenant);

        using var host = Host.CreateDefaultBuilder().UseEnvironment("IntegrationTest").Build();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SyntheticAuth:Enabled"] = "true",
            [$"TenantDatabases:{tenant}"] = connection
        }).Build();
        var connections = new TenantConnections(configuration, host.Services.GetRequiredService<IHostEnvironment>());
        var warehouse = DemoScenario.Identity(tenant, "warehouse");
        var legacy = new DemoScenario(run, connections);
        await legacy.Prepare(warehouse, DemoInputs.Sample);
        await legacy.Step(warehouse, 1);
        await StageC3Fixture(tenant, owner, new App01a2CommandService(connections));

        await owner.ExecuteAsync("""
            CREATE USER App02D1UpgradeReader WITHOUT LOGIN;
            GRANT SELECT ON SCHEMA::dbo TO App02D1UpgradeReader;
            DENY INSERT,UPDATE,DELETE ON SCHEMA::dbo TO App02D1UpgradeReader;
            """);
        HistoricalFacts before = await ObserveHistoricalFacts(connection);

        ScratchMigrator.MigrateBeforeD2(connection, tenant, run.Id, LogLevel.Warning);
        await run.Verify(tenant);
        long afterVersion = await owner.QuerySingleAsync<long>("SELECT MAX(Version) FROM dbo.VersionInfo");
        HistoricalFacts after = await ObserveHistoricalFacts(connection);
        upgrade.Add(tenant, new(beforeVersion, afterVersion, markerDenied, bindingDenied, before, after));
        if (!leaveAtD1)
            ScratchMigrator.Migrate(connection, tenant, run.Id, LogLevel.Warning, bindBeforeFeatureMigrations: true);
    }

    private bool RefusedMigration(string connection, string tenant, string reason)
    {
        try
        {
            ScratchMigrator.Migrate(connection, tenant, run.Id, LogLevel.Warning, bindBeforeFeatureMigrations: true);
            return false;
        }
        catch (InvalidOperationException error) when (error.Message == reason)
        {
            return true;
        }
    }

    private static async Task StageC3Fixture(string tenant, SqlConnection db, App01a2CommandService commands)
    {
        // The prior schema permits an A-only IsC3 batch. Stage its historical
        // draft against a real source-backed A right on a trip separate from the legacy loaded trip.
        var s = DemoScene.Create(DemoInputs.Sample);
        await db.ExecuteAsync("""
            INSERT dbo.Customer(Id,TenantId,Code,Name) VALUES(@Customer,@tenant,'D1-OLD-C3-CUSTOMER','Historical C3');
            INSERT dbo.ShipToSnapshot(Id,TenantId,CustomerId,DisplayName,Address)
                VALUES(@ShipTo,@tenant,@Customer,'Historical C3','03 Duong DEV');
            INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom) VALUES(@Sku,@tenant,'D1-OLD-C3-SKU','Historical C3','EA');
            INSERT dbo.StockLocation(Id,TenantId,Code,Kind)
                VALUES(@Warehouse,@tenant,'D1-OLD-C3-MAIN','WAREHOUSE'),(@Vehicle,@tenant,'D1-OLD-C3-TRIP','VEHICLE');
            INSERT dbo.Trip(Id,TenantId,Code,BusinessDate,VehicleLocationId)
                VALUES(@Trip,@tenant,'D1-OLD-C3','20261122',@Vehicle);
            INSERT dbo.AOrder(Id,TenantId,OrderNumber,CustomerId,ShipToSnapshotId,TripId,BusinessDate)
                VALUES(@Order,@tenant,'D1-OLD-C3-ORDER',@Customer,@ShipTo,@Trip,'20261122');
            INSERT dbo.AOrderLine(Id,TenantId,AOrderId,LineNumber,SkuId,BaseQuantity,UomCode,UomToBaseFactor,UnitPrice)
                VALUES(@OrderLine,@tenant,@Order,1,@Sku,10,'EA',1,15);
            INSERT dbo.StockPool(TenantId,StockLocationId,SkuId,OnHandBaseQuantity)
                VALUES(@tenant,@Warehouse,@Sku,10);
            INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,SnapshotCode,BaseQuantity,CostAmount)
                VALUES(@tenant,@Warehouse,@Sku,'D1-OLD-C3',10,100),(@tenant,@Vehicle,@Sku,'D1-OLD-C3',0,0);
            INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,OnHandBaseQuantity,OnHandCostAmount)
                VALUES(@tenant,@Warehouse,@Sku,10,100),(@tenant,@Vehicle,@Sku,0,0);
            """, new { tenant, s.Customer, s.ShipTo, s.Sku, s.Warehouse, s.Vehicle, s.Trip, s.Order, s.OrderLine });
        var command = await commands.ReserveAStockAsync(DemoScenario.Identity(tenant, "warehouse"),
            new("d1-upgrade-c3", "reserve", "D1-OLD-C3-RESERVE", s.Order, s.OrderLine, s.Warehouse, 10), default);
        if (command is not { Status: "APPLIED", SourceId: not null })
            throw new InvalidOperationException("Historical C3 right could not be seeded.");
        await db.ExecuteAsync("""
            DECLARE @right uniqueidentifier = (
                SELECT StockRightId FROM dbo.AReservation WHERE TenantId=@tenant AND AOrderLineId=@OrderLine);
            INSERT dbo.TripManifest(Id,TenantId,ManifestNumber,TripId,WarehouseLocationId,VehicleLocationId,IsC3)
                VALUES(@Manifest,@tenant,'D1-OLD-C3-MANIFEST',@Trip,@Warehouse,@Vehicle,1);
            INSERT dbo.TripManifestLine(Id,TenantId,TripManifestId,TripId,WarehouseLocationId,VehicleLocationId,
                LineNumber,SkuId,StockRightId,RightKind,PlannedBaseQuantity,IsC3)
                VALUES(@AManifestLine,@tenant,@Manifest,@Trip,@Warehouse,@Vehicle,1,@Sku,@right,'A',10,1);
            """, new { tenant, s.OrderLine, s.Manifest, s.Trip, s.Warehouse, s.Vehicle, s.Sku, s.AManifestLine });
    }

    private static async Task<HistoricalFacts> ObserveHistoricalFacts(string connection)
    {
        await using var db = new SqlConnection(connection);
        await db.OpenAsync();
        await db.ExecuteAsync("EXECUTE AS USER = 'App02D1UpgradeReader'");
        using var data = await db.QueryMultipleAsync("""
            SELECT IIF(USER_NAME()='App02D1UpgradeReader' AND HAS_PERMS_BY_NAME('dbo','SCHEMA','SELECT')=1
                AND HAS_PERMS_BY_NAME('dbo','SCHEMA','INSERT')=0
                AND HAS_PERMS_BY_NAME('dbo','SCHEMA','UPDATE')=0
                AND HAS_PERMS_BY_NAME('dbo','SCHEMA','DELETE')=0,1,0);
            SELECT
                (SELECT COUNT(*) FROM dbo.TripManifest WHERE IsC3=0 AND Status='LOADED') AS LegacyLoaded,
                (SELECT COUNT(*) FROM dbo.TripManifest WHERE IsC3=1 AND Status='DRAFT') AS C3Staged,
                (SELECT COUNT(*) FROM dbo.TripManifestLine) AS Lines,
                (SELECT COUNT(*) FROM dbo.BusinessSource) AS Sources,
                (SELECT COUNT(*) FROM dbo.LedgerEffect) AS Effects,
                (SELECT COUNT(*) FROM dbo.TripManifestLine l JOIN dbo.BusinessSourceLine s
                    ON s.TenantId=l.TenantId AND s.Id=l.LoadSourceLineId) AS LinkedLoadLines;
            SELECT CONCAT(
                (SELECT * FROM dbo.TripManifest ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                (SELECT * FROM dbo.TripManifestLine ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                (SELECT * FROM dbo.BusinessSource ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                (SELECT * FROM dbo.BusinessSourceLine ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                (SELECT * FROM dbo.LedgerEffect ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                (SELECT * FROM dbo.PhysicalStockMovement ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                (SELECT * FROM dbo.SaleDelivery ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                (SELECT * FROM dbo.ReceivableOpenItem ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                (SELECT * FROM dbo.CashReceipt ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                (SELECT * FROM dbo.StockRight ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                (SELECT * FROM dbo.StockPool ORDER BY TenantId,StockLocationId,SkuId FOR JSON PATH, INCLUDE_NULL_VALUES),
                (SELECT * FROM dbo.PhysicalStockBalance ORDER BY TenantId,StockLocationId,SkuId FOR JSON PATH, INCLUDE_NULL_VALUES),
                (SELECT * FROM dbo.AppTenantBinding ORDER BY Slot FOR JSON PATH, INCLUDE_NULL_VALUES));
            """);
        if (await data.ReadSingleAsync<int>() != 1)
            throw new InvalidOperationException("D.1 upgrade comparator requires read-only SQL permissions.");
        var counts = await data.ReadSingleAsync<HistoricalCounts>();
        string serialized = await data.ReadSingleAsync<string>();
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized))), counts.LegacyLoaded,
            counts.C3Staged, counts.Lines, counts.Sources, counts.Effects, counts.LinkedLoadLines);
    }

    private sealed record HistoricalCounts(int LegacyLoaded, int C3Staged, int Lines, int Sources,
        int Effects, int LinkedLoadLines);

    private void VerifyUpgrade(string tenant)
    {
        var data = upgrade[tenant];
        Expect(data.BeforeVersion, 202611220004L, "Exact pre-D.1 upgrade boundary");
        Expect(data.AfterVersion, 202611220007L, "Both D.1 migrations applied");
        Expect(data.WrongMarkerDenied, true, "D.1 migration rejects a wrong scratch ownership nonce");
        Expect(data.WrongBindingDenied, true, "D.1 migration rejects a wrong persisted tenant binding");
        Expect(data.Before.LegacyLoaded, 1, "Historical loaded legacy manifest retained");
        Expect(data.Before.C3Staged, 1, "Historical C.3 A-only batch retained");
        Expect(data.Before.Lines, 3, "Historical line and right identity retained");
        Expect(data.Before.LinkedLoadLines, 2, "Historical legacy load lines retain source FK links");
        Expect(data.After, data.Before, "Historical manifests, sources, ledger, physical and rights fingerprints unchanged");
    }
}
