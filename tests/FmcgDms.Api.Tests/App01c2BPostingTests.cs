using System.Net;
using System.Net.Http.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FmcgDms.Api.Tests;

/// <summary>
/// APP-01C.2 real LocalDB (two nonce-owned tenant DBs), HTTP synthetic JWT,
/// independent SQL state checks and adversarial B20 posting tests.
/// Does not claim APP-01C.4 65/65 or production-grade cash verification.
/// </summary>
[Collection("SqlScratch")]
public sealed class App01c2BPostingTests(SqlScratchFixture fixture)
{
    private const string A = "tenant-a";
    private const string B = "tenant-b";
    private const string Producer = "app01c2-tests";
    private const string RootA = "/api/synthetic/app01a/commands/";
    private const string RootB = "/api/synthetic/app01b/commands/";
    private const string RootC = "/api/synthetic/app01c/commands/post-b-sale-delivery-receipt";

    private sealed record Scene(Guid Customer, Guid ShipTo, Guid Sku,
        Guid Warehouse, Guid Vehicle, Guid Trip, Guid Order, Guid OrderLine,
        Guid Manifest, Guid AManifestLine, Guid BManifestLine, Guid ARight, Guid BRight);

    private sealed class SqlState
    {
        public decimal MainQty { get; set; }
        public decimal MainCost { get; set; }
        public decimal TripQty { get; set; }
        public decimal TripCost { get; set; }
        public decimal AHold { get; set; }
        public decimal BQuota { get; set; }
        public decimal CreditUsed { get; set; }
        public decimal ArB { get; set; }
        public decimal Cogs { get; set; }
        public decimal Custody { get; set; }
        public decimal Fund { get; set; }
        public int Deliveries { get; set; }
        public int Issues { get; set; }
        public int Receipts { get; set; }
        public int Allocations { get; set; }
        public int GrantConsumptions { get; set; }
        public int SaleSources { get; set; }
    }

    private static string Key() => Guid.NewGuid().ToString("N");
    private SqlConnection Open(string tenant) =>
        new(tenant == A ? fixture.TenantAConnectionString : fixture.TenantBConnectionString);

    private static async Task<(HttpStatusCode Status, BusinessCommandOutcome Outcome)> Send<T>(
        HttpClient client, string path, T request)
    {
        using var response = await client.PostAsJsonAsync(
            path, request, TestContext.Current.CancellationToken);
        var outcome = await response.Content.ReadFromJsonAsync<BusinessCommandOutcome>(
            TestContext.Current.CancellationToken);
        return (response.StatusCode, Assert.IsType<BusinessCommandOutcome>(outcome));
    }

    private static Task<(HttpStatusCode Status, BusinessCommandOutcome Outcome)> Post(
        HttpClient client, PostBSaleDeliveryReceiptCommand command) =>
        Send(client, RootC, command);

