using System.Data;
using FmcgDms.Api.Persistence;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class App03FinanceBootstrap
{
    internal const string RefundChecker = "app03-refund-checker";
    internal const string Owner = "app03-owner";
    internal static string AccountingActor(string role) => "app03-d4-" + role;

    internal static async Task Prepare(DemoRun run, string tenant, DateOnly day, CancellationToken ct)
    {
        await run.Verify(tenant, app03Session: true);
        await using var db = new SqlConnection(run.Connection(tenant));
        await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await db.ExecuteAsync(new CommandDefinition("""
            IF NOT EXISTS (SELECT 1 FROM dbo.App02ActorRoleGrant WITH (UPDLOCK,HOLDLOCK)
                WHERE TenantId=@tenant AND ActorId=@Owner AND RoleCode='owner')
                INSERT dbo.App02ActorRoleGrant(TenantId,ActorId,RoleCode,IsActive,ChangedBy)
                VALUES(@tenant,@Owner,'owner',1,N'app03-owner-bootstrap');

            IF NOT EXISTS (SELECT 1 FROM dbo.F08Treasury WITH (UPDLOCK,HOLDLOCK) WHERE TenantId=@tenant)
            BEGIN
                INSERT dbo.App02ActorRoleGrant(TenantId,ActorId,RoleCode,IsActive,ChangedBy)
                VALUES(@tenant,@RefundChecker,'finance',1,N'app03-finance-bootstrap');
                INSERT dbo.F08Scope(TenantId,ActorId,RoleCode)
                VALUES(@tenant,@Finance,'finance'),(@tenant,@RefundChecker,'finance'),
                      (@tenant,@Cashier,'cashier'),(@tenant,@Qa,'case_checker');
                INSERT dbo.F08Treasury(TenantId,OpeningAmount,AvailableAmount) VALUES(@tenant,100000,100000);
                INSERT d4app.ActorRole(ActorId,RoleCode,IsActive)
                VALUES(@Accountant,'accountant',1),(@Reconciler,'reconciler',1),(@Maker,'maker',1),(@Checker,'checker',1);

                DECLARE @sku uniqueidentifier=NEWID(),@lot uniqueidentifier=NEWID();
                INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom)
                VALUES(@sku,@tenant,N'Z-REFUND-EA',N'Hàng DEV có lô, không kèm quà',N'EA');
                INSERT dbo.StockPool(TenantId,StockLocationId,SkuId,OnHandBaseQuantity)
                SELECT @tenant,Id,@sku,6 FROM dbo.StockLocation WHERE TenantId=@tenant AND Code='C2-MAIN';
                INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,SnapshotCode,BaseQuantity,CostAmount,CapturedAtUtc)
                SELECT @tenant,Id,@sku,@Profile,IIF(Code='C2-MAIN',6,0),IIF(Code='C2-MAIN',18000,0),@Opening
                FROM dbo.StockLocation WHERE TenantId=@tenant AND Code IN ('C2-MAIN','DEV-TRUCK-A','DEV-TRUCK-B','F05-QA');
                INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,OnHandBaseQuantity,OnHandCostAmount)
                SELECT TenantId,StockLocationId,SkuId,BaseQuantity,CostAmount
                FROM dbo.PhysicalStockOpening WHERE TenantId=@tenant AND SkuId=@sku;
                INSERT dbo.F03Unit(TenantId,SkuId,UomCode,Factor,[Step]) VALUES(@tenant,@sku,'EA',1,1);
                INSERT dbo.F03Lot(TenantId,Id,SkuId,Code,ExpiresOn) VALUES(@tenant,@lot,@sku,N'REFUND-ORIGIN',@Expiry);
                INSERT dbo.F03LotOpening(TenantId,LotId,LocationId,Quantity)
                SELECT @tenant,@lot,Id,6 FROM dbo.StockLocation WHERE TenantId=@tenant AND Code='C2-MAIN';
                INSERT dbo.C2CommercialPolicy(TenantId,Id,CustomerId,SkuId,Version,Decision,EffectiveFromUtc,EffectiveUntilUtc,
                    Maker,Checker,ApprovedAtUtc,EvidenceRef,UnitPrice,DiscountPerUnit,TaxRate,TermsDays,CreditCap,SellerTimeZone)
                SELECT q.TenantId,NEWID(),q.CustomerId,@sku,q.Version,q.Decision,q.EffectiveFromUtc,q.EffectiveUntilUtc,
                    q.Maker,q.Checker,q.ApprovedAtUtc,@Profile,q.UnitPrice,0,0,q.TermsDays,q.CreditCap,q.SellerTimeZone
                FROM dbo.C2CommercialPolicy q JOIN dbo.Sku k ON k.TenantId=q.TenantId AND k.Id=q.SkuId
                WHERE q.TenantId=@tenant AND k.Code='R-05' AND q.EvidenceRef=@DayBootstrap;
                INSERT dbo.F04PlanQuote(TenantId,PlanId,PolicyId)
                SELECT @tenant,p.Id,q.Id FROM dbo.F04PricePlan p JOIN dbo.C2CommercialPolicy q
                    ON q.TenantId=p.TenantId AND q.CustomerId=p.CustomerId AND q.Version=p.Version
                WHERE p.TenantId=@tenant AND p.EvidenceRef=@DayBootstrap AND q.SkuId=@sku;
            END;

            IF NOT EXISTS (SELECT 1 FROM dbo.F06Period WHERE TenantId=@tenant AND MonthStart=@November)
                INSERT dbo.F06Period(TenantId,MonthStart,State) VALUES(@tenant,@November,'OPEN');

            IF @Day=@Rollover
            BEGIN
                UPDATE dbo.F06Period SET State='LOCKED',CloseReference=N'APP-03.7 DEV rollover',ClosedAtUtc=@Now
                WHERE TenantId=@tenant AND MonthStart=@November AND State='OPEN';
                IF NOT EXISTS (SELECT 1 FROM dbo.F06Period WHERE TenantId=@tenant AND MonthStart=@Rollover)
                    INSERT dbo.F06Period(TenantId,MonthStart,State) VALUES(@tenant,@Rollover,'OPEN');
            END;

            INSERT d4app.TripProfile(TripId)
            SELECT t.Id FROM dbo.Trip t WHERE t.TenantId=@tenant AND t.BusinessDate IS NOT NULL
                AND NOT EXISTS(SELECT 1 FROM d4app.TripProfile p WHERE p.TripId=t.Id);
            """, new
        {
            tenant, RefundChecker, Owner, DevF05Evidence.Finance, DevF05Evidence.Qa, App03SettlementBootstrap.Cashier,
            Accountant = AccountingActor("accountant"), Reconciler = AccountingActor("reconciler"),
            Maker = AccountingActor("maker"), Checker = AccountingActor("checker"),
            Profile = App03A1Profile.FinanceName, DayBootstrap = App03DayBootstrap.Version,
            Day = day.ToDateTime(TimeOnly.MinValue), Now = App03DayBootstrap.Clock(day),
            November = new DateTime(2026, 11, 1), Rollover = App03DevSession.FinanceRollover.ToDateTime(TimeOnly.MinValue),
            Expiry = App03DevSession.FinanceRollover.ToDateTime(TimeOnly.MinValue),
            Opening = App03DayBootstrap.Clock(App03DayBootstrap.FirstDay)
        }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }
}
