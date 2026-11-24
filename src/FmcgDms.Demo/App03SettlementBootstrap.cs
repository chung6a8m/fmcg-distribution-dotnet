using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class App03SettlementBootstrap
{
    internal const string Cashier = "app03-cashier";

    internal static async Task Prepare(DemoRun run, string tenant, CancellationToken ct)
    {
        await run.Verify(tenant, app03Session: true);
        await using var db = new SqlConnection(run.Connection(tenant));
        await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await db.ExecuteAsync(new CommandDefinition("""
            IF NOT EXISTS(SELECT 1 FROM dbo.App03SettlementDay WITH (UPDLOCK,HOLDLOCK))
            BEGIN
                DECLARE @purchase uniqueidentifier, @gift uniqueidentifier=NEWID(), @program uniqueidentifier=NEWID(),
                    @store uniqueidentifier=NEWID(), @quarantine uniqueidentifier=NEWID();
                SELECT @purchase=Id FROM dbo.Sku WHERE TenantId=@tenant AND Code=N'R-05';

                INSERT dbo.App02ActorRoleGrant(TenantId,ActorId,RoleCode,IsActive,ChangedBy)
                VALUES(@tenant,@Cashier,N'cashier',1,N'app03-settlement-bootstrap');

                INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom)
                VALUES(@gift,@tenant,N'APP03-GIFT',N'Quà DEV',N'EA');
                INSERT dbo.StockLocation(Id,TenantId,Code,Kind)
                VALUES(@store,@tenant,N'APP03-GIFT',N'WAREHOUSE'),(@quarantine,@tenant,N'APP03-GIFT-QA',N'QUARANTINE');
                INSERT dbo.F05QaScope(TenantId,ActorId,WarehouseId) VALUES(@tenant,@Warehouse,@store);
                INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,SnapshotCode,BaseQuantity,CostAmount,CapturedAtUtc)
                VALUES(@tenant,@store,@gift,N'APP-03.6-v1',10,1000,@Opening),
                      (@tenant,@quarantine,@gift,N'APP-03.6-v1',0,0,@Opening);
                INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,OnHandBaseQuantity,OnHandCostAmount)
                VALUES(@tenant,@store,@gift,10,1000),(@tenant,@quarantine,@gift,0,0);
                INSERT dbo.F07Program(TenantId,Id,PurchaseSkuId,GiftSkuId,GiftLocationId,QuarantineLocationId,
                    PurchaseThreshold,GiftsPerThreshold)
                VALUES(@tenant,@program,@purchase,@gift,@store,@quarantine,2,1);
                INSERT dbo.F07ProgramGrant(TenantId,ProgramId,ActorId,RoleCode)
                VALUES(@tenant,@program,@Actor,N'sales'),(@tenant,@program,@Finance,N'finance');
            END;

            INSERT dbo.D3CashierTrip(TenantId,TripId,ActorId)
            SELECT @tenant,t.Id,@Cashier FROM dbo.Trip t
            WHERE t.TenantId=@tenant AND t.BusinessDate IS NOT NULL
                AND NOT EXISTS(SELECT 1 FROM dbo.App03SettlementDay d WHERE d.BusinessDate=t.BusinessDate)
                AND NOT EXISTS(SELECT 1 FROM dbo.D3CashierTrip c WHERE c.TenantId=@tenant AND c.TripId=t.Id);

            INSERT dbo.App03SettlementDay(BusinessDate)
            SELECT DISTINCT t.BusinessDate FROM dbo.Trip t
            WHERE t.TenantId=@tenant AND t.BusinessDate IS NOT NULL
                AND NOT EXISTS(SELECT 1 FROM dbo.App03SettlementDay d WHERE d.BusinessDate=t.BusinessDate);
            """, new
        {
            tenant, Cashier, App03DayBootstrap.Actor, DevF05Evidence.Finance,
            Warehouse = tenant == "tenant-a" ? DevF05Evidence.Receiver : App03DayBootstrap.Actor,
            Opening = App03DayBootstrap.Clock(App03DayBootstrap.FirstDay)
        }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }
}
