using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class App02c3DevFixture
{
    internal static async Task Seed(SqlConnection db, string tenant, string actor, bool injectFault)
    {
        await db.ExecuteAsync("""
            INSERT dbo.C3TripWriter(TenantId,TripId,ActorId)
            SELECT TenantId,Id,@actor FROM dbo.Trip WHERE TenantId=@tenant AND Code=N'C2-TRIP'
            """, new { tenant, actor });
        if (injectFault)
            await db.ExecuteAsync("""
                CREATE TRIGGER dbo.TR_C3DevAbortMovement ON dbo.PhysicalStockMovement AFTER INSERT
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.TripManifestLine l
                        ON l.TenantId=i.TenantId AND l.Id=i.TripManifestLineId
                        WHERE l.IsC3=1 AND i.BaseQuantity=13)
                        THROW 51603, 'C.3 synthetic abort after custody balance updates', 1;
                END;
                """);
    }

    internal static async Task Verify(DemoRun run, string root, string source, string apiVersion, string apiSha256)
    {
        await run.Verify("tenant-a");
        await run.Verify("tenant-b");
        await using var a = new SqlConnection(run.Connection("tenant-a"));
        await using var b = new SqlConnection(run.Connection("tenant-b"));
        string[] actual = (await a.QueryAsync<string>("""
            SELECT CONCAT('CONFIRM|',o.Status,'|',st.Origin,'|',ss.Address)
            FROM dbo.C2Confirmation c
            JOIN dbo.AOrder o ON o.TenantId=c.TenantId AND o.Id=c.OrderId
            JOIN dbo.C1ShipToSelection st ON st.TenantId=c.TenantId AND st.Id=c.SelectionId
            JOIN dbo.ShipToSnapshot ss ON ss.TenantId=o.TenantId AND ss.Id=o.ShipToSnapshotId
            UNION ALL
            SELECT CONCAT('RIGHT|',r.GrantedBaseQuantity,'|',r.ConsumedBaseQuantity,'|',r.ReleasedBaseQuantity,'|',
                r.GrantedBaseQuantity-(SELECT SUM(l.LoadedBaseQuantity) FROM dbo.TripManifestLine l
                    WHERE l.TenantId=r.TenantId AND l.StockRightId=r.Id AND l.IsC3=1))
            FROM dbo.StockRight r
            UNION ALL
            SELECT CONCAT('POOL|',OnHandBaseQuantity,'|',AllocatedBaseQuantity,'|',OnHandBaseQuantity-AllocatedBaseQuantity)
            FROM dbo.StockPool
            UNION ALL
            SELECT CONCAT('PHYSICAL|',l.Code,'|',b.OnHandBaseQuantity,'|',b.OnHandCostAmount)
            FROM dbo.PhysicalStockBalance b JOIN dbo.StockLocation l ON l.TenantId=b.TenantId AND l.Id=b.StockLocationId
            UNION ALL
            SELECT CONCAT('MANIFEST|',m.Status,'|',t.Status,'|',c.Status,'|',v.BaseQuantity,'|',v.CostAmount)
            FROM dbo.TripManifest m
            JOIN dbo.Trip t ON t.TenantId=m.TenantId AND t.Id=m.TripId
            JOIN dbo.TripManifestLine l ON l.TenantId=m.TenantId AND l.TripManifestId=m.Id
            JOIN dbo.PhysicalStockMovement v ON v.TenantId=l.TenantId AND v.TripManifestLineId=l.Id
            JOIN dbo.BusinessSource s ON s.TenantId=m.TenantId AND s.Id=m.LoadBusinessSourceId AND s.SourceKind='MANIFEST_LOAD'
            JOIN dbo.BusinessCommand c ON c.TenantId=s.TenantId AND c.Id=s.BusinessCommandId
            JOIN dbo.C2CommandScope cs ON cs.TenantId=c.TenantId AND cs.CommandRowId=c.Id
                AND cs.LoadTripId=m.TripId AND cs.RoleCode='warehouse'
            WHERE m.IsC3=1
            UNION ALL
            SELECT CONCAT('LEDGER|',e.EffectKey,'|',l.Code,'|',e.QuantityDelta,'|',e.AmountDelta)
            FROM dbo.LedgerEffect e JOIN dbo.StockLocation l ON l.TenantId=e.TenantId AND l.Id=e.StockLocationId
            WHERE e.LedgerKind='PHYSICAL_STOCK'
            UNION ALL
            SELECT CONCAT('COUNTS|',(SELECT COUNT(*) FROM dbo.BusinessCommand),'|',
                (SELECT COUNT(*) FROM dbo.TripManifest),'|',(SELECT COUNT(*) FROM dbo.TripManifestLine),'|',
                (SELECT COUNT(*) FROM dbo.BusinessSource WHERE SourceKind='MANIFEST_LOAD'),'|',
                (SELECT COUNT(*) FROM dbo.LedgerEffect WHERE LedgerKind NOT IN ('RIGHT_A','PHYSICAL_STOCK')),'|',
                (SELECT COUNT(*) FROM dbo.SaleDelivery),'|',(SELECT COUNT(*) FROM dbo.ReceivableOpenItem))
            """)).Order(StringComparer.Ordinal).ToArray();
        string[] expected = [
            "CONFIRM|CONFIRMED|DIRECT|03 Duong DEV, Khu DEV, HCM, VN",
            "COUNTS|5|2|2|2|0|0|0",
            "LEDGER|PHYSICAL-IN|C2-TRUCK|10.000|100.0000",
            "LEDGER|PHYSICAL-IN|C2-TRUCK|15.000|150.0000",
            "LEDGER|PHYSICAL-OUT|C2-MAIN|-10.000|-100.0000",
            "LEDGER|PHYSICAL-OUT|C2-MAIN|-15.000|-150.0000",
            "MANIFEST|LOADED|PREPARED|APPLIED|10.000|100.0000",
            "MANIFEST|LOADED|PREPARED|APPLIED|15.000|150.0000",
            "PHYSICAL|C2-MAIN|75.000|750.0000",
            "PHYSICAL|C2-TRUCK|25.000|250.0000",
            "POOL|75.000|15.000|60.000",
            "RIGHT|40.000|0.000|0.000|15.000"
        ];
        int peer = await b.QuerySingleAsync<int>("""
            SELECT (SELECT COUNT(*) FROM dbo.BusinessCommand)+(SELECT COUNT(*) FROM dbo.C3TripWriter)
                +(SELECT COUNT(*) FROM dbo.TripManifest)+(SELECT COUNT(*) FROM dbo.PhysicalStockMovement)
                +(SELECT COUNT(*) FROM dbo.StockRight)+(SELECT COUNT(*) FROM dbo.LedgerEffect)
                +(SELECT COUNT(*) FROM dbo.StockPool WHERE AllocatedBaseQuantity<>0 OR OnHandBaseQuantity<>100)
            """);
        bool matches = actual.SequenceEqual(expected, StringComparer.Ordinal) && peer == 0;
        string dir = Path.Combine(root, "TestResults", "app02c3");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, run.Id + "-sql.json");
        static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
        {
            source, apiVersion, apiSha256, runId = run.Id, environment = "Synthetic Windows LocalDB", instance = DemoRun.Instance,
            fixtureVersion = 1, observedAtUtc = DateTime.UtcNow,
            browserFixtureSha256 = Hash(Path.Combine(root, "web", "e2e-app02c3", "load.spec.ts")),
            browserReportSha256 = Hash(Path.Combine(root, "web", "test-results-app02c3", run.Id, "junit.xml")),
            expected, actual, expectedTenantBEffects = 0, actualTenantBEffects = peer, matches
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"APP-02C.3 SQL comparison: {file}");
        if (!matches) throw new InvalidOperationException("C.3 SQL rows differ from the fixture expectations.");
    }
}
