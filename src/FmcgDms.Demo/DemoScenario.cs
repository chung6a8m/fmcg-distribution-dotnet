using System.Security.Claims;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Authentication;
using FmcgDms.Api.Persistence;
using FmcgDms.Api.Tenancy;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal sealed record DemoInputs(string CustomerCode, string CustomerName, string ShipToName, string Address,
    string SkuCode, string SkuName, string Uom, string WarehouseCode, string VehicleCode,
    string OrderCode, string TripCode, decimal Opening, decimal UnitCost)
{
    public static DemoInputs Sample { get; } = new("C01", "Cửa hàng mẫu", "Điểm giao mẫu", "Địa chỉ giả lập",
        "SKU01", "Hàng mẫu", "EA", "MAIN", "TRIP", "A01", "AC03", 100, 10);
}

internal sealed record DemoScene(Guid Sku, Guid Warehouse, Guid Vehicle, Guid Trip, Guid Customer,
    Guid ShipTo, Guid Order, Guid OrderLine, Guid Manifest, Guid AManifestLine, Guid BManifestLine, DemoInputs Inputs)
{
    public static DemoScene Create(DemoInputs inputs) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), inputs);
}

internal sealed partial class DemoScenario(DemoRun run, TenantConnections connections)
{
    public const string Producer = "local-ac03";
    public static readonly string[] Roles = ["warehouse", "sales", "cashier", "auditor"];

    public static ClaimsPrincipal Identity(string tenant, string role)
    {
        string actor = "demo-" + role;
        return new(new ClaimsIdentity([
            new(SyntheticAuth.TenantClaim, tenant), new("sub", actor), new("actor_id", actor),
            new("producer_id", Producer), new("synthetic_role", role)
        ], "LocalDemo"));
    }

    public Task<SqlConnection> Open(ClaimsPrincipal user) => connections.OpenSyntheticScratchAsync(user, default);

    public static async Task<DemoScene?> Scene(SqlConnection db)
    {
        var data = await db.QuerySingleOrDefaultAsync<string>("SELECT Data FROM dbo.LocalDemoScene WHERE Slot=1");
        return data is null ? null : JsonSerializer.Deserialize<DemoScene>(data);
    }