    private Scene Seed(string tenant, decimal openingCost = 1000m,
        decimal creditLimit = 100m)
    {
        var s = new Scene(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.Empty, Guid.Empty);
        using var db = Open(tenant);
        db.Execute("""
            INSERT dbo.Customer(Id,TenantId,Code,Name)
                VALUES(@Customer,@Tenant,@CustomerCode,N'C2 Customer');
            INSERT dbo.ShipToSnapshot(Id,TenantId,CustomerId,DisplayName,Address)
                VALUES(@ShipTo,@Tenant,@Customer,N'C2 ShipTo',N'Synthetic');
            INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom)
                VALUES(@Sku,@Tenant,@SkuCode,N'C2 SKU',N'EA');
            INSERT dbo.StockLocation(Id,TenantId,Code,Kind)
                VALUES(@Warehouse,@Tenant,@WhCode,N'WAREHOUSE');
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
                VALUES(@Tenant,@Order,@Trip,@Customer,7,N'APPROVED',N'C3-SYNTHETIC-v1',
                    N'synthetic-sales-maker',N'synthetic-finance-checker',N'A-terms-fixture',
                    DATEADD(minute,-1,SYSUTCDATETIME()),
                    DATEADD(day,-1,SYSUTCDATETIME()),DATEADD(day,7,SYSUTCDATETIME()));
            INSERT dbo.StockPool(TenantId,StockLocationId,SkuId,OnHandBaseQuantity)
                VALUES(@Tenant,@Warehouse,@Sku,100);
            INSERT dbo.BCreditPool(TenantId,TripId,CustomerId,PoolCode,CurrencyUnit,ApprovedLimit)
                VALUES(@Tenant,@Trip,@Customer,N'B',N'SYNTHETIC_UNITLESS',@CreditLimit);
            INSERT dbo.CreditPolicyApproval(Id,TenantId,TripId,CustomerId,PoolCode,
                CurrencyUnit,CaseNumber,PolicyVersion,ProposedBy,CheckedBy,
                Decision,Reason,EvidenceRef,ApprovedLimit,
                EffectiveFromUtc,EffectiveUntilUtc,ReviewedAtUtc)
            VALUES(@Approval,@Tenant,@Trip,@Customer,N'B',N'SYNTHETIC_UNITLESS',
                @ApprovalCase,N'C2-SYNTHETIC-v1',N'synthetic-actor',
                N'synthetic-finance-checker',N'APPROVED',N'Test only',
                N'approved-fixture',@CreditLimit,DATEADD(day,-1,SYSUTCDATETIME()),
                DATEADD(day,1,SYSUTCDATETIME()),SYSUTCDATETIME());
            INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,
                SnapshotCode,BaseQuantity,CostAmount)
                VALUES(@Tenant,@Warehouse,@Sku,N'C2-T0',100,@OpeningCost);
            INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,
                OnHandBaseQuantity,OnHandCostAmount)
                VALUES(@Tenant,@Warehouse,@Sku,100,@OpeningCost);
            INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,
                SnapshotCode,BaseQuantity,CostAmount)
                VALUES(@Tenant,@Vehicle,@Sku,N'C2-T0',0,0);
            INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,
                OnHandBaseQuantity,OnHandCostAmount)
                VALUES(@Tenant,@Vehicle,@Sku,0,0);
            """, new { Tenant = tenant, s.Customer, s.ShipTo, s.Sku, s.Warehouse,
                s.Vehicle, s.Trip, s.Order, s.OrderLine,
                CustomerCode = Key(), SkuCode = Key(), WhCode = Key(),
                VehicleCode = Key(), TripCode = Key(), OrderCode = Key(),
                Approval = Guid.NewGuid(), ApprovalCase = Key(),
                OpeningCost = openingCost, CreditLimit = creditLimit });
        return s;
    }

