using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class App03FieldBootstrap
{
    private const string Actor = "app03-field-bootstrap";

    internal static async Task Prepare(DemoRun run, string tenant, DateOnly day, CancellationToken ct)
    {
        await run.Verify(tenant, app03Session: true);
        await using var db = new SqlConnection(run.Connection(tenant));
        await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var customer = await db.QuerySingleAsync<Guid>(new CommandDefinition(
            "SELECT Id FROM dbo.Customer WHERE TenantId=@tenant AND Code=N'C2-FIXTURE'", new { tenant }, tx, cancellationToken: ct));
        var address = new C1Address("VN", "HCM", "Khu DEV", "25 Đường DEV", "Điểm nhận bán B DEV");
        var selection = new C1SelectionInput(Guid.NewGuid(), customer, null, null, null, address);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string payload = JsonSerializer.Serialize(address, json);
        var commandHash = SHA256.HashData(Encoding.UTF8.GetBytes("SHIP_TO_SELECT\n" + JsonSerializer.Serialize(selection, json)));
        var payloadHash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        await db.ExecuteAsync(new CommandDefinition("""
            DECLARE @sku uniqueidentifier, @shipTo uniqueidentifier;
            SELECT @sku=Id FROM dbo.Sku WITH (UPDLOCK,HOLDLOCK) WHERE TenantId=@tenant AND Code=N'Z-FIELD-EA';
            IF @sku IS NULL
            BEGIN
                SET @sku=NEWID();
                SET @shipTo=NEWID();
                DECLARE @selection uniqueidentifier=NEWID();
                INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom)
                VALUES(@sku,@tenant,N'Z-FIELD-EA',N'Hàng EA không quản lý lô',N'EA');
                INSERT dbo.StockPool(TenantId,StockLocationId,SkuId,OnHandBaseQuantity)
                SELECT @tenant,Id,@sku,10 FROM dbo.StockLocation WHERE TenantId=@tenant AND Code=N'C2-MAIN';
                INSERT dbo.PhysicalStockOpening
                    (TenantId,StockLocationId,SkuId,SnapshotCode,BaseQuantity,CostAmount,CapturedAtUtc)
                SELECT @tenant,Id,@sku,@Profile,CASE WHEN Kind='WAREHOUSE' THEN 10 ELSE 0 END,
                    CASE WHEN Kind='WAREHOUSE' THEN 40000 ELSE 0 END,@Opening
                FROM dbo.StockLocation WHERE TenantId=@tenant AND Code IN ('C2-MAIN','DEV-TRUCK-A','DEV-TRUCK-B');
                INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,OnHandBaseQuantity,OnHandCostAmount)
                SELECT TenantId,StockLocationId,SkuId,BaseQuantity,CostAmount FROM dbo.PhysicalStockOpening
                WHERE TenantId=@tenant AND SkuId=@sku;

                INSERT dbo.C1Command(TenantId,ActorId,CommandId,Kind,HashVersion,RequestHash,HttpStatus,Code,ResultId,CompletedAtUtc)
                VALUES(@tenant,@Actor,@CommandId,'SHIP_TO_SELECT',1,@commandHash,200,'APPLIED',@selection,@Opening);
                INSERT dbo.C1ShipToSelection(Id,TenantId,CustomerId,Origin,ValidatorVersion,Payload,PayloadHash,ActorId,CommandId)
                VALUES(@selection,@tenant,@customer,'DIRECT',1,@payload,@payloadHash,@Actor,@CommandId);
                INSERT dbo.ShipToSnapshot(Id,TenantId,CustomerId,DisplayName,Address,CapturedAtUtc)
                VALUES(@shipTo,@tenant,@customer,@Recipient,@Street,@Opening);
                INSERT dbo.D2ShipToBinding(TenantId,CustomerId,ShipToSnapshotId,SelectionId)
                VALUES(@tenant,@customer,@shipTo,@selection);

                INSERT dbo.C2CommercialPolicy(TenantId,Id,CustomerId,SkuId,Version,Decision,EffectiveFromUtc,EffectiveUntilUtc,
                    Maker,Checker,ApprovedAtUtc,EvidenceRef,UnitPrice,DiscountPerUnit,TaxRate,TermsDays,CreditCap,SellerTimeZone)
                SELECT @tenant,NEWID(),@customer,@sku,q.Version,q.Decision,q.EffectiveFromUtc,q.EffectiveUntilUtc,
                    q.Maker,q.Checker,q.ApprovedAtUtc,@Profile,q.UnitPrice,0,0,q.TermsDays,q.CreditCap,q.SellerTimeZone
                FROM dbo.C2CommercialPolicy q JOIN dbo.Sku k ON k.TenantId=q.TenantId AND k.Id=q.SkuId
                WHERE q.TenantId=@tenant AND q.CustomerId=@customer AND k.Code=N'R-05' AND q.EvidenceRef=@DayBootstrap;
                INSERT dbo.F04PlanQuote(TenantId,PlanId,PolicyId)
                SELECT @tenant,p.Id,q.Id FROM dbo.F04PricePlan p JOIN dbo.C2CommercialPolicy q
                    ON q.TenantId=p.TenantId AND q.CustomerId=p.CustomerId AND q.Version=p.Version
                WHERE p.TenantId=@tenant AND p.CustomerId=@customer AND q.SkuId=@sku AND p.EvidenceRef=@DayBootstrap;
            END;
            ELSE
                SELECT @shipTo=b.ShipToSnapshotId FROM dbo.D2ShipToBinding b
                JOIN dbo.C1ShipToSelection s ON s.TenantId=b.TenantId AND s.Id=b.SelectionId
                WHERE b.TenantId=@tenant AND b.CustomerId=@customer AND s.ActorId=@Actor;

            IF @shipTo IS NULL THROW 51705, 'APP-03 field bootstrap is incomplete.', 1;
            INSERT dbo.VerifiedCashEvidence(TenantId,EvidenceRef,TripId,CustomerId,ShipToSnapshotId,
                PaymentMethod,VerificationStatus,VerifiedAmount,VerifiedBy,VerifiedAtUtc)
            SELECT @tenant,r.Ref,t.Id,@customer,@shipTo,'CASH','VERIFIED',n.Quantity*q.UnitPrice,
                N'app03-cash-reviewer',DATEADD(minute,-1,@Now)
            FROM dbo.Trip t
            CROSS JOIN (VALUES (1),(2),(3)) n(Quantity)
            CROSS APPLY (SELECT TOP(1) UnitPrice FROM dbo.C2CommercialPolicy
                WHERE TenantId=@tenant AND CustomerId=@customer AND SkuId=@sku AND EvidenceRef=@Profile
                    AND EffectiveFromUtc<=@Now AND EffectiveUntilUtc>@Now ORDER BY Version DESC) q
            CROSS APPLY (SELECT CONCAT(N'APP03-CASH-',t.Code,N'-',n.Quantity) AS Ref) r
            WHERE t.TenantId=@tenant AND t.BusinessDate=@Day
                AND NOT EXISTS (SELECT 1 FROM dbo.VerifiedCashEvidence e WHERE e.TenantId=@tenant AND e.EvidenceRef=r.Ref);
            """, new
        {
            tenant, customer, Actor, selection.CommandId, commandHash, payload, payloadHash, address.Recipient,
            Street = string.Join(", ", address.Line, address.Locality, address.Region, address.Country),
            Profile = App03A1Profile.FieldName, DayBootstrap = App03DayBootstrap.Version,
            Opening = App03DayBootstrap.Clock(App03DayBootstrap.FirstDay), Now = App03DayBootstrap.Clock(day),
            Day = day.ToDateTime(TimeOnly.MinValue)
        }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }
}
