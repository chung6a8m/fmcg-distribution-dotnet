using System.Net;
using System.Net.Http.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FmcgDms.Api.Tests;

/// <summary>
/// APP-01C.3 two-tenant nonce-owned SQL Server LocalDB transactional A25.
/// Confirm snapshots derive price and N days from independently approved witness.
/// These tests do not claim a standalone APP-01C.4 65/65 SQL oracle.
/// </summary>
[Collection("SqlScratch")]
public sealed class App01c3APartialDeliveryTests(SqlScratchFixture fixture)
{
    private const string A = "tenant-a";
    private const string B = "tenant-b";
    private const string Producer = "app01c3-tests";
    private const string RootA = "/api/synthetic/app01a/commands/";
    private const string RootB = "/api/synthetic/app01b/commands/";
    private const string RootC = "/api/synthetic/app01c/commands/post-b-sale-delivery-receipt";
    private const string RootA25 = "/api/synthetic/app01c/commands/post-a-partial-delivery";

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
                @VerifiedBy,@VerifiedAt);
            """, new { Tenant = tenant, Evidence = evidence, Trip = trip ?? s.Trip,
                Customer = customer ?? s.Customer, ShipTo = shipTo ?? s.ShipTo,
                Status = status, Amount = amount,
                VerifiedBy = status == "VERIFIED" ? verifier : null,
                VerifiedAt = status == "VERIFIED" ? (DateTime?)DateTime.UtcNow : null });
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
                 WHERE e.TenantId=@Tenant AND e.LedgerKind=N'FUND') Fund,
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

    private static PostAPartialDeliveryCommand A25(Scene s, decimal quantity = 25m,
        string? command = null, string? source = null) =>
        new(Producer, command ?? Key(), source ?? Key(), s.Trip, s.Customer, s.ShipTo,
            s.Order, s.OrderLine, s.AManifestLine, s.Sku, quantity);

    private static Task<(HttpStatusCode Status, BusinessCommandOutcome Outcome)> PostA(
        HttpClient client, PostAPartialDeliveryCommand command) =>
        Send(client, RootA25, command);

    private sealed class AState
    {
        public decimal ArA { get; set; }
        public decimal ArB { get; set; }
        public decimal TotalCogs { get; set; }
        public decimal AConsumed { get; set; }
        public decimal AOriginal { get; set; }
        public decimal ACost { get; set; }
        public decimal FrozenPrice { get; set; }
        public int FrozenTerms { get; set; }
        public DateTime? DueDate { get; set; }
        public string OrderStatus { get; set; } = "";
        public int ADeliveries { get; set; }
        public int AEffects { get; set; }
        public int AReceipts { get; set; }
        public int AGrantConsumptions { get; set; }
    }

    private AState ReadA(string tenant, Scene s)
    {
        using var db = Open(tenant);
        return db.QuerySingle<AState>("""
            SELECT
                (SELECT COALESCE(SUM(OutstandingAmount),0)
                 FROM dbo.ReceivableOpenItem
                 WHERE TenantId=@Tenant AND RightKind='A'
                   AND SaleDeliveryId IN (SELECT Id FROM dbo.SaleDelivery
                     WHERE TenantId=@Tenant AND AOrderId=@Order)) ArA,
                (SELECT COALESCE(SUM(OutstandingAmount),0)
                 FROM dbo.ReceivableOpenItem
                 WHERE TenantId=@Tenant AND RightKind='B'
                   AND SaleDeliveryId IN (SELECT Id FROM dbo.SaleDelivery
                     WHERE TenantId=@Tenant AND TripId=@Trip)) ArB,
                (SELECT COALESCE(SUM(e.AmountDelta),0) FROM dbo.LedgerEffect e
                  JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId
                    AND l.Id=e.BusinessSourceLineId
                  JOIN dbo.BusinessSource src ON src.TenantId=l.TenantId
                    AND src.Id=l.BusinessSourceId
                  WHERE src.TenantId=@Tenant AND src.TripId=@Trip
                    AND e.LedgerKind=N'COGS') TotalCogs,
                (SELECT ConsumedBaseQuantity FROM dbo.StockRight
                 WHERE TenantId=@Tenant AND Id=@ARight) AConsumed,
                (SELECT COALESCE(SUM(OriginalAmount),0)
                 FROM dbo.ReceivableOpenItem
                 WHERE TenantId=@Tenant AND RightKind='A'
                   AND SaleDeliveryId IN (SELECT Id FROM dbo.SaleDelivery
                     WHERE TenantId=@Tenant AND AOrderId=@Order)) AOriginal,
                (SELECT COALESCE(SUM(CostAmount),0) FROM dbo.SaleDelivery
                 WHERE TenantId=@Tenant AND AOrderId=@Order AND RightKind='A') ACost,
                (SELECT UnitPriceSnapshot FROM dbo.AConfirmedLineSnapshot
                 WHERE TenantId=@Tenant AND AOrderLineId=@OrderLine) FrozenPrice,
                (SELECT TermsDaysSnapshot FROM dbo.AConfirmedLineSnapshot
                 WHERE TenantId=@Tenant AND AOrderLineId=@OrderLine) FrozenTerms,
                (SELECT MIN(DueBusinessDate) FROM dbo.ReceivableOpenItem
                 WHERE TenantId=@Tenant AND RightKind='A'
                   AND SaleDeliveryId IN (SELECT Id FROM dbo.SaleDelivery
                     WHERE TenantId=@Tenant AND AOrderId=@Order)) DueDate,
                (SELECT Status FROM dbo.AOrder WHERE TenantId=@Tenant AND Id=@Order) OrderStatus,
                (SELECT COUNT(*) FROM dbo.SaleDelivery
                 WHERE TenantId=@Tenant AND AOrderId=@Order AND RightKind='A') ADeliveries,
                (SELECT COUNT(*) FROM dbo.LedgerEffect e
                   JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId
                      AND l.Id=e.BusinessSourceLineId
                   JOIN dbo.BusinessSource src ON src.TenantId=l.TenantId
                      AND src.Id=l.BusinessSourceId
                 WHERE src.TenantId=@Tenant AND src.TripId=@Trip
                   AND src.SourceKind=N'A_DELIVERY') AEffects,
                (SELECT COUNT(*) FROM dbo.CashReceipt r
                   JOIN dbo.SaleDelivery d ON d.TenantId=r.TenantId
                     AND d.Id=r.SaleDeliveryId
                 WHERE r.TenantId=@Tenant AND d.AOrderId=@Order) AReceipts,
                (SELECT COUNT(*) FROM dbo.BGrantConsumption g
                   JOIN dbo.SaleDelivery d ON d.TenantId=g.TenantId
                     AND d.Id=g.SaleDeliveryId
                 WHERE g.TenantId=@Tenant AND d.AOrderId=@Order) AGrantConsumptions
            """, new { Tenant = tenant, s.Trip, s.Order, s.OrderLine, s.ARight });
    }

    // Rejected commands are durable, but no stock/right, source/ledger, AR,
    // receipt or credit-grant business state may change. Use one SQL-derived
    // fingerprint rather than trusting the HTTP outcome alone.
    private string Fingerprint(string tenant, Scene s)
    {
        using var db = Open(tenant);
        return db.QuerySingle<string>("""
            SELECT
                (SELECT OnHandBaseQuantity FROM dbo.PhysicalStockBalance
                 WHERE TenantId=@Tenant AND StockLocationId=@Vehicle AND SkuId=@Sku) TripQty,
                (SELECT OnHandCostAmount FROM dbo.PhysicalStockBalance
                 WHERE TenantId=@Tenant AND StockLocationId=@Vehicle AND SkuId=@Sku) TripCost,
                (SELECT ConsumedBaseQuantity FROM dbo.StockRight
                 WHERE TenantId=@Tenant AND Id=@ARight) AConsumed,
                (SELECT ConsumedBaseQuantity FROM dbo.StockRight
                 WHERE TenantId=@Tenant AND Id=@BRight) BConsumed,
                (SELECT Status FROM dbo.AOrder WHERE TenantId=@Tenant AND Id=@Order) OrderStatus,
                (SELECT COUNT(*) FROM dbo.SaleDelivery WHERE TenantId=@Tenant AND TripId=@Trip) Deliveries,
                (SELECT COUNT(*) FROM dbo.DeliveryStockIssue i
                 JOIN dbo.SaleDelivery d ON d.TenantId=i.TenantId AND d.Id=i.SaleDeliveryId
                 WHERE d.TenantId=@Tenant AND d.TripId=@Trip) Issues,
                (SELECT COUNT(*) FROM dbo.BusinessSource WHERE TenantId=@Tenant
                 AND TripId=@Trip AND SourceKind IN (N'A_DELIVERY',N'B_SALE_DELIVERY')) SalesSources,
                (SELECT COUNT(*) FROM dbo.LedgerEffect e
                  JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId
                     AND l.Id=e.BusinessSourceLineId
                  JOIN dbo.BusinessSource src ON src.TenantId=l.TenantId
                     AND src.Id=l.BusinessSourceId
                  WHERE src.TenantId=@Tenant AND src.TripId=@Trip) Effects,
                (SELECT COALESCE(SUM(OutstandingAmount),0) FROM dbo.ReceivableOpenItem
                 WHERE TenantId=@Tenant) OutstandingAr,
                (SELECT COUNT(*) FROM dbo.CashReceipt WHERE TenantId=@Tenant) Receipts,
                (SELECT COUNT(*) FROM dbo.BGrantConsumption WHERE TenantId=@Tenant) CreditUsages
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
            """, new { Tenant=tenant, s.Vehicle, s.Sku, s.ARight, s.BRight,
                s.Order, s.Trip });
    }

    [Fact]
    public async Task T4_after_B20_matches_all_physical_right_AR_COGS_custody_expected_on_two_tenants()
    {
        using var factory=fixture.CreateFactory();
        foreach (var tenant in new[] { A, B })
        {
            using var client=SqlScratchFixture.WithTenant(factory.CreateClient(),tenant);
            var s=await Prepare(client,tenant);
            Assert.Equal("B_SALE_DELIVERY_POSTED",(await Post(client,
                Sale(s,CashWitness(tenant,s,200m)))).Outcome.OutcomeCode);
            var pre=State(tenant,s);
            Assert.Equal((50m,500m,40m,10m,100m,200m),
                (pre.TripQty,pre.TripCost,pre.AHold,pre.BQuota,pre.ArB,pre.Custody));

            var posted=await PostA(client,A25(s));
            Assert.Equal(HttpStatusCode.OK,posted.Status);
            Assert.Equal("A_PARTIAL_DELIVERY_POSTED",posted.Outcome.OutcomeCode);
            var state=State(tenant,s);
            var a=ReadA(tenant,s);
            Assert.Equal((30m,300m,25m,250m),(state.MainQty,state.MainCost,
                state.TripQty,state.TripCost));
            Assert.Equal((15m,10m,100m,200m,0m),(state.AHold,state.BQuota,
                state.ArB,state.Custody,state.Fund));
            Assert.Equal((375m,100m,450m,25m,375m,250m),
                (a.ArA,a.ArB,a.TotalCogs,a.AConsumed,a.AOriginal,a.ACost));
            Assert.Equal((15m,7,"PARTIALLY_DELIVERED",1,4,0,0),
                (a.FrozenPrice,a.FrozenTerms,a.OrderStatus,a.ADeliveries,
                 a.AEffects,a.AReceipts,a.AGrantConsumptions));
            using var db=Open(tenant);
            var expectedDate=db.ExecuteScalar<DateTime>("""
                SELECT DATEADD(day,7,BusinessDate) FROM dbo.Trip
                WHERE TenantId=@Tenant AND Id=@Trip
                """,new { Tenant=tenant,s.Trip });
            Assert.Equal(expectedDate.Date,a.DueDate?.Date);
            Assert.Equal(4,db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM dbo.LedgerEffect e
                JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId
                    AND l.Id=e.BusinessSourceLineId
                WHERE l.TenantId=@Tenant AND l.BusinessSourceId=@Source
                """,new { Tenant=tenant, Source=posted.Outcome.SourceId }));
            // Independent audit-as-of reconstructs all four A source legs,
            // due-dated AR and B20 effects, not just an HTTP success code.
            using var auditor=SqlScratchFixture.WithTenant(
                factory.CreateClient(),tenant,roles:["auditor"]);
            string asOf=Uri.EscapeDataString(
                DateTimeOffset.UtcNow.AddSeconds(5).ToString("O"));
            var audit=await auditor.GetFromJsonAsync<App01aAuditSnapshot>(
                "/api/synthetic/app01a/audit/as-of?asOf="+asOf,
                TestContext.Current.CancellationToken);
            Assert.NotNull(audit);
            Assert.Empty(audit.ReconciliationIssues);
            Assert.Single(audit.Sources,x=>x.SourceKind=="A_DELIVERY"
                && x.Id==posted.Outcome.SourceId);

        }
    }

    [Fact]
    public async Task Split_A_25_then_15_fulfills_only_delivered_quantity_and_never_spends_B()
    {
        using var factory=fixture.CreateFactory();
        using var client=SqlScratchFixture.WithTenant(factory.CreateClient(),A);
        var s=await Prepare(client);
        Assert.Equal("APPLIED",(await PostA(client,A25(s))).Outcome.Status);
        Assert.Equal("PARTIALLY_DELIVERED",ReadA(A,s).OrderStatus);
        Assert.Equal("APPLIED",(await PostA(client,A25(s,15m))).Outcome.Status);
        var state=State(A,s);var a=ReadA(A,s);
        Assert.Equal((30m,300m,0m,30m,600m,400m,"FULFILLED"),
            (state.TripQty,state.TripCost,state.AHold,state.BQuota,
             a.ArA,a.TotalCogs,a.OrderStatus));
        Assert.Equal((2,8,0,0),(a.ADeliveries,a.AEffects,a.AReceipts,a.AGrantConsumptions));
        var rejected=await PostA(client,A25(s,1m));
        Assert.Equal("A_ORDER_NOT_DELIVERABLE",rejected.Outcome.ReasonCode);
        Assert.Equal(2,ReadA(A,s).ADeliveries);
    }

    [Fact]
    public async Task A_price_and_due_terms_remain_frozen_after_source_master_edits()
    {
        using var factory=fixture.CreateFactory();
        using var client=SqlScratchFixture.WithTenant(factory.CreateClient(),A);
        var s=await Prepare(client);
        using var db=Open(A);
        db.Execute("""
            UPDATE dbo.AOrderLine SET UnitPrice=99
            WHERE TenantId=@Tenant AND Id=@OrderLine
            """,new { Tenant=A,s.OrderLine });
        Assert.Throws<SqlException>(()=>db.Execute("""
            UPDATE dbo.AOrderTermsApproval SET TermsDays=14
            WHERE TenantId=@Tenant AND AOrderId=@Order
            """,new { Tenant=A,s.Order }));
        Assert.Equal("APPLIED",(await PostA(client,A25(s))).Outcome.Status);
        var a=ReadA(A,s);
        Assert.Equal((15m,7,375m,375m),(a.FrozenPrice,a.FrozenTerms,a.ArA,a.AOriginal));
        Assert.Equal(db.ExecuteScalar<DateTime>(
            "SELECT DATEADD(day,7,BusinessDate) FROM dbo.Trip WHERE Id=@Trip",
            new {s.Trip}).Date,a.DueDate?.Date);
    }

    [Fact]
    public async Task Missing_or_pending_A_terms_approval_rejects_confirm_without_snapshot()
    {
        using var factory=fixture.CreateFactory();
        using var client=SqlScratchFixture.WithTenant(factory.CreateClient(),A);
        var s=Seed(A);
        Guid anotherOrder=Guid.NewGuid(),anotherLine=Guid.NewGuid();
        using var db=Open(A);
        db.Execute("""
            INSERT dbo.AOrder(Id,TenantId,OrderNumber,CustomerId,ShipToSnapshotId,
                TripId,BusinessDate)
            VALUES(@Order,@Tenant,@Number,@Customer,@ShipTo,@Trip,
                CONVERT(date,SYSUTCDATETIME()));
            INSERT dbo.AOrderLine(Id,TenantId,AOrderId,LineNumber,SkuId,
                BaseQuantity,UomCode,UomToBaseFactor,UnitPrice)
            VALUES(@Line,@Tenant,@Order,1,@Sku,1,N'EA',1,15);
            """,new { Tenant=A, Order=anotherOrder, Line=anotherLine,
                Number=Key(),s.Customer,s.ShipTo,s.Trip,s.Sku });
        var cmd=new ConfirmAOrderCommand(Producer,Key(),Key(),anotherOrder);
        Assert.Equal("A_TERMS_APPROVAL_REQUIRED",(await Send(client,
            RootA+"confirm-a-order",cmd)).Outcome.ReasonCode);
        db.Execute("""
            INSERT dbo.AOrderTermsApproval(TenantId,AOrderId,TripId,CustomerId,
                TermsDays,Decision,PolicyVersion,ProposedBy,
                EffectiveFromUtc,EffectiveUntilUtc)
            VALUES(@Tenant,@Order,@Trip,@Customer,7,N'PENDING',N'test-v1',
                N'synthetic-maker',DATEADD(day,-1,SYSUTCDATETIME()),
                DATEADD(day,1,SYSUTCDATETIME()));
            """,new { Tenant=A,Order=anotherOrder,s.Trip,s.Customer });
        Assert.Equal("A_TERMS_APPROVAL_REQUIRED",(await Send(client,
            RootA+"confirm-a-order",cmd with {CommandId=Key()})).Outcome.ReasonCode);
        Assert.Equal(0,db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.AConfirmedLineSnapshot WHERE TenantId=@Tenant
                AND AOrderId=@Order
            """,new { Tenant=A,Order=anotherOrder }));
        Assert.Equal("DRAFT",db.ExecuteScalar<string>(
            "SELECT Status FROM dbo.AOrder WHERE TenantId=@Tenant AND Id=@Order",
            new { Tenant=A,Order=anotherOrder }));
    }

    [Fact]
    public async Task Same_key_replay_changed_payload_cross_kind_and_duplicate_source_are_safe()
    {
        using var factory=fixture.CreateFactory();
        using var client=SqlScratchFixture.WithTenant(factory.CreateClient(),A);
        var s=await Prepare(client);
        var req=A25(s);var applied=await PostA(client,req);
        Assert.Equal("APPLIED",applied.Outcome.Status);
        string after=Fingerprint(A,s);
        var replay=await PostA(client,req with {BaseQuantity=25.000m});
        Assert.True(replay.Outcome.Replayed);
        Assert.Equal(applied.Outcome.SourceId,replay.Outcome.SourceId);
        Assert.Equal(HttpStatusCode.Conflict,(await PostA(client,req with
            {BaseQuantity=15m})).Status);
        Assert.Equal(HttpStatusCode.Conflict,(await Send(client,
            RootB+"preflight-b-sale",new PreflightBSaleCommand(
                Producer,req.CommandId,Key(),s.Trip,s.Sku,1m,15m,15m))).Status);
        Assert.Equal("SOURCE_ALREADY_USED",(await PostA(client,
            req with {CommandId=Key()})).Outcome.ReasonCode);
        Assert.Equal(after,Fingerprint(A,s));
    }

    [Fact]
    public async Task Wrong_manifest_right_source_order_line_and_tenant_fail_closed()
    {
        using var factory=fixture.CreateFactory();
        using var a=SqlScratchFixture.WithTenant(factory.CreateClient(),A);
        using var b=SqlScratchFixture.WithTenant(factory.CreateClient(),B);
        using var warehouse=SqlScratchFixture.WithTenant(factory.CreateClient(),A,
            roles:["warehouse"]);
        var s=await Prepare(a);
        var other=await Prepare(b,B);
        string before=Fingerprint(A,s), otherBefore=Fingerprint(B,other);
        var req=A25(s);
        using(var unauthorized=await warehouse.PostAsJsonAsync(RootA25,req,
            TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.Forbidden,unauthorized.StatusCode);
        Assert.Equal("TRIP_NOT_ELIGIBLE",(await PostA(b,req)).Outcome.ReasonCode);
        Assert.Equal("A_FROZEN_LOADED_RIGHT_NOT_FOUND",(await PostA(a,
            req with {CommandId=Key(),SourceNumber=Key(),TripManifestLineId=s.BManifestLine})).Outcome.ReasonCode);
        Assert.Equal("A_FROZEN_LOADED_RIGHT_NOT_FOUND",(await PostA(a,
            req with {CommandId=Key(),SourceNumber=Key(),AOrderLineId=Guid.NewGuid()})).Outcome.ReasonCode);
        Assert.Equal("CUSTOMER_SHIPTO_OR_SKU_NOT_ELIGIBLE",(await PostA(a,
            req with {CommandId=Key(),SourceNumber=Key(),ShipToSnapshotId=Guid.NewGuid()})).Outcome.ReasonCode);
        Assert.Equal(before,Fingerprint(A,s));
        Assert.Equal(otherBefore,Fingerprint(B,other));
    }

    [Fact]
    public async Task Parallel_A25_A25_on_one_right_cannot_oversell_A40()
    {
        using var factory=fixture.CreateFactory();
        using var left=SqlScratchFixture.WithTenant(factory.CreateClient(),A);
        using var right=SqlScratchFixture.WithTenant(factory.CreateClient(),A);
        var s=await Prepare(left);
        var results=await Task.WhenAll(PostA(left,A25(s)),PostA(right,A25(s)));
        Assert.Equal(1,results.Count(x=>x.Outcome.Status=="APPLIED"));
        Assert.Equal(1,results.Count(x=>x.Outcome.ReasonCode=="A_LINE_OVERDELIVERY"));
        var state=State(A,s);
        Assert.Equal((45m,450m,15m,1),(state.TripQty,state.TripCost,
            state.AHold,ReadA(A,s).ADeliveries));
    }

    [Fact]
    public async Task Parallel_A25_B20_share_trip_lock_without_losing_cost_or_entitlements()
    {
        using var factory=fixture.CreateFactory();
        using var left=SqlScratchFixture.WithTenant(factory.CreateClient(),A);
        using var right=SqlScratchFixture.WithTenant(factory.CreateClient(),A);
        var s=await Prepare(left);
        var witness=CashWitness(A,s,200m);
        var results=await Task.WhenAll(PostA(left,A25(s)),Post(right,Sale(s,witness)));
        Assert.All(results,r=>Assert.Equal("APPLIED",r.Outcome.Status));
        var stock=State(A,s);var ar=ReadA(A,s);
        Assert.Equal((25m,250m,15m,10m,100m,200m),
            (stock.TripQty,stock.TripCost,stock.AHold,stock.BQuota,
             stock.ArB,stock.Custody));
        Assert.Equal((375m,450m,250m),(ar.ArA,ar.TotalCogs,ar.ACost));
    }

    [Fact]
    public async Task Missing_short_inflated_or_wrong_cost_vehicle_cannot_be_posted()
    {
        using var factory=fixture.CreateFactory();
        using var client=SqlScratchFixture.WithTenant(factory.CreateClient(),A);
        var s=await Prepare(client);
        using var db=Open(A);
        foreach(var pair in new[] {(10m,100m,"VEHICLE_STOCK_INSUFFICIENT"),
                                   (80m,800m,"VEHICLE_STOCK_SOURCE_DRIFT"),
                                   (70m,701m,"VEHICLE_STOCK_SOURCE_DRIFT")})
        {
            db.Execute("""
                UPDATE dbo.PhysicalStockBalance
                SET OnHandBaseQuantity=@Qty,OnHandCostAmount=@Cost
                WHERE TenantId=@Tenant AND StockLocationId=@Vehicle AND SkuId=@Sku
                """,new { Tenant=A,s.Vehicle,s.Sku,Qty=pair.Item1,Cost=pair.Item2 });
            string before=Fingerprint(A,s);
            Assert.Equal(pair.Item3,(await PostA(client,A25(s))).Outcome.ReasonCode);
            Assert.Equal(before,Fingerprint(A,s));
        }
    }

    [Fact]
    public async Task After_effect_trigger_failure_rolls_back_every_A_mutation_and_same_key_retries()
    {
        using var factory=fixture.CreateFactory();
        using var client=SqlScratchFixture.WithTenant(factory.CreateClient(),A);
        var s=await Prepare(client);
        var req=A25(s);string before=Fingerprint(A,s);
        using var db=Open(A);
        db.Execute($"""
            CREATE TRIGGER dbo.TR_App01c3_Fault ON dbo.LedgerEffect
            AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM inserted e
                    JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId
                        AND l.Id=e.BusinessSourceLineId
                    JOIN dbo.BusinessSource src ON src.TenantId=l.TenantId
                        AND src.Id=l.BusinessSourceId
                    WHERE src.SourceKind=N'A_DELIVERY' AND src.TripId='{s.Trip:D}')
                    THROW 51089,'APP01C3 forced after-effect rollback',1;
            END;
            """);
        try
        {
            bool failed;
            try
            {
                using var response=await client.PostAsJsonAsync(RootA25,req,
                    TestContext.Current.CancellationToken);
                failed=!response.IsSuccessStatusCode;
            }
            catch(Exception) {failed=true;}
            Assert.True(failed);
            Assert.Equal(before,Fingerprint(A,s));
            Assert.Equal(0,db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM dbo.BusinessCommand
                WHERE TenantId=@Tenant AND ProducerId=@Producer AND CommandId=@Command
                """,new { Tenant=A,Producer,Command=req.CommandId }));
        }
        finally {db.Execute("DROP TRIGGER IF EXISTS dbo.TR_App01c3_Fault");}
        var applied=await PostA(client,req);
        Assert.Equal("A_PARTIAL_DELIVERY_POSTED",applied.Outcome.OutcomeCode);
        Assert.False(applied.Outcome.Replayed);
        Assert.Equal(1,ReadA(A,s).ADeliveries);
    }

    [Fact]
    public async Task Fractional_A_cost_rounds_to_four_decimals_and_remains_source_backed()
    {
        using var factory=fixture.CreateFactory();
        using var client=SqlScratchFixture.WithTenant(factory.CreateClient(),A);
        var s=await Prepare(client,openingCost:1000.1m);
        Assert.Equal(700.07m,State(A,s).TripCost);
        Assert.Equal("APPLIED",(await PostA(client,A25(s))).Outcome.Status);
        var state=State(A,s);var a=ReadA(A,s);
        Assert.Equal((45m,450.045m,250.025m,375m),
            (state.TripQty,state.TripCost,a.ACost,a.ArA));
    }
}