    private async Task<Scene> Prepare(HttpClient client, string tenant = A,
        decimal openingCost = 1000m, decimal grantAmount = 100m,
        decimal? secondGrant = null)
    {
        var s = Seed(tenant, openingCost, Math.Max(100m, grantAmount + (secondGrant ?? 0m)));
        Assert.Equal("A_STOCK_RESERVED", (await Send(client, RootA + "reserve-a-stock",
            new ReserveAStockCommand(Producer, Key(), Key(), s.Order, s.OrderLine,
                s.Warehouse, 40m))).Outcome.OutcomeCode);
        Assert.Equal("B_QUOTA_GRANTED", (await Send(client, RootA + "grant-b-quota",
            new GrantBQuotaCommand(Producer, Key(), Key(), s.Trip,
                s.Warehouse, s.Sku, 30m))).Outcome.OutcomeCode);
        Assert.Equal("A_ORDER_CONFIRMED", (await Send(client, RootA + "confirm-a-order",
            new ConfirmAOrderCommand(Producer, Key(), Key(),
                s.Order))).Outcome.OutcomeCode);
        if (grantAmount > 0)
            await GrantCredit(client, s, grantAmount);
        if (secondGrant is > 0)
            await GrantCredit(client, s, secondGrant.Value);

        using (var db = Open(tenant))
        {
            Guid rightA = db.QuerySingle<Guid>("""
                SELECT Id FROM dbo.StockRight WHERE TenantId=@Tenant
                    AND TripId=@Trip AND RightKind='A'
                """, new { Tenant = tenant, s.Trip });
            Guid rightB = db.QuerySingle<Guid>("""
                SELECT Id FROM dbo.StockRight WHERE TenantId=@Tenant
                    AND TripId=@Trip AND RightKind='B'
                """, new { Tenant = tenant, s.Trip });
            db.Execute("""
                INSERT dbo.TripManifest(Id,TenantId,ManifestNumber,TripId,
                    WarehouseLocationId,VehicleLocationId)
                VALUES(@Manifest,@Tenant,@Number,@Trip,@Warehouse,@Vehicle);
                INSERT dbo.TripManifestLine(Id,TenantId,TripManifestId,TripId,
                    WarehouseLocationId,VehicleLocationId,LineNumber,SkuId,
                    StockRightId,RightKind,PlannedBaseQuantity)
                VALUES(@ALine,@Tenant,@Manifest,@Trip,@Warehouse,@Vehicle,1,@Sku,
                    @RightA,'A',40);
                INSERT dbo.TripManifestLine(Id,TenantId,TripManifestId,TripId,
                    WarehouseLocationId,VehicleLocationId,LineNumber,SkuId,
                    StockRightId,RightKind,PlannedBaseQuantity)
                VALUES(@BLine,@Tenant,@Manifest,@Trip,@Warehouse,@Vehicle,2,@Sku,
                    @RightB,'B',30);
                """, new { Tenant = tenant, s.Manifest, s.Trip, s.Warehouse, s.Vehicle,
                    s.Sku, ALine = s.AManifestLine, BLine = s.BManifestLine,
                    RightA = rightA, RightB = rightB, Number = Key() });
            s = s with { ARight = rightA, BRight = rightB };
        }
        Assert.Equal("MANIFEST_LOADED", (await Send(client, RootB + "load-manifest",
            new LoadManifestCommand(Producer, Key(), Key(), s.Manifest))).Outcome.OutcomeCode);
        var b31 = await Send(client, RootB + "preflight-b-sale",
            new PreflightBSaleCommand(Producer, Key(), Key(), s.Trip, s.Sku, 31m, 15m, 465m));
        Assert.Equal("REJECTED_QUOTA", b31.Outcome.OutcomeCode);
        return s;
    }

    private static async Task GrantCredit(HttpClient client, Scene s, decimal amount)
    {
        var result = await Send(client, RootA + "grant-b-credit",
            new GrantBCreditCommand(Producer, Key(), Key(), s.Trip, s.Customer,
                s.ShipTo, "B", "SYNTHETIC_UNITLESS", amount));
        Assert.Equal("B_CREDIT_GRANTED", result.Outcome.OutcomeCode);
    }

    private string CashWitness(string tenant, Scene s, decimal amount,
        string status = "VERIFIED", string verifier = "synthetic-cash-checker",
        Guid? trip = null, Guid? customer = null, Guid? shipTo = null)
    {
        var evidence = Key();
        using var db = Open(tenant);
        db.Execute("""
            INSERT dbo.VerifiedCashEvidence(TenantId,EvidenceRef,TripId,CustomerId,
                ShipToSnapshotId,PaymentMethod,VerificationStatus,VerifiedAmount,
                VerifiedBy,VerifiedAtUtc)
            VALUES(@Tenant,@Evidence,@Trip,@Customer,@ShipTo,N'CASH',@Status,@Amount,
                @VerifiedBy,CASE WHEN @Status=N'VERIFIED' THEN DATEADD(second,-1,SYSUTCDATETIME()) END);
            """, new { Tenant = tenant, Evidence = evidence, Trip = trip ?? s.Trip,
                Customer = customer ?? s.Customer, ShipTo = shipTo ?? s.ShipTo,
                Status = status, Amount = amount,
                VerifiedBy = status == "VERIFIED" ? verifier : null });
        return evidence;
    }

    private static PostBSaleDeliveryReceiptCommand Sale(Scene s, string evidence,
        decimal qty = 20m, decimal price = 15m, decimal cash = 200m,
        string? command = null, string? source = null) =>
        new(Producer, command ?? Key(), source ?? Key(),
            s.Trip, s.Customer, s.ShipTo, s.BManifestLine, s.Sku,
            qty, price, cash, evidence);