    public async Task<DemoScene> Prepare(ClaimsPrincipal user, DemoInputs input)
    {
        string tenant = SyntheticAuth.GetTenantId(user);
        await using var db = await Open(user);
        var s = await Scene(db);
        if (s is null)
        {
            s = DemoScene.Create(input);
            using var tx = db.BeginTransaction();
            // Synthetic master data, opening snapshot and policy witnesses adapted from APP-01E.2.
            await db.ExecuteAsync("""
            INSERT dbo.Customer(Id,TenantId,Code,Name)
                VALUES(@Customer,@Tenant,@CustomerCode,@CustomerName);
            INSERT dbo.ShipToSnapshot(Id,TenantId,CustomerId,DisplayName,Address)
                VALUES(@ShipTo,@Tenant,@Customer,@ShipToName,@Address);
            INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom)
                VALUES(@Sku,@Tenant,@SkuCode,@SkuName,@Uom);
            INSERT dbo.StockLocation(Id,TenantId,Code,Kind)
                VALUES(@Warehouse,@Tenant,@WarehouseCode,N'WAREHOUSE');
            INSERT dbo.StockLocation(Id,TenantId,Code,Kind)
                VALUES(@Vehicle,@Tenant,@VehicleCode,N'VEHICLE');
            INSERT dbo.Trip(Id,TenantId,Code,BusinessDate,VehicleLocationId)
                VALUES(@Trip,@Tenant,@TripCode,CONVERT(date,SYSUTCDATETIME()),@Vehicle);
            INSERT dbo.AOrder(Id,TenantId,OrderNumber,CustomerId,ShipToSnapshotId,
                TripId,BusinessDate)
                VALUES(@Order,@Tenant,@OrderCode,@Customer,@ShipTo,@Trip,
                    CONVERT(date,SYSUTCDATETIME()));
            INSERT dbo.AOrderLine(Id,TenantId,AOrderId,LineNumber,SkuId,
                BaseQuantity,UomCode,UomToBaseFactor,UnitPrice)
                VALUES(@OrderLine,@Tenant,@Order,1,@Sku,40,N'EA',1,15);
            INSERT dbo.AOrderTermsApproval(TenantId,AOrderId,TripId,CustomerId,
                TermsDays,Decision,PolicyVersion,ProposedBy,CheckedBy,
                EvidenceRef,ApprovedAtUtc,EffectiveFromUtc,EffectiveUntilUtc)
                VALUES(@Tenant,@Order,@Trip,@Customer,7,N'APPROVED',N'DEMO-SYNTHETIC-v1',
                    N'e2-maker',N'e2-checker',N'E2-terms',
                    DATEADD(minute,-1,SYSUTCDATETIME()),
                    DATEADD(day,-1,SYSUTCDATETIME()),DATEADD(year,10,SYSUTCDATETIME()));
            INSERT dbo.BCreditPool(TenantId,TripId,CustomerId,PoolCode,CurrencyUnit,ApprovedLimit)
                VALUES(@Tenant,@Trip,@Customer,N'B',N'SYNTHETIC_UNITLESS',100);
            INSERT dbo.CreditPolicyApproval(Id,TenantId,TripId,CustomerId,PoolCode,
                CurrencyUnit,CaseNumber,PolicyVersion,ProposedBy,CheckedBy,
                Decision,Reason,EvidenceRef,ApprovedLimit,
                EffectiveFromUtc,EffectiveUntilUtc,ReviewedAtUtc)
                VALUES(@Approval,@Tenant,@Trip,@Customer,N'B',N'SYNTHETIC_UNITLESS',
                    @ApprovalCase,N'E2-SYNTHETIC-v1',N'e2-maker',N'e2-checker',
                    N'APPROVED',N'Test only',N'approved-fixture',100,
                    DATEADD(day,-1,SYSUTCDATETIME()),DATEADD(year,10,SYSUTCDATETIME()),
                    SYSUTCDATETIME());
            INSERT dbo.StockPool(TenantId,StockLocationId,SkuId,OnHandBaseQuantity)
                VALUES(@Tenant,@Warehouse,@Sku,@Opening);
            INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,
                SnapshotCode,BaseQuantity,CostAmount)
                VALUES(@Tenant,@Warehouse,@Sku,N'DEMO-T0',@Opening,@OpeningCost);
            INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,
                OnHandBaseQuantity,OnHandCostAmount)
                VALUES(@Tenant,@Warehouse,@Sku,@Opening,@OpeningCost);
            INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,
                SnapshotCode,BaseQuantity,CostAmount)
                VALUES(@Tenant,@Vehicle,@Sku,N'DEMO-T0',0,0);
            INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,
                OnHandBaseQuantity,OnHandCostAmount)
                VALUES(@Tenant,@Vehicle,@Sku,0,0);
                INSERT dbo.LocalDemoScene VALUES(1,@Data);
            """, new {
                Tenant = tenant, s.Customer, s.ShipTo, s.Sku, s.Warehouse, s.Vehicle, s.Trip, s.Order, s.OrderLine,
                input.CustomerCode, input.CustomerName, input.ShipToName, input.Address, input.SkuCode, input.SkuName,
                input.Uom, input.WarehouseCode, input.VehicleCode, input.TripCode, input.OrderCode, input.Opening,
                OpeningCost = input.Opening * input.UnitCost, Approval = Guid.NewGuid(), ApprovalCase = run.Id,
                Data = JsonSerializer.Serialize(s)
            }, tx);
            tx.Commit();
        }

        if (s.Inputs != input)
            throw new InvalidOperationException("Chuyến đã được tạo với dữ liệu khác. Mở phiên Fresh để tạo chuyến mới.");
        if (await Current(db) >= 0) return s;
        var warehouse = Identity(tenant, "warehouse");
        var sales = Identity(tenant, "sales");
        var commands = new App01a2CommandService(connections);
        Require(await commands.ReserveAStockAsync(warehouse,
            new(Producer, "reserve", "AC03-RESERVE", s.Order, s.OrderLine, s.Warehouse, 40), default), "A_STOCK_RESERVED");
        Require(await commands.GrantBQuotaAsync(warehouse,
            new(Producer, "quota", "AC03-QUOTA", s.Trip, s.Warehouse, s.Sku, 30), default), "B_QUOTA_GRANTED");
        Require(await commands.ConfirmAOrderAsync(sales,
            new(Producer, "confirm", "AC03-CONFIRM", s.Order), default), "A_ORDER_CONFIRMED");
        Require(await commands.GrantBCreditAsync(warehouse,
            new(Producer, "credit", "AC03-CREDIT", s.Trip, s.Customer, s.ShipTo, "B", "SYNTHETIC_UNITLESS", 100), default),
            "B_CREDIT_GRANTED");

        using var manifestTx = db.BeginTransaction();
        await db.ExecuteAsync("""
            IF NOT EXISTS(SELECT 1 FROM dbo.TripManifest WHERE Id=@Manifest)
            BEGIN
                DECLARE @RightA uniqueidentifier=(SELECT Id FROM dbo.StockRight WHERE TripId=@Trip AND RightKind='A');
                DECLARE @RightB uniqueidentifier=(SELECT Id FROM dbo.StockRight WHERE TripId=@Trip AND RightKind='B');

                INSERT dbo.TripManifest(Id,TenantId,ManifestNumber,TripId,
                    WarehouseLocationId,VehicleLocationId)
                    VALUES(@Manifest,@Tenant,@Number,@Trip,@Warehouse,@Vehicle);
                INSERT dbo.TripManifestLine(Id,TenantId,TripManifestId,TripId,
                    WarehouseLocationId,VehicleLocationId,LineNumber,SkuId,
                    StockRightId,RightKind,PlannedBaseQuantity)
                    VALUES(@ALine,@Tenant,@Manifest,@Trip,@Warehouse,@Vehicle,
                        1,@Sku,@RightA,'A',40);
                INSERT dbo.TripManifestLine(Id,TenantId,TripManifestId,TripId,
                    WarehouseLocationId,VehicleLocationId,LineNumber,SkuId,
                    StockRightId,RightKind,PlannedBaseQuantity)
                    VALUES(@BLine,@Tenant,@Manifest,@Trip,@Warehouse,@Vehicle,
                        2,@Sku,@RightB,'B',30);
            END
            """, new { Tenant = tenant, s.Manifest, s.Trip, s.Warehouse, s.Vehicle, s.Sku,
                ALine = s.AManifestLine, BLine = s.BManifestLine, Number = "AC03-MANIFEST" }, manifestTx);
        manifestTx.Commit();
        await Capture(db, tenant, s, 0, null);
        return s;
    }

