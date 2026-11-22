using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class App02c2DevFixture
{
    internal static Task Seed(SqlConnection db, string tenant, string actor) => db.ExecuteAsync("""
        DECLARE @customer uniqueidentifier=NEWID(), @sku uniqueidentifier=NEWID(),
            @warehouse uniqueidentifier=NEWID(), @vehicle uniqueidentifier=NEWID(), @trip uniqueidentifier=NEWID(),
            @now datetime2(7)=SYSUTCDATETIME();
        INSERT dbo.App02ActorRoleGrant(TenantId,ActorId,RoleCode,IsActive,ChangedBy)
        VALUES(@tenant,@actor,N'warehouse',1,N'c2-dev-launcher');
        INSERT dbo.Customer(Id,TenantId,Code,Name) VALUES(@customer,@tenant,N'C2-FIXTURE',N'Khách hàng C.2 DEV');
        INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom) VALUES(@sku,@tenant,N'C2-SKU',N'Hàng tổng hợp',N'EA');
        INSERT dbo.StockLocation(Id,TenantId,Code,Kind)
        VALUES(@warehouse,@tenant,N'C2-MAIN',N'WAREHOUSE'),(@vehicle,@tenant,N'C2-TRUCK',N'VEHICLE');
        INSERT dbo.Trip(Id,TenantId,Code,BusinessDate,VehicleLocationId)
        VALUES(@trip,@tenant,N'C2-TRIP',CAST(@now AS date),@vehicle);
        INSERT dbo.StockPool(TenantId,StockLocationId,SkuId,OnHandBaseQuantity)
        VALUES(@tenant,@warehouse,@sku,100);
        INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,SnapshotCode,BaseQuantity,CostAmount)
        VALUES(@tenant,@warehouse,@sku,N'C2-v1',100,1000),(@tenant,@vehicle,@sku,N'C2-v1',0,0);
        INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,OnHandBaseQuantity,OnHandCostAmount)
        VALUES(@tenant,@warehouse,@sku,100,1000),(@tenant,@vehicle,@sku,0,0);
        INSERT dbo.C2CommercialPolicy(TenantId,Id,CustomerId,SkuId,Version,Decision,EffectiveFromUtc,EffectiveUntilUtc,
            Maker,Checker,ApprovedAtUtc,EvidenceRef,UnitPrice,DiscountPerUnit,TaxRate,TermsDays,CreditCap,SellerTimeZone)
        VALUES(@tenant,NEWID(),@customer,@sku,1,'APPROVED',DATEADD(day,-1,@now),DATEADD(day,1,@now),
            N'c2-fixture-maker',N'c2-fixture-checker',DATEADD(day,-1,@now),N'c2-fixture-v1',15,0,0,7,1000,'Asia/Ho_Chi_Minh');
        """, new { tenant, actor });

    private const string EffectCounts = """
        SELECT CONCAT('POSTINGS|',(SELECT COUNT(*) FROM dbo.PhysicalStockMovement),'|',
            (SELECT COUNT(*) FROM dbo.SaleDelivery),'|',(SELECT COUNT(*) FROM dbo.ReceivableOpenItem),'|',
            (SELECT COUNT(*) FROM dbo.LedgerEffect WHERE LedgerKind<>'RIGHT_A'))
        """;

    internal static async Task Verify(DemoRun run, string root, string source, string apiVersion, string apiSha256)
    {
        await run.Verify("tenant-a");
        await run.Verify("tenant-b");
        await using var a = new SqlConnection(run.Connection("tenant-a"));
        await using var b = new SqlConnection(run.Connection("tenant-b"));
        string[] actual = (await a.QueryAsync<string>("""
            SELECT CONCAT('CONFIRM|',o.Status,'|',c.Code,'|',s.Origin,'|',f.Address,'|',
                al.ConfirmedBaseQuantity,'|',al.UnitPriceSnapshot,'|',al.TermsDaysSnapshot,'|',
                p.PolicyVersion,'|',p.BaseUom,'|',p.GrossAmount,'|',
                JSON_VALUE(p.PolicyPayload,'$.currency'),'|',JSON_VALUE(p.PolicyPayload,'$.invoiceEligible'),'|',
                CASE WHEN p.SourceHash=HASHBYTES('SHA2_256',
                    CONVERT(varchar(max),p.PolicyPayload COLLATE Latin1_General_100_BIN2_UTF8))
                    THEN 'HASHED' ELSE 'MISMATCH' END,'|',
                CASE WHEN o.BusinessDate=CAST((o.ConfirmedAtUtc AT TIME ZONE 'UTC') AT TIME ZONE 'SE Asia Standard Time' AS date)
                    THEN 'DATED' ELSE 'MISMATCH' END)
            FROM dbo.C2Confirmation x
            JOIN dbo.AOrder o ON o.TenantId=x.TenantId AND o.Id=x.OrderId
            JOIN dbo.Customer c ON c.TenantId=x.TenantId AND c.Id=x.CustomerId
            JOIN dbo.C1ShipToSelection s ON s.TenantId=x.TenantId AND s.Id=x.SelectionId
            JOIN dbo.ShipToSnapshot f ON f.TenantId=x.TenantId AND f.Id=x.ShipToSnapshotId
            JOIN dbo.AConfirmedLineSnapshot al ON al.TenantId=x.TenantId AND al.AOrderId=x.OrderId
                AND al.ConfirmBusinessSourceId=x.ConfirmSourceId
            JOIN dbo.C2LinePolicy p ON p.TenantId=al.TenantId AND p.OrderLineId=al.AOrderLineId
            UNION ALL
            SELECT CONCAT('RIGHT|',r.GrantedBaseQuantity,'|',r.ConsumedBaseQuantity,'|',r.ReleasedBaseQuantity,'|',e.QuantityDelta)
            FROM dbo.StockRight r JOIN dbo.LedgerEffect e
                ON e.TenantId=r.TenantId AND e.BusinessSourceLineId=r.BusinessSourceLineId AND e.LedgerKind='RIGHT_A'
            UNION ALL
            SELECT CONCAT('POOL|',OnHandBaseQuantity,'|',AllocatedBaseQuantity) FROM dbo.StockPool
            UNION ALL
            SELECT CONCAT('PHYSICAL|',l.Code,'|',b.OnHandBaseQuantity,'|',b.OnHandCostAmount)
            FROM dbo.PhysicalStockBalance b JOIN dbo.StockLocation l ON l.TenantId=b.TenantId AND l.Id=b.StockLocationId
            UNION ALL
            SELECT CONCAT('COMMAND|',Status,'|',COALESCE(ReasonCode,OutcomeCode),'|',COUNT(*))
            FROM dbo.BusinessCommand GROUP BY Status,COALESCE(ReasonCode,OutcomeCode)
            UNION ALL
            """ + "\n" + EffectCounts)).Order(StringComparer.Ordinal).ToArray();
        string[] expected = [
            "COMMAND|APPLIED|A_STOCK_RESERVED|2",
            "COMMAND|APPLIED|C2_DRAFT_CREATED|1",
            "COMMAND|APPLIED|C2_ORDER_CONFIRMED|1",
            "COMMAND|REJECTED|A_LINE_OVER_RESERVED|1",
            "CONFIRM|CONFIRMED|C2-FIXTURE|DIRECT|02 Duong DEV, Khu DEV, HCM, VN|40.000|15.0000|7|1|EA|600.0000|SYNTHETIC_UNITLESS|false|HASHED|DATED",
            "PHYSICAL|C2-MAIN|100.000|1000.0000",
            "PHYSICAL|C2-TRUCK|0.000|0.0000",
            "POOL|100.000|40.000",
            "POSTINGS|0|0|0|0",
            "RIGHT|15.000|0.000|0.000|15.000",
            "RIGHT|25.000|0.000|0.000|25.000"
        ];
        var peer = await b.QuerySingleAsync<int>("""
            SELECT (SELECT COUNT(*) FROM dbo.C2OrderDraft)+(SELECT COUNT(*) FROM dbo.BusinessCommand)
                +(SELECT COUNT(*) FROM dbo.AOrder)+(SELECT COUNT(*) FROM dbo.StockRight)
                +(SELECT COUNT(*) FROM dbo.C1ShipToSelection)
                +(SELECT COUNT(*) FROM dbo.StockPool WHERE AllocatedBaseQuantity<>0 OR OnHandBaseQuantity<>100)
            """);
        bool matches = actual.SequenceEqual(expected, StringComparer.Ordinal) && peer == 0;
        string dir = Path.Combine(root, "TestResults", "app02c2");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, run.Id + "-sql.json");
        static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
        {
            source, apiVersion, apiSha256, runId = run.Id, environment = "Synthetic Windows LocalDB", instance = DemoRun.Instance,
            fixtureVersion = 1, observedAtUtc = DateTime.UtcNow,
            browserFixtureSha256 = Hash(Path.Combine(root, "web", "e2e-app02c2", "orders.spec.ts")),
            browserReportSha256 = Hash(Path.Combine(root, "web", "test-results-app02c2", run.Id, "junit.xml")),
            expected, actual, expectedTenantBEffects = 0, actualTenantBEffects = peer, matches
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"APP-02C.2 SQL comparison: {file}");
        if (!matches) throw new InvalidOperationException("C.2 SQL rows differ from the fixture expectations.");
    }
}