    private SqlState State(string tenant, Scene s)
    {
        using var db = Open(tenant);
        return db.QuerySingle<SqlState>("""
            SELECT
                (SELECT OnHandBaseQuantity FROM dbo.PhysicalStockBalance
                 WHERE TenantId=@Tenant AND StockLocationId=@Warehouse AND SkuId=@Sku) MainQty,
                (SELECT OnHandCostAmount FROM dbo.PhysicalStockBalance
                 WHERE TenantId=@Tenant AND StockLocationId=@Warehouse AND SkuId=@Sku) MainCost,
                (SELECT OnHandBaseQuantity FROM dbo.PhysicalStockBalance
                 WHERE TenantId=@Tenant AND StockLocationId=@Vehicle AND SkuId=@Sku) TripQty,
                (SELECT OnHandCostAmount FROM dbo.PhysicalStockBalance
                 WHERE TenantId=@Tenant AND StockLocationId=@Vehicle AND SkuId=@Sku) TripCost,
                (SELECT GrantedBaseQuantity-ConsumedBaseQuantity-ReleasedBaseQuantity
                 FROM dbo.StockRight WHERE TenantId=@Tenant AND Id=@ARight) AHold,
                (SELECT GrantedBaseQuantity-ConsumedBaseQuantity-ReleasedBaseQuantity
                 FROM dbo.StockRight WHERE TenantId=@Tenant AND Id=@BRight) BQuota,
                (SELECT COALESCE(SUM(ConsumedAmount),0) FROM dbo.BCreditGrant
                 WHERE TenantId=@Tenant AND TripId=@Trip AND CustomerId=@Customer) CreditUsed,
                (SELECT COALESCE(SUM(r.OutstandingAmount),0) FROM dbo.ReceivableOpenItem r
                 JOIN dbo.SaleDelivery d ON d.Id=r.SaleDeliveryId AND d.TenantId=r.TenantId
                 WHERE r.TenantId=@Tenant AND d.TripId=@Trip AND r.RightKind='B') ArB,
                (SELECT COALESCE(SUM(e.AmountDelta),0) FROM dbo.LedgerEffect e
                 JOIN dbo.BusinessSourceLine l ON l.Id=e.BusinessSourceLineId AND l.TenantId=e.TenantId
                 JOIN dbo.BusinessSource src ON src.Id=l.BusinessSourceId AND src.TenantId=l.TenantId
                 WHERE e.TenantId=@Tenant AND src.TripId=@Trip AND src.SourceKind=N'B_SALE_DELIVERY'
                     AND e.LedgerKind=N'COGS') Cogs,
                (SELECT COALESCE(SUM(e.AmountDelta),0) FROM dbo.LedgerEffect e
                 JOIN dbo.BusinessSourceLine l ON l.Id=e.BusinessSourceLineId AND l.TenantId=e.TenantId
                 JOIN dbo.BusinessSource src ON src.Id=l.BusinessSourceId AND src.TenantId=l.TenantId
                 WHERE e.TenantId=@Tenant AND src.TripId=@Trip AND src.SourceKind=N'B_SALE_DELIVERY'
                     AND e.LedgerKind=N'CUSTODY') Custody,
                (SELECT COALESCE(SUM(e.AmountDelta),0) FROM dbo.LedgerEffect e
                 JOIN dbo.BusinessSourceLine l ON l.Id=e.BusinessSourceLineId AND l.TenantId=e.TenantId
                 JOIN dbo.BusinessSource src ON src.Id=l.BusinessSourceId AND src.TenantId=l.TenantId
                 WHERE e.TenantId=@Tenant AND src.TripId=@Trip AND e.LedgerKind=N'FUND') Fund,
                (SELECT COUNT(*) FROM dbo.SaleDelivery WHERE TenantId=@Tenant AND TripId=@Trip) Deliveries,
                (SELECT COUNT(*) FROM dbo.DeliveryStockIssue i
                 JOIN dbo.SaleDelivery d ON d.Id=i.SaleDeliveryId AND d.TenantId=i.TenantId
                 WHERE d.TenantId=@Tenant AND d.TripId=@Trip) Issues,
                (SELECT COUNT(*) FROM dbo.CashReceipt WHERE TenantId=@Tenant AND TripId=@Trip) Receipts,
                (SELECT COUNT(*) FROM dbo.ReceiptAllocation a
                 JOIN dbo.CashReceipt r ON r.Id=a.CashReceiptId AND r.TenantId=a.TenantId
                 WHERE r.TenantId=@Tenant AND r.TripId=@Trip) Allocations,
                (SELECT COUNT(*) FROM dbo.BGrantConsumption WHERE TenantId=@Tenant AND TripId=@Trip) GrantConsumptions,
                (SELECT COUNT(*) FROM dbo.BusinessSource
                 WHERE TenantId=@Tenant AND TripId=@Trip AND SourceKind=N'B_SALE_DELIVERY') SaleSources
            """, new { Tenant = tenant, s.Warehouse, s.Vehicle, s.Sku,
                s.ARight, s.BRight, s.Trip, s.Customer });
    }