    public static Task<int> Current(SqlConnection db) =>
        db.ExecuteScalarAsync<int>("SELECT COALESCE(MAX(Step),-1) FROM dbo.LocalDemoCheckpoint");

    public static string Role(int action) => action switch
    {
        1 or 7 => "warehouse",
        2 or 3 or 4 or 5 => "sales",
        6 => "cashier",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    // Action 5 is a non-posting offer. Only its separately counted acceptance is oracle T5.
    private static int RequiredCheckpoint(int action) => action <= 5 ? action - 1 : action - 2;

    public async Task<BusinessCommandOutcome> Step(ClaimsPrincipal user, int action)
    {
        if (!user.HasClaim("synthetic_role", Role(action))) throw new UnauthorizedAccessException();
        string tenant = SyntheticAuth.GetTenantId(user);
        await using var db = await Open(user);
        var s = await Scene(db) ?? throw new InvalidOperationException("Create a trip first.");
        int current = await Current(db);
        if (current < RequiredCheckpoint(action)) throw new InvalidOperationException("Previous checkpoint required.");
        var args = new { Tenant = tenant, s.Trip, s.Customer, s.ShipTo };
        string key = $"step-{action}";
        string source = $"AC03-{action}";
        var transfer = new App01b2CommandService(connections);
        var close = new App01dCashierUnloadService(connections);
        BusinessCommandOutcome outcome;
        switch (action)
        {
            case 1:
                outcome = await transfer.LoadManifestAsync(user, new(Producer, key, source, s.Manifest), default);
                Require(outcome, "MANIFEST_LOADED");
                break;
            case 2:
                outcome = await transfer.PreflightBSaleAsync(user, new(Producer, key, source, s.Trip, s.Sku, 31, 15, 465), default);
                if (outcome.Status != "REJECTED" || outcome.ReasonCode != "B_QUOTA_INSUFFICIENT" || outcome.SourceId is not null)
                    throw new InvalidOperationException("Expected rejection for insufficient quota.");
                break;
            case 3:
                await db.ExecuteAsync("""
                    IF NOT EXISTS(SELECT 1 FROM dbo.VerifiedCashEvidence WHERE TenantId=@Tenant AND EvidenceRef='demo-payment')
                    INSERT dbo.VerifiedCashEvidence(TenantId,EvidenceRef,TripId,CustomerId,ShipToSnapshotId,PaymentMethod,
                        VerificationStatus,VerifiedAmount,VerifiedBy,VerifiedAtUtc)
                    VALUES(@Tenant,'demo-payment',@Trip,@Customer,@ShipTo,'CASH','VERIFIED',200,'demo-cash-checker',SYSUTCDATETIME());
                    """, args);
                outcome = await new App01c2BPostingService(connections).PostBSaleDeliveryReceiptAsync(user,
                    new(Producer, key, source, s.Trip, s.Customer, s.ShipTo, s.BManifestLine, s.Sku, 20, 15, 200, "demo-payment"), default);
                Require(outcome, "B_SALE_DELIVERY_POSTED");
                break;
            case 4:
                outcome = await new App01c3APostingService(connections).PostAPartialDeliveryAsync(user,
                    new(Producer, key, source, s.Trip, s.Customer, s.ShipTo, s.Order, s.OrderLine, s.AManifestLine, s.Sku, 25), default);
                Require(outcome, "A_PARTIAL_DELIVERY_POSTED");
                break;
            case 5:
                outcome = await close.OfferCashHandoverAsync(user, new(Producer, key, source, s.Trip, 200), default);
                Require(outcome, "CASH_HANDOVER_OFFERED");
                return outcome;
            case 6:
                var offer = await db.QuerySingleOrDefaultAsync<Guid>(
                    "SELECT Id FROM dbo.CashHandoverOffer WHERE TenantId=@Tenant AND TripId=@Trip", args);
                if (offer == Guid.Empty) throw new InvalidOperationException("Record the handover offer before counting.");
                await db.ExecuteAsync("""
                    IF NOT EXISTS(SELECT 1 FROM dbo.CashierCountEvidence WHERE TenantId=@Tenant AND EvidenceRef='demo-count')
                    INSERT dbo.CashierCountEvidence(TenantId,EvidenceRef,TripId,CountedAmount,CountedBy,VerificationStatus,CountedAtUtc)
                    VALUES(@Tenant,'demo-count',@Trip,200,'demo-cashier','VERIFIED',SYSUTCDATETIME());
                    """, args);
                outcome = await close.AcceptCashHandoverAsync(user, new(Producer, key, source, s.Trip, offer, "demo-count"), default);
                Require(outcome, "CASH_HANDOVER_ACCEPTED");
                break;
            default:
                outcome = await close.UnloadTripRemainderAsync(user, new(Producer, key, source, s.Trip, s.Manifest), default);
                Require(outcome, "TRIP_REMAINDER_UNLOADED");
                break;
        }
        int checkpoint = action < 5 ? action : action - 1;
        if (checkpoint > current) await Capture(db, tenant, s, checkpoint, outcome);
        return outcome;
    }

    private static void Require(BusinessCommandOutcome outcome, string code)
    {
        if (outcome.Status != "APPLIED" || outcome.OutcomeCode != code)
            throw new InvalidOperationException($"Command returned {outcome.OutcomeCode} ({outcome.ReasonCode}).");
    }
}
