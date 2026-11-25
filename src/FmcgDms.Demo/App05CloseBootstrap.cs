using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class App05CloseBootstrap
{
    internal const string Ops = "app05-tenant-ops";
    internal const string Assignee = "app05-assignee";
    internal const string Maker = "app05-credit-maker";
    internal const string Checker = "app05-credit-checker";
    internal const string SkuCode = "DAY-A-EA";
    internal const string CustomerCode = "DAY-A-DEV";
    internal const string Version = App03A1Profile.CloseReadinessBootstrapVersion;
    private const string BootstrapActor = "app05-close-bootstrap";

    internal static async Task Prepare(DemoRun run, string tenant, CancellationToken ct, bool splitSuccessorLots = false)
    {
        await run.Verify(tenant, app03Session: true);
        await using var db = new SqlConnection(run.Connection(tenant));
        await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        await db.ExecuteAsync(new CommandDefinition("""
            IF OBJECT_ID(N'dbo.LocalApp05Bootstrap',N'U') IS NULL
                CREATE TABLE dbo.LocalApp05Bootstrap(Slot int PRIMARY KEY CHECK(Slot=1), Version varchar(40) NOT NULL);
            IF OBJECT_ID(N'dbo.LocalApp05TripBootstrap',N'U') IS NULL
                CREATE TABLE dbo.LocalApp05TripBootstrap(TripId uniqueidentifier PRIMARY KEY REFERENCES dbo.Trip(Id));
            """, transaction: tx, cancellationToken: ct));

        string? installed = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT Version FROM dbo.LocalApp05Bootstrap WITH (UPDLOCK,HOLDLOCK) WHERE Slot=1",
            transaction: tx, cancellationToken: ct));
        if (installed is not null && installed != Version)
            throw new InvalidOperationException("Unsupported APP-05.1 fixture version.");
        if (installed is null)
            await Seed(db, tx, tenant, splitSuccessorLots, ct);

        // Track prepared trips separately so Resume and NextDay preserve grant revocations,
        // including those on trips that already existed before the profile upgrade.
        await db.ExecuteAsync(new CommandDefinition("""
            DECLARE @trips TABLE(TripId uniqueidentifier PRIMARY KEY);
            INSERT @trips SELECT t.Id FROM dbo.Trip t
            WHERE t.TenantId=@tenant AND t.BusinessDate IS NOT NULL
                AND NOT EXISTS(SELECT 1 FROM dbo.LocalApp05TripBootstrap p WITH (UPDLOCK,HOLDLOCK) WHERE p.TripId=t.Id);
            INSERT dbo.App05TripOperator(TenantId,TripId,ActorId)
            SELECT @tenant,t.TripId,a.ActorId FROM @trips t CROSS JOIN (VALUES(@Ops),(@Assignee)) a(ActorId);
            INSERT dbo.LocalApp05TripBootstrap(TripId) SELECT TripId FROM @trips;
            """, new { tenant, Ops, Assignee }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    private static Task Seed(SqlConnection db, SqlTransaction tx, string tenant, bool splitSuccessorLots, CancellationToken ct)
    {
        Guid customer = Guid.NewGuid(), outlet = Guid.NewGuid(), registered = Guid.NewGuid();
        var address = new C1Address("VN", "HCM", "Khu DEV", "05 Đường DEV", "Điểm nhận cuối ca DEV");
        var register = new C1RegisteredInput(Guid.NewGuid(), customer, null, null, outlet, address);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("REGISTERED_SAVE\n" + JsonSerializer.Serialize(register, json)));

        return db.ExecuteAsync(new CommandDefinition("""
            IF (SELECT COUNT(*) FROM dbo.StockLocation WHERE TenantId=@tenant
                AND Code IN ('C2-MAIN','DEV-TRUCK-A','DEV-TRUCK-B','F05-QA') AND IsActive=1)<>4
                THROW 51720,'APP-05.1 requires its four active APP-03 stock locations.',1;

            DECLARE @sku uniqueidentifier=NEWID(), @lot uniqueidentifier=NEWID(),
                @main uniqueidentifier=(SELECT Id FROM dbo.StockLocation WHERE TenantId=@tenant AND Code='C2-MAIN'),
                @plan uniqueidentifier=NEWID(), @policy uniqueidentifier=NEWID();

            INSERT dbo.Customer(Id,TenantId,Code,Name) VALUES(@customer,@tenant,@CustomerCode,N'Khách hàng đối soát DEV');
            INSERT dbo.C1Outlet(Id,TenantId,CustomerId,Name) VALUES(@outlet,@tenant,@customer,N'Điểm bán đối soát DEV');
            INSERT dbo.C1Command(TenantId,ActorId,CommandId,Kind,HashVersion,RequestHash,HttpStatus,Code,ResultId,Revision,CompletedAtUtc)
            VALUES(@tenant,@BootstrapActor,@CommandId,'REGISTERED_SAVE',1,@hash,200,'APPLIED',@registered,1,@Opening);
            INSERT dbo.C1RegisteredShipTo(Id,TenantId,CustomerId) VALUES(@registered,@tenant,@customer);
            INSERT dbo.C1RegisteredShipToRevision
                (TenantId,CustomerId,RegisteredId,Revision,OutletId,Payload,ActorId,CommandId,RecordedAtUtc)
            VALUES(@tenant,@customer,@registered,1,@outlet,@Payload,@BootstrapActor,@CommandId,@Opening);
            INSERT dbo.F01CustomerScope(TenantId,CustomerId,ActorId) VALUES(@tenant,@customer,@Sales);

            INSERT dbo.App02ActorRoleGrant(TenantId,ActorId,RoleCode,IsActive,ChangedBy)
            VALUES(@tenant,@Ops,'tenant_ops',1,@BootstrapActor),(@tenant,@Assignee,'trip_assignee',1,@BootstrapActor),
                (@tenant,@Maker,'credit_maker',1,@BootstrapActor),(@tenant,@Checker,'credit_checker',1,@BootstrapActor);

            INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom) VALUES(@sku,@tenant,@SkuCode,N'Hàng đối soát cuối ca DEV',N'EA');
            INSERT dbo.StockPool(TenantId,StockLocationId,SkuId,OnHandBaseQuantity) VALUES(@tenant,@main,@sku,60);
            INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,SnapshotCode,BaseQuantity,CostAmount,CapturedAtUtc)
            SELECT @tenant,Id,@sku,@Version,IIF(Id=@main,60,0),IIF(Id=@main,180000,0),@Opening
            FROM dbo.StockLocation WHERE TenantId=@tenant AND Code IN ('C2-MAIN','DEV-TRUCK-A','DEV-TRUCK-B','F05-QA');
            INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,OnHandBaseQuantity,OnHandCostAmount)
            SELECT TenantId,StockLocationId,SkuId,BaseQuantity,CostAmount
            FROM dbo.PhysicalStockOpening WHERE TenantId=@tenant AND SkuId=@sku AND SnapshotCode=@Version;
            INSERT dbo.F03Unit(TenantId,SkuId,UomCode,Factor,[Step]) VALUES(@tenant,@sku,'EA',1,1);
            INSERT dbo.F03Lot(TenantId,Id,SkuId,Code,ExpiresOn) VALUES(@tenant,@lot,@sku,'DAY-A-ORIGIN',IIF(@SplitSuccessorLots=1,DATEADD(day,3,@FirstDay),@Expiry));
            INSERT dbo.F03LotOpening(TenantId,LotId,LocationId,Quantity)
            VALUES(@tenant,@lot,@main,IIF(@SplitSuccessorLots=1,49,60));
            IF @SplitSuccessorLots=1
            BEGIN
                DECLARE @early uniqueidentifier=NEWID(),@late uniqueidentifier=NEWID(),@expired uniqueidentifier=NEWID();
                INSERT dbo.F03Lot(TenantId,Id,SkuId,Code,ExpiresOn)
                VALUES(@tenant,@early,@sku,'DAY-A-EARLY',DATEADD(day,5,@FirstDay)),
                    (@tenant,@late,@sku,'DAY-A-LATE',DATEADD(day,7,@FirstDay)),
                    (@tenant,@expired,@sku,'DAY-A-EXPIRED',DATEADD(day,-1,@FirstDay));
                INSERT dbo.F03LotOpening(TenantId,LotId,LocationId,Quantity)
                VALUES(@tenant,@early,@main,2),(@tenant,@late,@main,4),(@tenant,@expired,@main,5);
            END;

            INSERT dbo.C2CommercialPolicy(TenantId,Id,CustomerId,SkuId,Version,Decision,EffectiveFromUtc,EffectiveUntilUtc,
                Maker,Checker,ApprovedAtUtc,EvidenceRef,UnitPrice,DiscountPerUnit,TaxRate,TermsDays,CreditCap,SellerTimeZone)
            VALUES(@tenant,@policy,@customer,@sku,1,'APPROVED',@Starts,@Ends,@Maker,@Checker,
                @Starts,@Version,10000,0,0,7,500000,'Asia/Ho_Chi_Minh');
            INSERT dbo.F04PricePlan(TenantId,Id,CustomerId,Version,TaxMode,OrderDiscount,
                EffectiveFromUtc,EffectiveUntilUtc,Maker,Checker,ApprovedAtUtc,EvidenceRef)
            VALUES(@tenant,@plan,@customer,1,'EXCLUSIVE',0,@Starts,@Ends,@Maker,@Checker,@Starts,@Version);
            INSERT dbo.F04PlanQuote(TenantId,PlanId,PolicyId) VALUES(@tenant,@plan,@policy);
            INSERT dbo.LocalApp05Bootstrap(Slot,Version) VALUES(1,@Version);
            """, new
        {
            tenant, Version, SkuCode, CustomerCode, Ops, Assignee, Maker, Checker, BootstrapActor,
            customer, outlet, registered, register.CommandId, hash, Payload = JsonSerializer.Serialize(address, json),
            Sales = App03DayBootstrap.Actor,
            Opening = App03DayBootstrap.Clock(App03DayBootstrap.FirstDay),
            Starts = IctMidnight(App03DayBootstrap.FirstDay),
            Ends = IctMidnight(App03DayBootstrap.LastDay.AddDays(1)),
            Expiry = App03DayBootstrap.LastDay.AddDays(1).ToDateTime(TimeOnly.MinValue),
            FirstDay = App03DayBootstrap.FirstDay.ToDateTime(TimeOnly.MinValue), SplitSuccessorLots = splitSuccessorLots
        }, tx, cancellationToken: ct));
    }

    private static DateTime IctMidnight(DateOnly day) => day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-7);
}
