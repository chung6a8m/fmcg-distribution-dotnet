using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class App03HandoffEvidence
{
    internal static async Task Prepare(DemoRun run, CancellationToken ct)
    {
        await run.Verify("tenant-a", app03Session: true);
        await using var db = new SqlConnection(run.Connection("tenant-a"));
        await db.ExecuteAsync(new CommandDefinition("""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DECLARE @tenant nvarchar(64)=N'tenant-a',
                @source uniqueidentifier=(SELECT Id FROM dbo.Customer WHERE TenantId=N'tenant-a' AND Code=N'C2-FIXTURE');
            INSERT dbo.F03Unit(TenantId,SkuId,UomCode,Factor,[Step])
            SELECT TenantId,Id,N'PACK',2,1 FROM dbo.Sku WHERE TenantId=@tenant AND Code=N'R-05';

            DECLARE @cases TABLE(Id uniqueidentifier,Code nvarchar(40));
            INSERT @cases VALUES(NEWID(),N'NO-PLAN'),(NEWID(),N'EXPIRED-PLAN'),(NEWID(),N'SELF-CHECK');
            INSERT dbo.Customer(TenantId,Id,Code,Name)
            SELECT @tenant,Id,Code,Code FROM @cases;
            INSERT dbo.C2CommercialPolicy(TenantId,Id,CustomerId,SkuId,Version,Decision,EffectiveFromUtc,EffectiveUntilUtc,
                Maker,Checker,ApprovedAtUtc,EvidenceRef,UnitPrice,DiscountPerUnit,TaxRate,TermsDays,CreditCap,SellerTimeZone)
            SELECT @tenant,NEWID(),c.Id,p.SkuId,1,p.Decision,p.EffectiveFromUtc,p.EffectiveUntilUtc,
                p.Maker,p.Checker,p.ApprovedAtUtc,N'APP-03.4-verify',p.UnitPrice,0,p.TaxRate,p.TermsDays,p.CreditCap,p.SellerTimeZone
            FROM @cases c CROSS JOIN dbo.C2CommercialPolicy p WHERE p.TenantId=@tenant AND p.CustomerId=@source AND p.Version=1;
            INSERT dbo.F04PricePlan(TenantId,Id,CustomerId,Version,TaxMode,OrderDiscount,EffectiveFromUtc,EffectiveUntilUtc,
                Maker,Checker,ApprovedAtUtc,EvidenceRef)
            SELECT @tenant,NEWID(),c.Id,1,p.TaxMode,p.OrderDiscount,DATEADD(day,-1,p.EffectiveFromUtc),
                CASE WHEN c.Code=N'EXPIRED-PLAN' THEN p.EffectiveFromUtc ELSE p.EffectiveUntilUtc END,
                p.Maker,CASE WHEN c.Code=N'SELF-CHECK' THEN @Actor ELSE p.Checker END,
                DATEADD(day,-1,p.EffectiveFromUtc),N'APP-03.4-verify'
            FROM @cases c CROSS JOIN dbo.F04PricePlan p
            WHERE c.Code<>N'NO-PLAN' AND p.TenantId=@tenant AND p.CustomerId=@source AND p.Version=1;
            INSERT dbo.F04PlanQuote(TenantId,PlanId,PolicyId)
            SELECT @tenant,p.Id,q.Id FROM @cases c
            JOIN dbo.F04PricePlan p ON p.TenantId=@tenant AND p.CustomerId=c.Id
            JOIN dbo.C2CommercialPolicy q ON q.TenantId=p.TenantId AND q.CustomerId=c.Id;
            COMMIT;
            """, new { App03DayBootstrap.Actor }, cancellationToken: ct));
    }

    internal static async Task Review(DemoRun run, string directory, string source, CancellationToken ct)
    {
        var primary = await Read("tenant-a");
        var peer = await Read("tenant-b");
        if (primary != new Counts(5, 2, 3, 0, 6, 6, 6, 0, 0, 0, 80000, 60000, 2)
            || peer != new Counts(0, 0, 0, 0, 0, 0, 0, 6, 0, 0, 0, 0, 0))
            throw new InvalidOperationException($"Unexpected APP-03.4 SQL state: tenant-a={primary}, tenant-b={peer}.");
        await File.WriteAllTextAsync(Path.Combine(directory, "sql-review.json"),
            JsonSerializer.Serialize(new { source, runId = run.Id, primary, peer },
                new JsonSerializerOptions { WriteIndented = true }), ct);

        async Task<Counts> Read(string tenant)
        {
            await run.Verify(tenant, app03Session: true);
            await using var db = new SqlConnection(run.Connection(tenant));
            return await db.QuerySingleAsync<Counts>(new CommandDefinition("""
                SELECT
                    (SELECT COUNT(*) FROM dbo.C2OrderDraft WHERE TenantId=@Tenant) AS Drafts,
                    (SELECT COUNT(*) FROM dbo.AOrder WHERE TenantId=@Tenant) AS Orders,
                    (SELECT COUNT(*) FROM dbo.BusinessCommand WHERE TenantId=@Tenant AND ReasonCode=N'F04_PRICE_PLAN_REQUIRED') AS PriceRejections,
                    (SELECT COUNT(*) FROM dbo.BusinessSource s JOIN dbo.BusinessCommand c ON c.TenantId=s.TenantId
                        AND c.Id=s.BusinessCommandId WHERE s.TenantId=@Tenant AND c.Status=N'REJECTED') AS RejectedSources,
                    (SELECT COALESCE(SUM(Quantity),0) FROM dbo.F03LotHold WHERE TenantId=@Tenant) AS LotHeld,
                    (SELECT COALESCE(SUM(Quantity),0) FROM dbo.F03LotLoad WHERE TenantId=@Tenant) AS LotLoaded,
                    (SELECT COALESCE(SUM(Quantity),0) FROM dbo.F03LotIssue WHERE TenantId=@Tenant) AS LotIssued,
                    (SELECT SUM(OnHandBaseQuantity) FROM dbo.StockPool WHERE TenantId=@Tenant) AS MainOnHand,
                    (SELECT SUM(AllocatedBaseQuantity) FROM dbo.StockPool WHERE TenantId=@Tenant) AS MainAllocated,
                    (SELECT SUM(b.OnHandBaseQuantity) FROM dbo.PhysicalStockBalance b JOIN dbo.StockLocation l
                        ON l.TenantId=b.TenantId AND l.Id=b.StockLocationId WHERE b.TenantId=@Tenant AND l.Kind=N'VEHICLE') AS VehicleOnHand,
                    (SELECT COALESCE(SUM(GrossAmount),0) FROM dbo.F04PriceLine WHERE TenantId=@Tenant) AS ConfirmedVnd,
                    (SELECT COALESCE(SUM(GrossAmount),0) FROM dbo.F04DeliveryPricing WHERE TenantId=@Tenant) AS DeliveredVnd,
                    (SELECT COUNT(*) FROM dbo.F03DraftQuantity q JOIN dbo.AOrder o ON o.TenantId=q.TenantId AND o.Id=q.DraftId
                        WHERE q.TenantId=@Tenant AND q.UomCode=N'PACK' AND q.EnteredQuantity=2 AND q.Factor=2) AS PackSnapshots
                """, new { Tenant = tenant }, cancellationToken: ct));
        }
    }

    private sealed record Counts(int Drafts, int Orders, int PriceRejections, int RejectedSources,
        decimal LotHeld, decimal LotLoaded, decimal LotIssued, decimal MainOnHand, decimal MainAllocated,
        decimal VehicleOnHand, long ConfirmedVnd, long DeliveredVnd, int PackSnapshots);
}
