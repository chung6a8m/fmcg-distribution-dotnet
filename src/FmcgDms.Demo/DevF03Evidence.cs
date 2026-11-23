using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class DevF03Evidence
{
    internal static readonly DateTime FixtureUtc = new(2026, 11, 23, 2, 0, 0, DateTimeKind.Utc);
    internal sealed record StockRow(string Tenant, string Location, decimal Quantity, decimal Cost);
    private sealed record OpeningRow(string Tenant, string Location, string Lot, DateTime ExpiresOn, decimal Quantity);
    private sealed record LotRow(string Stage, string Lot, decimal Quantity, Guid SourceId, Guid? PredecessorId, bool Linked);
    private sealed record QuantityRow(string Stage, string Lot, decimal Quantity);
    private sealed record Fact(string Metric, decimal Value);

    internal static async Task<Guid> Seed(SqlConnection db, string tenant, CancellationToken ct)
    {
        await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(ct);
        Guid quarantine = await db.QuerySingleAsync<Guid>(new CommandDefinition("""
            DECLARE @sku uniqueidentifier, @main uniqueidentifier, @quarantine uniqueidentifier=NEWID();
            SELECT @sku=Id FROM dbo.Sku WHERE TenantId=@Tenant AND Code=N'C2-SKU';
            SELECT @main=Id FROM dbo.StockLocation WHERE TenantId=@Tenant AND Code=N'C2-MAIN';

            INSERT dbo.StockLocation(Id,TenantId,Code,Kind)
            VALUES(@quarantine,@Tenant,N'F03-QA',N'QUARANTINE');
            INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,SnapshotCode,BaseQuantity,CostAmount)
            VALUES(@Tenant,@quarantine,@sku,N'F03-v1',40,400);
            INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,OnHandBaseQuantity,OnHandCostAmount)
            VALUES(@Tenant,@quarantine,@sku,40,400);

            INSERT dbo.F03Unit(TenantId,SkuId,UomCode,Factor,[Step])
            VALUES(@Tenant,@sku,N'EA',1,1),(@Tenant,@sku,N'BOX',24,1);
            DECLARE @early uniqueidentifier=NEWID(), @late uniqueidentifier=NEWID(),
                @expired uniqueidentifier=NEWID(), @held uniqueidentifier=NEWID();
            INSERT dbo.F03Lot(TenantId,Id,SkuId,Code,ExpiresOn)
            VALUES (@Tenant,@early,@sku,N'EARLY','20270630'),
                (@Tenant,@late,@sku,N'LATE','20271231'),
                (@Tenant,@expired,@sku,N'EXPIRED','20260630'),
                (@Tenant,@held,@sku,N'QA-HELD','20261231');
            INSERT dbo.F03LotOpening(TenantId,LotId,LocationId,Quantity)
            VALUES (@Tenant,@early,@main,30),(@Tenant,@late,@main,50),
                (@Tenant,@expired,@main,20),(@Tenant,@held,@quarantine,40);
            SELECT @quarantine;
            """, new { Tenant = tenant }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return quarantine;
    }

    internal static async Task<StockRow[]> CaptureStock(DemoRun run, CancellationToken ct)
    {
        var rows = new List<StockRow>();
        foreach (string tenant in DemoRun.Tenants)
        {
            await using var db = new SqlConnection(run.Connection(tenant));
            rows.AddRange(await db.QueryAsync<StockRow>(new CommandDefinition("""
                SELECT b.TenantId AS Tenant,l.Code AS Location,b.OnHandBaseQuantity AS Quantity,b.OnHandCostAmount AS Cost
                FROM dbo.PhysicalStockBalance b
                JOIN dbo.StockLocation l ON l.TenantId=b.TenantId AND l.Id=b.StockLocationId
                JOIN dbo.Sku k ON k.TenantId=b.TenantId AND k.Id=b.SkuId
                WHERE b.TenantId=@Tenant AND k.Code=N'C2-SKU'
                ORDER BY l.Code
                """, new { Tenant = tenant }, cancellationToken: ct)));
        }
        return rows.ToArray();
    }

    internal static async Task Verify(DemoRun run, string root, string source, string apiSha256, StockRow[] before,
        CancellationToken ct)
    {
        await run.Verify("tenant-a");
        await run.Verify("tenant-b");
        await using var a = new SqlConnection(run.Connection("tenant-a"));
        await using var b = new SqlConnection(run.Connection("tenant-b"));
        StockRow[] after = await CaptureStock(run, ct);
        StockRow[] expectedBefore = [
            new("tenant-a", "C2-MAIN", 100, 1000), new("tenant-a", "C2-TRUCK", 0, 0),
            new("tenant-a", "F03-QA", 40, 400),
            new("tenant-b", "C2-MAIN", 100, 1000), new("tenant-b", "C2-TRUCK", 0, 0),
            new("tenant-b", "F03-QA", 40, 400)
        ];
        StockRow[] expectedAfter = [
            new("tenant-a", "C2-MAIN", 52, 520), new("tenant-a", "C2-TRUCK", 13, 130),
            new("tenant-a", "F03-QA", 40, 400),
            new("tenant-b", "C2-MAIN", 100, 1000), new("tenant-b", "C2-TRUCK", 0, 0),
            new("tenant-b", "F03-QA", 40, 400)
        ];
        OpeningRow[] opening = [.. await ReadOpening(a, ct), .. await ReadOpening(b, ct)];
        OpeningRow[] expectedOpening = DemoRun.Tenants.SelectMany(tenant => new OpeningRow[]
        {
            new(tenant, "C2-MAIN", "EARLY", new DateTime(2027, 6, 30), 30),
            new(tenant, "C2-MAIN", "EXPIRED", new DateTime(2026, 6, 30), 20),
            new(tenant, "C2-MAIN", "LATE", new DateTime(2027, 12, 31), 50),
            new(tenant, "F03-QA", "QA-HELD", new DateTime(2026, 12, 31), 40)
        }).ToArray();

        var trail = (await a.QueryAsync<LotRow>(new CommandDefinition("""
            SELECT N'HOLD' AS Stage,lot.Code AS Lot,h.Quantity,r.BusinessSourceLineId AS SourceId,
                ar.AOrderLineId AS PredecessorId,
                CAST(IIF(r.RightKind='A' AND r.SkuId=lot.SkuId AND ar.AOrderLineId IS NOT NULL
                    AND o.LotId IS NOT NULL AND loc.Kind=N'WAREHOUSE',1,0) AS bit) AS Linked
            FROM dbo.F03LotHold h
            JOIN dbo.F03Lot lot ON lot.TenantId=h.TenantId AND lot.Id=h.LotId
            JOIN dbo.StockRight r ON r.TenantId=h.TenantId AND r.Id=h.StockRightId
            JOIN dbo.StockLocation loc ON loc.TenantId=r.TenantId AND loc.Id=r.StockLocationId
            LEFT JOIN dbo.AReservation ar ON ar.TenantId=r.TenantId AND ar.StockRightId=r.Id
            LEFT JOIN dbo.F03LotOpening o ON o.TenantId=h.TenantId AND o.LotId=h.LotId AND o.LocationId=loc.Id
            UNION ALL
            SELECT N'LOAD',lot.Code,x.Quantity,m.LoadSourceLineId,m.StockRightId,
                CAST(IIF(m.RightKind='A' AND m.SkuId=lot.SkuId AND h.LotId IS NOT NULL
                    AND m.LoadedBaseQuantity>0 AND src.SourceKind=N'MANIFEST_LOAD',1,0) AS bit)
            FROM dbo.F03LotLoad x
            JOIN dbo.F03Lot lot ON lot.TenantId=x.TenantId AND lot.Id=x.LotId
            JOIN dbo.TripManifestLine m ON m.TenantId=x.TenantId AND m.Id=x.ManifestLineId
            LEFT JOIN dbo.F03LotHold h ON h.TenantId=x.TenantId AND h.StockRightId=m.StockRightId AND h.LotId=x.LotId
            LEFT JOIN dbo.BusinessSource src ON src.TenantId=m.TenantId AND src.Id=m.LoadBusinessSourceId
            UNION ALL
            SELECT N'ISSUE',lot.Code,i.Quantity,d.BusinessSourceLineId,d.TripManifestLineId,
                CAST(IIF(d.RightKind='A' AND d.SkuId=lot.SkuId AND x.LotId IS NOT NULL AND st.Id IS NOT NULL
                    AND lot.ExpiresOn>=d.BusinessDate AND d.BusinessSourceKind=N'A_DELIVERY',1,0) AS bit)
            FROM dbo.F03LotIssue i
            JOIN dbo.F03Lot lot ON lot.TenantId=i.TenantId AND lot.Id=i.LotId
            JOIN dbo.SaleDelivery d ON d.TenantId=i.TenantId AND d.Id=i.DeliveryId
            LEFT JOIN dbo.F03LotLoad x ON x.TenantId=i.TenantId AND x.ManifestLineId=d.TripManifestLineId AND x.LotId=i.LotId
            LEFT JOIN dbo.DeliveryStockIssue st ON st.TenantId=d.TenantId AND st.SaleDeliveryId=d.Id
                AND st.BusinessSourceLineId=d.BusinessSourceLineId
            ORDER BY Stage,Lot
            """, cancellationToken: ct))).ToArray();
        QuantityRow[] expectedTrail = [
            new("HOLD", "EARLY", 30), new("HOLD", "LATE", 18),
            new("ISSUE", "EARLY", 30), new("ISSUE", "LATE", 5),
            new("LOAD", "EARLY", 30), new("LOAD", "LATE", 18)
        ];

        var facts = (await a.QueryAsync<Fact>(new CommandDefinition("""
            SELECT N'Draft rows' AS Metric, COUNT(*) AS Value FROM dbo.C2OrderDraftLine
            UNION ALL
            SELECT N'BOX conversion rows',COUNT(*) FROM dbo.F03DraftQuantity q
            JOIN dbo.C2OrderDraftLine d ON d.TenantId=q.TenantId AND d.DraftId=q.DraftId AND d.LineNumber=q.LineNumber
            JOIN dbo.AOrderLine l ON l.TenantId=q.TenantId AND l.AOrderId=q.DraftId AND l.LineNumber=q.LineNumber
            JOIN dbo.AConfirmedLineSnapshot c ON c.TenantId=l.TenantId AND c.AOrderLineId=l.Id
            WHERE q.UomCode=N'BOX' AND q.EnteredQuantity=2 AND q.Factor=24 AND q.[Step]=1
                AND q.BaseStep=1 AND q.LotTracked=1 AND d.BaseUom=N'EA' AND d.BaseQuantity=48 AND l.BaseQuantity=48
                AND l.UomCode=N'BOX' AND l.UomToBaseFactor=24 AND c.ConfirmedBaseQuantity=48
            UNION ALL SELECT N'Invalid UoM command', COUNT(*) FROM dbo.BusinessCommand
                WHERE Status=N'REJECTED' AND ReasonCode=N'F03_UOM_INVALID'
            UNION ALL SELECT N'Quarantine command refused', COUNT(*) FROM dbo.BusinessCommand
                WHERE Status=N'REJECTED' AND ReasonCode=N'F03_LOT_STOCK_INSUFFICIENT'
            UNION ALL SELECT N'FEFO bypass command refused', COUNT(*) FROM dbo.BusinessCommand
                WHERE Status=N'REJECTED' AND ReasonCode=N'F03_FEFO_EARLIER_LOT'
            UNION ALL SELECT N'Forbidden lot allocations', COUNT(*) FROM dbo.F03LotHold h
                JOIN dbo.F03Lot l ON l.TenantId=h.TenantId AND l.Id=h.LotId WHERE l.Code IN(N'EXPIRED',N'QA-HELD')
            UNION ALL SELECT N'Forbidden lot loads', COUNT(*) FROM dbo.F03LotLoad x
                JOIN dbo.F03Lot l ON l.TenantId=x.TenantId AND l.Id=x.LotId WHERE l.Code IN(N'EXPIRED',N'QA-HELD')
            UNION ALL SELECT N'Forbidden lot issues', COUNT(*) FROM dbo.F03LotIssue x
                JOIN dbo.F03Lot l ON l.TenantId=x.TenantId AND l.Id=x.LotId WHERE l.Code IN(N'EXPIRED',N'QA-HELD')
            UNION ALL SELECT N'Right rows',COUNT(*) FROM dbo.StockRight
            UNION ALL SELECT N'Right granted',COALESCE(SUM(GrantedBaseQuantity),0) FROM dbo.StockRight
            UNION ALL SELECT N'Right consumed',COALESCE(SUM(ConsumedBaseQuantity),0) FROM dbo.StockRight
            UNION ALL SELECT N'Manifest load quantity',COALESCE(SUM(LoadedBaseQuantity),0) FROM dbo.TripManifestLine
            UNION ALL SELECT N'Physical load moves',COUNT(*) FROM dbo.PhysicalStockMovement
            UNION ALL SELECT N'Physical load quantity',COALESCE(SUM(BaseQuantity),0) FROM dbo.PhysicalStockMovement
            UNION ALL SELECT N'Total deliveries',COUNT(*) FROM dbo.SaleDelivery
            UNION ALL SELECT N'Delivery quantity',COALESCE(SUM(BaseQuantity),0) FROM dbo.SaleDelivery
            UNION ALL SELECT N'Delivery payable',COALESCE(SUM(PayableAmount),0) FROM dbo.SaleDelivery
            UNION ALL SELECT N'Delivery cost',COALESCE(SUM(CostAmount),0) FROM dbo.SaleDelivery
            UNION ALL SELECT N'Invoice eligible',COUNT(*) FROM dbo.SaleDelivery WHERE InvoiceEligible=1
            UNION ALL SELECT N'Delivery stock issues',COUNT(*) FROM dbo.DeliveryStockIssue
            UNION ALL SELECT N'Issue cost',COALESCE(SUM(CostAmount),0) FROM dbo.DeliveryStockIssue
            UNION ALL SELECT N'AR rows',COUNT(*) FROM dbo.ReceivableOpenItem
            UNION ALL SELECT N'AR outstanding',COALESCE(SUM(OutstandingAmount),0) FROM dbo.ReceivableOpenItem
            UNION ALL SELECT N'COGS posted',COALESCE(SUM(AmountDelta),0) FROM dbo.LedgerEffect WHERE EffectKey=N'COGS'
            """, cancellationToken: ct))).ToDictionary(f => f.Metric, f => f.Value, StringComparer.Ordinal);
        (string Case, string Metric, decimal Expected)[] checks = [
            ("F03-01", "Draft rows", 1), ("F03-01", "BOX conversion rows", 1),
            ("F03-01", "Invalid UoM command", 1),
            ("F03-02", "Quarantine command refused", 1), ("F03-02", "Forbidden lot allocations", 0),
            ("F03-02", "Forbidden lot loads", 0), ("F03-02", "Forbidden lot issues", 0),
            ("F03-02", "Right rows", 1), ("F03-02", "Right granted", 48),
            ("F03-03", "Right consumed", 35), ("F03-03", "Manifest load quantity", 48),
            ("F03-03", "Physical load moves", 2), ("F03-03", "FEFO bypass command refused", 1), ("F03-03", "Physical load quantity", 48),
            ("F03-03", "Total deliveries", 2), ("F03-03", "Delivery quantity", 35),
            ("F03-03", "Delivery payable", 525), ("F03-03", "Delivery cost", 350),
            ("F03-03", "Invoice eligible", 0), ("F03-03", "Delivery stock issues", 2),
            ("F03-03", "Issue cost", 350), ("F03-03", "AR rows", 2),
            ("F03-03", "AR outstanding", 525), ("F03-03", "COGS posted", 350)
        ];
        var numeric = checks.Select(check =>
        {
            decimal? actual = facts.TryGetValue(check.Metric, out decimal value) ? value : null;
            return new { check.Case, check.Metric, check.Expected, Actual = actual, Diff = actual - check.Expected,
                Status = actual == check.Expected ? "PASS" : "FAIL" };
        }).ToArray();
        int tenantBEffects = await b.QuerySingleAsync<int>(new CommandDefinition("""
            SELECT (SELECT COUNT(*) FROM dbo.BusinessCommand)+(SELECT COUNT(*) FROM dbo.C2OrderDraft)
                +(SELECT COUNT(*) FROM dbo.F03DraftQuantity)+(SELECT COUNT(*) FROM dbo.F03LotHold)
                +(SELECT COUNT(*) FROM dbo.F03LotLoad)+(SELECT COUNT(*) FROM dbo.F03LotIssue)
                +(SELECT COUNT(*) FROM dbo.StockRight)+(SELECT COUNT(*) FROM dbo.TripManifest)
                +(SELECT COUNT(*) FROM dbo.SaleDelivery)+(SELECT COUNT(*) FROM dbo.ReceivableOpenItem)
                +(SELECT COUNT(*) FROM dbo.LedgerEffect)
            """, cancellationToken: ct));
        var actualTrail = trail.Select(row => new QuantityRow(row.Stage, row.Lot, row.Quantity)).ToArray();
        bool stockMatches = before.SequenceEqual(expectedBefore) && after.SequenceEqual(expectedAfter);
        bool trailMatches = actualTrail.SequenceEqual(expectedTrail)
            && trail.All(row => row.Linked && row.SourceId != Guid.Empty && row.PredecessorId is not null);
        bool matches = stockMatches && trailMatches && opening.SequenceEqual(expectedOpening)
            && numeric.All(row => row.Status == "PASS") && tenantBEffects == 0;

        string outDir = Path.Combine(root, "TestResults", "dev-f03", run.Id);
        Directory.CreateDirectory(outDir);
        string resultPath = Path.Combine(outDir, "sql-result.json");
        static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new
        {
            runId = run.Id, source, apiSha256, environment = "Synthetic Windows LocalDB",
            instance = DemoRun.Instance, acceptance = "AC-05", fixtureVersion = "v1", observedAtUtc = DateTime.UtcNow,
            browserFixtureSha256 = Hash(Path.Combine(root, "web", "e2e-dev-f03", "fefo.spec.ts")),
            browserReportSha256 = Hash(Path.Combine(root, "web", "test-results-app02c5", run.Id, "junit.xml")),
            numeric, expectedTrail, trail, trailMatches, expectedOpening, opening,
            expectedBefore, before, expectedAfter, after, stockMatches,
            expectedTenantBEffects = 0, tenantBEffects, matches
        }, new JsonSerializerOptions { WriteIndented = true }), ct);
        Console.WriteLine($"DEV-F03 SQL comparison: {resultPath}");
        if (!matches) throw new InvalidOperationException("DEV-F03 SQL rows differ from the fixture expectations.");
    }

    private static Task<IEnumerable<OpeningRow>> ReadOpening(SqlConnection db, CancellationToken ct) =>
        db.QueryAsync<OpeningRow>(new CommandDefinition("""
            SELECT o.TenantId AS Tenant,l.Code AS Location,lot.Code AS Lot,lot.ExpiresOn,o.Quantity
            FROM dbo.F03LotOpening o
            JOIN dbo.F03Lot lot ON lot.TenantId=o.TenantId AND lot.Id=o.LotId
            JOIN dbo.StockLocation l ON l.TenantId=o.TenantId AND l.Id=o.LocationId
            ORDER BY l.Code,lot.Code
            """, cancellationToken: ct));
}
