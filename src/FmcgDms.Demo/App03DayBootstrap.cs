using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class App03DayBootstrap
{
    internal const string Version = App03A1Profile.DayBootstrapVersion;
    internal const string Actor = "c1-dev-sales";
    internal static readonly DateOnly FirstDay = new(2026, 11, 23);
    internal static readonly DateOnly LastDay = new(2026, 11, 30);

    internal static DateTime Clock(DateOnly date) => new(date, new TimeOnly(2, 0), DateTimeKind.Utc);
    private static DateTime IctMidnightUtc(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-7);

    internal static async Task Seed(DemoRun run, string tenant, CancellationToken ct)
    {
        await run.Verify(tenant, app03Session: true);
        await using var db = new SqlConnection(run.Connection(tenant));
        await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        // This marker and all the openings commit together. An existing marker never causes reseeding.
        await db.ExecuteAsync(new CommandDefinition("""
            IF OBJECT_ID(N'dbo.LocalApp03Bootstrap',N'U') IS NULL
                CREATE TABLE dbo.LocalApp03Bootstrap(Slot int PRIMARY KEY CHECK (Slot=1), Version varchar(40) NOT NULL);
            """, transaction: tx, cancellationToken: ct));
        string? installed = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT Version FROM dbo.LocalApp03Bootstrap WITH (UPDLOCK,HOLDLOCK) WHERE Slot=1",
            transaction: tx, cancellationToken: ct));
        if (installed is not null && installed != Version)
            throw new InvalidOperationException("Unsupported APP-03 bootstrap version.");
        if (installed == Version)
        {
            await tx.CommitAsync(ct);
            return;
        }

        Guid customer = Guid.NewGuid(), outlet = Guid.NewGuid(), registered = Guid.NewGuid();
        var address = new C1Address("VN", "HCM", "Khu DEV", "23 Đường DEV", "Điểm nhận DEV");
        var register = new C1RegisteredInput(Guid.NewGuid(), customer, null, null, outlet, address);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("REGISTERED_SAVE\n" + JsonSerializer.Serialize(register, json)));

        // The C.1 master revision requires its receipt. No ship-to selection or sales source is seeded.
        await db.ExecuteAsync(new CommandDefinition("""
            DECLARE @sku uniqueidentifier=NEWID(), @main uniqueidentifier=NEWID(),
                @truckA uniqueidentifier=NEWID(), @truckB uniqueidentifier=NEWID(),
                @quarantine uniqueidentifier=NEWID(), @lot uniqueidentifier=NEWID();

            INSERT dbo.Customer(Id,TenantId,Code,Name) VALUES(@customer,@tenant,N'C2-FIXTURE',N'Khách hàng APP-03 DEV');
            INSERT dbo.C1Outlet(Id,TenantId,CustomerId,Name) VALUES(@outlet,@tenant,@customer,N'Điểm bán DEV');
            INSERT dbo.C1Command(TenantId,ActorId,CommandId,Kind,HashVersion,RequestHash,HttpStatus,Code,ResultId,Revision,CompletedAtUtc)
            VALUES(@tenant,N'app03-bootstrap',@CommandId,'REGISTERED_SAVE',1,@hash,200,'APPLIED',@registered,1,@Clock);
            INSERT dbo.C1RegisteredShipTo(Id,TenantId,CustomerId) VALUES(@registered,@tenant,@customer);
            INSERT dbo.C1RegisteredShipToRevision
                (TenantId,CustomerId,RegisteredId,Revision,OutletId,Payload,ActorId,CommandId,RecordedAtUtc)
            VALUES(@tenant,@customer,@registered,1,@outlet,@Payload,N'app03-bootstrap',@CommandId,@Clock);

            INSERT dbo.App02ActorRoleGrant(TenantId,ActorId,RoleCode,IsActive,ChangedBy)
            VALUES(@tenant,@Actor,N'sales',1,N'app03-bootstrap'),
                  (@tenant,@Actor,N'warehouse',1,N'app03-bootstrap'),
                  (@tenant,@Receiver,N'warehouse',1,N'app03-bootstrap'),
                  (@tenant,@Finance,N'finance',1,N'app03-bootstrap'),
                  (@tenant,@Qa,N'case_checker',1,N'app03-bootstrap');
            INSERT dbo.F01CustomerScope(TenantId,CustomerId,ActorId) VALUES(@tenant,@customer,@Actor);

            INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom) VALUES(@sku,@tenant,N'R-05',N'Hàng tổng hợp DEV',N'EA');
            INSERT dbo.StockLocation(Id,TenantId,Code,Kind)
            VALUES(@main,@tenant,N'C2-MAIN',N'WAREHOUSE'),(@truckA,@tenant,N'DEV-TRUCK-A',N'VEHICLE'),
                  (@truckB,@tenant,N'DEV-TRUCK-B',N'VEHICLE'),(@quarantine,@tenant,N'F05-QA',N'QUARANTINE');
            INSERT dbo.F05QaScope(TenantId,ActorId,WarehouseId) VALUES(@tenant,@Qa,@main);
            INSERT dbo.StockPool(TenantId,StockLocationId,SkuId,OnHandBaseQuantity) VALUES(@tenant,@main,@sku,6);
            INSERT dbo.PhysicalStockOpening
                (TenantId,StockLocationId,SkuId,SnapshotCode,BaseQuantity,CostAmount,CapturedAtUtc)
            VALUES(@tenant,@main,@sku,@Version,6,20001,@Clock),(@tenant,@truckA,@sku,@Version,0,0,@Clock),
                  (@tenant,@truckB,@sku,@Version,0,0,@Clock),(@tenant,@quarantine,@sku,@Version,0,0,@Clock);
            INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,OnHandBaseQuantity,OnHandCostAmount)
            SELECT TenantId,StockLocationId,SkuId,BaseQuantity,CostAmount
            FROM dbo.PhysicalStockOpening WHERE TenantId=@tenant AND SkuId=@sku;
            INSERT dbo.F03Unit(TenantId,SkuId,UomCode,Factor,[Step]) VALUES(@tenant,@sku,N'EA',1,1);
            INSERT dbo.F03Lot(TenantId,Id,SkuId,Code,ExpiresOn) VALUES(@tenant,@lot,@sku,N'RET-ORIGIN',@LotExpiry);
            INSERT dbo.F03LotOpening(TenantId,LotId,LocationId,Quantity) VALUES(@tenant,@lot,@main,6);

            DECLARE @plans TABLE(Version int,PlanId uniqueidentifier,QuoteId uniqueidentifier,
                Starts datetime2,Ends datetime2,Price int,Terms int);
            INSERT @plans VALUES(1,NEWID(),NEWID(),@Starts,@Cutover,10000,7),
                                (2,NEWID(),NEWID(),@Cutover,@Ends,12000,10);
            INSERT dbo.C2CommercialPolicy(TenantId,Id,CustomerId,SkuId,Version,Decision,EffectiveFromUtc,EffectiveUntilUtc,
                Maker,Checker,ApprovedAtUtc,EvidenceRef,UnitPrice,DiscountPerUnit,TaxRate,TermsDays,CreditCap,SellerTimeZone)
            SELECT @tenant,QuoteId,@customer,@sku,Version,'APPROVED',Starts,Ends,N'app03-maker',N'app03-checker',
                @Starts,@Version,Price,0,0,Terms,100000,'Asia/Ho_Chi_Minh' FROM @plans;
            INSERT dbo.F04PricePlan(TenantId,Id,CustomerId,Version,TaxMode,OrderDiscount,
                EffectiveFromUtc,EffectiveUntilUtc,Maker,Checker,ApprovedAtUtc,EvidenceRef)
            SELECT @tenant,PlanId,@customer,Version,'EXCLUSIVE',0,Starts,Ends,N'app03-maker',N'app03-checker',
                @Starts,@Version FROM @plans;
            INSERT dbo.F04PlanQuote(TenantId,PlanId,PolicyId) SELECT @tenant,PlanId,QuoteId FROM @plans;
            INSERT dbo.LocalApp03Bootstrap(Slot,Version) VALUES(1,@Version);
            """, new
        {
            tenant, customer, outlet, registered, register.CommandId, hash, Payload = JsonSerializer.Serialize(address, json),
            Actor, DevF05Evidence.Receiver, DevF05Evidence.Finance, DevF05Evidence.Qa, Version,
            Clock = Clock(FirstDay), LotExpiry = LastDay.ToDateTime(TimeOnly.MinValue),
            Starts = IctMidnightUtc(FirstDay), Cutover = IctMidnightUtc(FirstDay.AddDays(1)),
            Ends = IctMidnightUtc(LastDay.AddDays(1))
        }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    internal static async Task PrepareDay(DemoRun run, string tenant, DateOnly date, CancellationToken ct)
    {
        await run.Verify(tenant, app03Session: true);
        await using var db = new SqlConnection(run.Connection(tenant));
        await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var args = new { tenant, Version, Actor, Day = date.ToDateTime(TimeOnly.MinValue) };
        await db.ExecuteAsync(new CommandDefinition("""
            IF NOT EXISTS (SELECT 1 FROM dbo.LocalApp03Bootstrap WHERE Slot=1 AND Version=@Version)
                THROW 51700, 'APP-03 bootstrap version is missing or unsupported.', 1;
            """, args, tx, cancellationToken: ct));
        string prefix = "DEV-" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        await PrepareTrip("A");
        if (date == FirstDay) await PrepareTrip("B");
        await tx.CommitAsync(ct);

        Task PrepareTrip(string slot) => db.ExecuteAsync(new CommandDefinition("""
            DECLARE @id uniqueidentifier;
            SELECT @id=Id FROM dbo.Trip WITH (UPDLOCK,HOLDLOCK) WHERE TenantId=@tenant AND Code=@Code;
            IF @id IS NOT NULL
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM dbo.Trip t JOIN dbo.StockLocation l
                    ON l.TenantId=t.TenantId AND l.Id=t.VehicleLocationId
                    WHERE t.Id=@id AND t.BusinessDate=@Day AND l.Code=@Truck)
                    THROW 51701, 'Existing APP-03 trip does not match its planned date and vehicle.', 1;
                RETURN;
            END;
            DECLARE @vehicle uniqueidentifier =
                (SELECT Id FROM dbo.StockLocation WHERE TenantId=@tenant AND Code=@Truck AND Kind='VEHICLE' AND IsActive=1);
            IF @vehicle IS NULL THROW 51702, 'APP-03 trip vehicle is unavailable.', 1;
            SET @id=NEWID();
            INSERT dbo.Trip(Id,TenantId,Code,BusinessDate,VehicleLocationId) VALUES(@id,@tenant,@Code,@Day,@vehicle);
            INSERT dbo.C3TripWriter(TenantId,TripId,ActorId) VALUES(@tenant,@id,@Actor);
            INSERT dbo.C4DeliveryWriter(TenantId,TripId,ActorId) VALUES(@tenant,@id,@Actor);
            """, new { tenant, args.Day, Actor, Code = prefix + "-" + slot, Truck = "DEV-TRUCK-" + slot },
            tx, cancellationToken: ct));
    }
}
