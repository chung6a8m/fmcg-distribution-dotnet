using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class App02c4DevFixture
{
    private sealed record Dates(string BusinessDate, string DueDate);

    internal static async Task Seed(SqlConnection db, string tenant, string actor, bool injectFault)
    {
        await db.ExecuteAsync("""
            UPDATE dbo.Trip SET BusinessDate='20000101' WHERE TenantId=@tenant AND Code=N'C2-TRIP';
            INSERT dbo.C4DeliveryWriter(TenantId,TripId,ActorId)
            SELECT TenantId,Id,@actor FROM dbo.Trip WHERE TenantId=@tenant AND Code=N'C2-TRIP';
            """, new { tenant, actor });
        if (injectFault)
            await db.ExecuteAsync("""
                CREATE TRIGGER dbo.TR_C4DevAbortAr ON dbo.LedgerEffect AFTER INSERT AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.SaleDelivery d
                        ON d.TenantId=i.TenantId AND d.BusinessSourceLineId=i.BusinessSourceLineId
                        WHERE d.C4EvidenceId IS NOT NULL AND d.BaseQuantity=13 AND i.EffectKey='AR-GROSS')
                        THROW 51609, 'C.4 synthetic fault during AR ledger posting.', 1;
                END;
                """);
    }

    internal static async Task Verify(DemoRun run, string root, string source, string apiVersion, string apiSha256, bool c5)
    {
        await run.Verify("tenant-a");
        await run.Verify("tenant-b");
        await using var a = new SqlConnection(run.Connection("tenant-a"));
        await using var b = new SqlConnection(run.Connection("tenant-b"));
        string[] actual = (await a.QueryAsync<string>("""
            SELECT CONCAT('DELIVERY|',o.Status,'|',d.BaseQuantity,'|',d.PayableAmount,'|',d.CostAmount,'|',
                IIF(d.BusinessDate=CONVERT(date,DATEADD(hour,7,e.OccurredAtUtc)) AND d.BusinessDate<>t.BusinessDate,'LOCAL','BAD'),'|',sh.Address)
            FROM dbo.SaleDelivery d
            JOIN dbo.C4DeliveryEvidence e ON e.TenantId=d.TenantId AND e.Id=d.C4EvidenceId
            JOIN dbo.AOrder o ON o.TenantId=d.TenantId AND o.Id=d.AOrderId
            JOIN dbo.Trip t ON t.TenantId=d.TenantId AND t.Id=d.TripId
            JOIN dbo.ShipToSnapshot sh ON sh.TenantId=d.TenantId AND sh.Id=d.ShipToSnapshotId
            UNION ALL
            SELECT CONCAT('AR|',ar.OriginalAmount,'|',ar.OutstandingAmount,'|',DATEDIFF(day,d.BusinessDate,ar.DueBusinessDate),'|',
                IIF(p.TermsDaysSnapshot=7 AND ar.DueBusinessDate=DATEADD(day,7,d.BusinessDate),'FROZEN','BAD'))
            FROM dbo.ReceivableOpenItem ar
            JOIN dbo.SaleDelivery d ON d.TenantId=ar.TenantId AND d.Id=ar.SaleDeliveryId
            JOIN dbo.AConfirmedLineSnapshot p ON p.TenantId=d.TenantId AND p.AOrderLineId=d.AOrderLineId
            UNION ALL
            SELECT CONCAT('RIGHT|',r.Status,'|',r.GrantedBaseQuantity,'|',r.ConsumedBaseQuantity,'|',
                r.GrantedBaseQuantity-r.ConsumedBaseQuantity-r.ReleasedBaseQuantity) FROM dbo.StockRight r
            UNION ALL
            SELECT CONCAT('PHYSICAL|',l.Code,'|',b.OnHandBaseQuantity,'|',b.OnHandCostAmount)
            FROM dbo.PhysicalStockBalance b JOIN dbo.StockLocation l ON l.TenantId=b.TenantId AND l.Id=b.StockLocationId
            UNION ALL
            SELECT CONCAT('LOAD|',l.LoadedBaseQuantity,'|',l.LoadedCostAmount,'|',bs.SourceKind)
            FROM dbo.TripManifestLine l JOIN dbo.BusinessSource bs ON bs.TenantId=l.TenantId AND bs.Id=l.LoadBusinessSourceId
            UNION ALL
            SELECT CONCAT('DELIVERY-EFFECT|',e.EffectKey,'|',e.QuantityDelta,'|',e.AmountDelta)
            FROM dbo.LedgerEffect e JOIN dbo.SaleDelivery d
                ON d.TenantId=e.TenantId AND d.BusinessSourceLineId=e.BusinessSourceLineId
            UNION ALL
            SELECT CONCAT('CASE|',e.Quantity,'|',c.Status,'|',COUNT(d.Id))
            FROM dbo.C4DeliveryEvidence e LEFT JOIN dbo.SaleDelivery d ON d.TenantId=e.TenantId AND d.C4EvidenceId=e.Id
            JOIN dbo.BusinessCommand c ON c.TenantId=e.TenantId AND c.Id=e.Id
            GROUP BY e.Quantity,c.Status
            UNION ALL
            SELECT CONCAT('COUNTS|',(SELECT COUNT(*) FROM dbo.C4DeliveryEvidence),'|',
                (SELECT COUNT(*) FROM dbo.C2CommandScope WHERE DeliveryTripId IS NOT NULL),'|',
                (SELECT COUNT(*) FROM dbo.BusinessSource WHERE SourceKind='A_DELIVERY'),'|',
                (SELECT COUNT(*) FROM dbo.DeliveryStockIssue),'|',(SELECT COUNT(*) FROM dbo.ReceivableOpenItem),'|',
                (SELECT COUNT(*) FROM dbo.CashReceipt),'|',(SELECT COUNT(*) FROM dbo.BGrantConsumption))
            """)).Order(StringComparer.Ordinal).ToArray();
        string[] expected = [
            "AR|375.0000|375.0000|7|FROZEN",
            "CASE|13.000|RECEIVED|0", "CASE|25.000|APPLIED|1", "COUNTS|2|2|1|1|1|0|0",
            "DELIVERY-EFFECT|AR-GROSS|0.000|375.0000",
            "DELIVERY-EFFECT|COGS|0.000|250.0000",
            "DELIVERY-EFFECT|PHYSICAL-OUT|-25.000|-250.0000",
            "DELIVERY-EFFECT|RIGHT-A-USED|-25.000|0.0000",
            "DELIVERY|PARTIALLY_DELIVERED|25.000|375.0000|250.0000|LOCAL|04 Duong DEV, Khu DEV, HCM, VN",
            c5 ? "LOAD|40.001|400.0100|MANIFEST_LOAD" : "LOAD|40.000|400.0000|MANIFEST_LOAD",
            c5 ? "PHYSICAL|C2-MAIN|59.999|599.9900" : "PHYSICAL|C2-MAIN|60.000|600.0000",
            c5 ? "PHYSICAL|C2-TRUCK|15.001|150.0100" : "PHYSICAL|C2-TRUCK|15.000|150.0000",
            c5 ? "RIGHT|HELD|40.001|25.000|15.001" : "RIGHT|HELD|40.000|25.000|15.000"
        ];
        int peer = await b.QuerySingleAsync<int>("""
            SELECT (SELECT COUNT(*) FROM dbo.C4DeliveryEvidence)+(SELECT COUNT(*) FROM dbo.C4DeliveryWriter)
                +(SELECT COUNT(*) FROM dbo.BusinessCommand)+(SELECT COUNT(*) FROM dbo.TripManifest)
                +(SELECT COUNT(*) FROM dbo.SaleDelivery)+(SELECT COUNT(*) FROM dbo.ReceivableOpenItem)
                +(SELECT COUNT(*) FROM dbo.LedgerEffect)
            """);
        bool matches = actual.SequenceEqual(expected, StringComparer.Ordinal) && peer == 0;
        var dates = await a.QuerySingleOrDefaultAsync<Dates>("""
            SELECT CONVERT(char(10),d.BusinessDate,23) AS BusinessDate,CONVERT(char(10),ar.DueBusinessDate,23) AS DueDate
            FROM dbo.SaleDelivery d JOIN dbo.ReceivableOpenItem ar ON ar.TenantId=d.TenantId AND ar.SaleDeliveryId=d.Id
            WHERE d.C4EvidenceId IS NOT NULL
            """);
        string increment = c5 ? "app02c5" : "app02c4";
        string dir = Path.Combine(root, "TestResults", increment);
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, run.Id + "-sql.json");
        static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
        {
            source, apiVersion, apiSha256, runId = run.Id, environment = "Synthetic Windows LocalDB", instance = DemoRun.Instance,
            fixtureVersion = 1, observedAtUtc = DateTime.UtcNow,
            browserFixtureSha256 = Hash(Path.Combine(root, "web", "e2e-" + increment, c5 ? "workspace.spec.ts" : "delivery.spec.ts")),
            browserReportSha256 = Hash(Path.Combine(root, "web", "test-results-" + increment, run.Id, "junit.xml")),
            dates, expected, actual, expectedTenantBEffects = 0, actualTenantBEffects = peer, matches
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{increment} SQL comparison: {file}");
        if (!matches) throw new InvalidOperationException("C.4 SQL rows differ from the fixture expectations.");
    }
}