    private static void EqualState(SqlState expected, SqlState actual)
    {
        Assert.Equal(expected.MainQty, actual.MainQty);
        Assert.Equal(expected.MainCost, actual.MainCost);
        Assert.Equal(expected.TripQty, actual.TripQty);
        Assert.Equal(expected.TripCost, actual.TripCost);
        Assert.Equal(expected.AHold, actual.AHold);
        Assert.Equal(expected.BQuota, actual.BQuota);
        Assert.Equal(expected.CreditUsed, actual.CreditUsed);
        Assert.Equal(expected.ArB, actual.ArB);
        Assert.Equal(expected.Cogs, actual.Cogs);
        Assert.Equal(expected.Custody, actual.Custody);
        Assert.Equal(expected.Fund, actual.Fund);
        Assert.Equal(expected.Deliveries, actual.Deliveries);
        Assert.Equal(expected.Issues, actual.Issues);
        Assert.Equal(expected.Receipts, actual.Receipts);
        Assert.Equal(expected.Allocations, actual.Allocations);
        Assert.Equal(expected.GrantConsumptions, actual.GrantConsumptions);
        Assert.Equal(expected.SaleSources, actual.SaleSources);
    }

    [Fact]
    public async Task T3_B20_persists_50_500_AR100_COGS200_custody200_and_B10()
    {
        using var factory = fixture.CreateFactory();
        using var client = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var s = await Prepare(client);
        var pre = State(A, s);
        Assert.Equal((30m, 300m, 70m, 700m, 40m, 30m, 0m, 0m),
            (pre.MainQty, pre.MainCost, pre.TripQty, pre.TripCost,
             pre.AHold, pre.BQuota, pre.ArB, pre.Cogs));
        var result = await Post(client, Sale(s, CashWitness(A, s, 200m)));
        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Equal("B_SALE_DELIVERY_POSTED", result.Outcome.OutcomeCode);
        Assert.NotNull(result.Outcome.SourceId);

        var t3 = State(A, s);
        Assert.Equal((30m, 300m, 50m, 500m), (t3.MainQty,t3.MainCost,t3.TripQty,t3.TripCost));
        Assert.Equal((40m, 10m, 100m, 100m, 200m, 200m, 0m),
            (t3.AHold,t3.BQuota,t3.CreditUsed,t3.ArB,t3.Cogs,t3.Custody,t3.Fund));
        Assert.Equal((1,1,1,1,1,1), (t3.Deliveries,t3.Issues,t3.Receipts,
            t3.Allocations,t3.GrantConsumptions,t3.SaleSources));

        using var db = Open(A);
        Assert.Equal((300m,200m,100m), db.QuerySingle<(decimal,decimal,decimal)>("""
            SELECT OriginalAmount,AppliedAmount,OutstandingAmount
            FROM dbo.ReceivableOpenItem WHERE TenantId=@Tenant
                AND SaleDeliveryId=(SELECT Id FROM dbo.SaleDelivery
                    WHERE TenantId=@Tenant AND BusinessSourceId=@Source)
            """, new { Tenant = A, Source = result.Outcome.SourceId }));
        Assert.Equal((20m,200m), db.QuerySingle<(decimal,decimal)>("""
            SELECT BaseQuantity,CostAmount FROM dbo.DeliveryStockIssue
            WHERE TenantId=@Tenant AND SaleDeliveryId=(
                SELECT Id FROM dbo.SaleDelivery WHERE TenantId=@Tenant
                    AND BusinessSourceId=@Source)
            """, new { Tenant = A, Source = result.Outcome.SourceId }));
        Assert.Equal(7, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.LedgerEffect e
            JOIN dbo.BusinessSourceLine l ON l.Id=e.BusinessSourceLineId AND l.TenantId=e.TenantId
            WHERE l.TenantId=@Tenant AND l.BusinessSourceId=@Source
            """, new { Tenant = A, Source = result.Outcome.SourceId }));
        Assert.Equal(0, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.LedgerEffect e
            JOIN dbo.BusinessSourceLine l ON l.Id=e.BusinessSourceLineId AND l.TenantId=e.TenantId
            WHERE l.TenantId=@Tenant AND l.BusinessSourceId=@Source
                AND e.LedgerKind IN (N'FUND',N'REVENUE',N'RIGHT_A')
            """, new { Tenant = A, Source = result.Outcome.SourceId }));
    }

    [Fact]
    public async Task Same_command_replay_changed_payload_cross_kind_and_duplicate_source_are_safe()
    {
        using var factory = fixture.CreateFactory();
        using var client = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var s = await Prepare(client);
        var req = Sale(s, CashWitness(A,s,200m));
        var applied = await Post(client, req);
        Assert.Equal("APPLIED", applied.Outcome.Status);
        var snap = State(A,s);
        var replay = await Post(client, req with { BaseQuantity = 20.000m });
        Assert.True(replay.Outcome.Replayed);
        Assert.Equal(applied.Outcome.SourceId, replay.Outcome.SourceId);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(client, req with
            { UnitPrice = 14m })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(client,
            RootB + "preflight-b-sale", new PreflightBSaleCommand(
                Producer, req.CommandId, Key(), s.Trip, s.Sku, 1m, 15m, 15m))).Status);
        Assert.Equal("SOURCE_ALREADY_USED", (await Post(client,
            req with { CommandId = Key() })).Outcome.ReasonCode);
        EqualState(snap, State(A,s));
    }

    [Fact]
    public async Task Missing_pending_wrong_amount_self_verified_or_reused_cash_never_posts()
    {
        using var factory = fixture.CreateFactory();
        using var client = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var s = await Prepare(client);
        var before = State(A,s);
        foreach (var evidence in new[] {
            Key(),
            CashWitness(A,s,200m,status:"PENDING"),
            CashWitness(A,s,199m),
            CashWitness(A,s,200m,verifier:"synthetic-actor"),
            CashWitness(A,s,200m,verifier:"synthetic-actor ")
        })
        {
            var result = await Post(client, Sale(s,evidence));
            Assert.Equal("CASH_EVIDENCE_NOT_VERIFIED", result.Outcome.ReasonCode);
            EqualState(before, State(A,s));
        }
        var validRef = CashWitness(A,s,200m);
        var valid = await Post(client, Sale(s,validRef));
        Assert.True(valid.Outcome.Status == "APPLIED", valid.Outcome.ReasonCode ?? valid.Outcome.Status);
        var after = State(A,s);
        Assert.Equal("CASH_EVIDENCE_ALREADY_USED", (await Post(client,
            Sale(s,validRef,qty:10m,price:30m,cash:200m))).Outcome.ReasonCode);
        EqualState(after, State(A,s));
    }

    [Fact]
    public async Task Exceeding_B_right_credit_or_physical_vehicle_rejects_without_any_effect()
    {
        using var factory = fixture.CreateFactory();
        using var client = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var s = await Prepare(client, grantAmount:99m);
        var before = State(A,s);
        Assert.Equal("B_QUOTA_INSUFFICIENT", (await Post(client,
            Sale(s,CashWitness(A,s,200m),qty:31m))).Outcome.ReasonCode);
        Assert.Equal("B_CREDIT_INSUFFICIENT", (await Post(client,
            Sale(s,CashWitness(A,s,200m)))).Outcome.ReasonCode);
        EqualState(before, State(A,s));

        using var db = Open(A);
        db.Execute("""
            UPDATE dbo.PhysicalStockBalance SET OnHandBaseQuantity=10,OnHandCostAmount=100
            WHERE TenantId=@Tenant AND StockLocationId=@Vehicle AND SkuId=@Sku
            """, new { Tenant=A,s.Vehicle,s.Sku });
        var damaged = State(A,s);
        Assert.Equal("VEHICLE_STOCK_INSUFFICIENT", (await Post(client,
            Sale(s,CashWitness(A,s,200m)))).Outcome.ReasonCode);
        EqualState(damaged, State(A,s));

        // A positive but inflated mutable balance must not become a saleable
        // vehicle source; independent manifest movements still total 70/700.
        db.Execute("""
            UPDATE dbo.PhysicalStockBalance
            SET OnHandBaseQuantity=80,OnHandCostAmount=800
            WHERE TenantId=@Tenant AND StockLocationId=@Vehicle AND SkuId=@Sku
            """, new { Tenant=A,s.Vehicle,s.Sku });
        var inflated = State(A,s);
        Assert.Equal("VEHICLE_STOCK_SOURCE_DRIFT", (await Post(client,
            Sale(s,CashWitness(A,s,200m)))).Outcome.ReasonCode);
        EqualState(inflated, State(A,s));

        // Quantity alone can agree while cost provenance is corrupted.
        db.Execute("""
            UPDATE dbo.PhysicalStockBalance
            SET OnHandBaseQuantity=70,OnHandCostAmount=701
            WHERE TenantId=@Tenant AND StockLocationId=@Vehicle AND SkuId=@Sku
            """, new { Tenant=A,s.Vehicle,s.Sku });
        var wrongCost = State(A,s);
        Assert.Equal("VEHICLE_STOCK_SOURCE_DRIFT", (await Post(client,
            Sale(s,CashWitness(A,s,200m)))).Outcome.ReasonCode);
        EqualState(wrongCost, State(A,s));
    }

    [Fact]
    public async Task Two_clients_competing_for_B_right_cannot_both_sell_20()
    {
        using var factory = fixture.CreateFactory();
        using var left = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        using var right = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var s = await Prepare(left);
        var requests = new[] {
            Sale(s,CashWitness(A,s,200m)),
            Sale(s,CashWitness(A,s,200m))
        };
        var outcomes = await Task.WhenAll(Post(left,requests[0]),Post(right,requests[1]));
        Assert.Equal(1, outcomes.Count(x => x.Outcome.Status=="APPLIED"));
        Assert.Equal(1, outcomes.Count(x => x.Outcome.ReasonCode=="B_QUOTA_INSUFFICIENT"));
        Assert.Equal((50m,500m,10m,1), (State(A,s).TripQty,State(A,s).TripCost,
            State(A,s).BQuota,State(A,s).Deliveries));
    }

    [Fact]
    public async Task Two_clients_competing_for_one_credit_grant_cannot_spend_it_twice()
    {
        using var factory = fixture.CreateFactory();
        using var left = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        using var right = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var s = await Prepare(left);
        var requests = new[] {
            Sale(s,CashWitness(A,s,50m),qty:10m,cash:50m),
            Sale(s,CashWitness(A,s,50m),qty:10m,cash:50m)
        };
        var results = await Task.WhenAll(Post(left,requests[0]),Post(right,requests[1]));
        Assert.Equal(1, results.Count(x=>x.Outcome.Status=="APPLIED"));
        Assert.Equal(1, results.Count(x=>x.Outcome.ReasonCode=="B_CREDIT_INSUFFICIENT"));
        var t3 = State(A,s);
        Assert.Equal((60m,600m,20m,100m,100m,50m,1),
            (t3.TripQty,t3.TripCost,t3.BQuota,t3.CreditUsed,t3.ArB,t3.Custody,t3.Deliveries));
    }

    [Fact]
    public async Task After_allocation_SQL_fault_rolls_back_command_source_and_every_counter_then_retry()
    {
        using var factory = fixture.CreateFactory();
        using var client = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var s = await Prepare(client);
        var req = Sale(s,CashWitness(A,s,200m));
        var before = State(A,s);
        using var db = Open(A);
        db.Execute($"""
            CREATE TRIGGER dbo.TR_App01c2_Fault ON dbo.ReceiptAllocation
            AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM inserted i
                    JOIN dbo.CashReceipt r ON r.Id=i.CashReceiptId
                        AND r.TenantId=i.TenantId
                    WHERE r.TripId='{s.Trip:D}')
                    THROW 51079, 'APP01C2 forced after allocation rollback', 1;
            END;
            """);
        try
        {
            bool failed;
            try
            {
                using var response = await client.PostAsJsonAsync(
                    RootC,req,TestContext.Current.CancellationToken);
                failed = !response.IsSuccessStatusCode;
            }
            catch (Exception) { failed=true; }
            Assert.True(failed);
            EqualState(before,State(A,s));
            Assert.Equal(0,db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM dbo.BusinessCommand
                WHERE TenantId=@Tenant AND ProducerId=@Producer AND CommandId=@Command
                """,new { Tenant=A,Producer,Command=req.CommandId }));
        }
        finally { db.Execute("DROP TRIGGER IF EXISTS dbo.TR_App01c2_Fault"); }
        var again = await Post(client,req);
        Assert.Equal("B_SALE_DELIVERY_POSTED",again.Outcome.OutcomeCode);
        Assert.False(again.Outcome.Replayed);
        Assert.Equal(1,State(A,s).Deliveries);
    }

    [Fact]
    public async Task Actor_policy_two_tenant_routing_and_foreign_cash_witness_fail_closed()
    {
        using var factory=fixture.CreateFactory();
        using var a=SqlScratchFixture.WithTenant(factory.CreateClient(),A);
        using var b=SqlScratchFixture.WithTenant(factory.CreateClient(),B);
        using var warehouse=SqlScratchFixture.WithTenant(factory.CreateClient(),A,
            roles:["warehouse"]);
        var s=await Prepare(a);
        var other=await Prepare(b,B);
        var cash=CashWitness(A,s,200m);
        var request=Sale(s,cash);
        using(var unauthorized=await warehouse.PostAsJsonAsync(
            RootC,request,TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.Forbidden,unauthorized.StatusCode);
        Assert.Equal("TRIP_NOT_ELIGIBLE",(await Post(b,request)).Outcome.ReasonCode);
        Assert.Equal("CASH_EVIDENCE_NOT_VERIFIED",(await Post(a,Sale(s,CashWitness(B,other,200m))))
            .Outcome.ReasonCode);
        Assert.Equal(0,State(A,s).Deliveries);
        Assert.Equal(0,State(B,other).Deliveries);
        Assert.Equal(70m,State(B,other).TripQty);
    }

    [Fact]
    public async Task One_B_delivery_can_consume_multiple_independent_approved_grants()
    {
        using var factory=fixture.CreateFactory();
        using var client=SqlScratchFixture.WithTenant(factory.CreateClient(),A);
        var s=await Prepare(client,grantAmount:60m,secondGrant:40m);
        var result=await Post(client,Sale(s,CashWitness(A,s,200m)));
        Assert.Equal("B_SALE_DELIVERY_POSTED",result.Outcome.OutcomeCode);
        var t3=State(A,s);
        Assert.Equal(2,t3.GrantConsumptions);
        Assert.Equal(100m,t3.CreditUsed);
        using var db=Open(A);
        Assert.Equal(new[]{40m,60m},db.Query<decimal>("""
            SELECT ConsumedAmount FROM dbo.BCreditGrant
            WHERE TenantId=@Tenant AND TripId=@Trip ORDER BY ConsumedAmount
            """,new { Tenant=A,s.Trip }).ToArray());
        Assert.Equal(100m,db.ExecuteScalar<decimal>("""
            SELECT -SUM(e.AmountDelta) FROM dbo.LedgerEffect e
            JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId AND l.Id=e.BusinessSourceLineId
            WHERE e.TenantId=@Tenant AND l.BusinessSourceId=@Source
                AND e.LedgerKind=N'RIGHT_CREDIT_B_CONSUMED'
            """,new { Tenant=A,Source=result.Outcome.SourceId }));
    }

    [Fact]
    public async Task Fractional_moving_average_cost_is_preserved_at_four_decimal_places()
    {
        using var factory=fixture.CreateFactory();
        using var client=SqlScratchFixture.WithTenant(factory.CreateClient(),A);
        var s=await Prepare(client,openingCost:1000.1m);
        Assert.Equal(700.07m,State(A,s).TripCost);
        var result=await Post(client,Sale(s,CashWitness(A,s,200m)));
        Assert.Equal("APPLIED",result.Outcome.Status);
        var state=State(A,s);
        Assert.Equal((50m,500.05m,200.02m),(state.TripQty,state.TripCost,state.Cogs));
    }
}
