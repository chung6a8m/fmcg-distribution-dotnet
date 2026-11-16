using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FmcgDms.Api.Tests;

/// <summary>
/// APP-01E.2: genuine two-transport SQL contention for cross-command races.
/// Each case is isolated per nonce-owned tenant LocalDB, uses independent
/// signed actors and reconciles physical/source/ledger state AFTER COMMIT.
/// E.3 fault matrices and E.4 disconnect/restart remain separate.
/// </summary>
[Collection("SqlScratch")]
public sealed class App01e2CrossCommandRaceTests(SqlScratchFixture fixture)
{
    private const string Producer = "app01e2-races";
    private const string A = "/api/synthetic/app01a/commands/";
    private const string B = "/api/synthetic/app01b/commands/";
    private const string C = "/api/synthetic/app01c/commands/";
    private const string D = "/api/synthetic/app01d/commands/";
    private const string SalesActor = "e2-sales";
    private const string WarehouseActor = "e2-warehouse";
    private const string CashierActor = "e2-cashier";

    private sealed record Scene(Guid Sku, Guid Warehouse, Guid Vehicle, Guid Trip,
        Guid Order, Guid OrderLine, Guid Manifest, Guid Customer, Guid ShipTo,
        Guid AManifestLine, Guid BManifestLine);

    private sealed record Request(string Path, IApp01aCommand Payload,
        string Role, string Actor, string SourceKind, int Lines, int Effects);

    private sealed record Reply(HttpStatusCode HttpStatus, BusinessCommandOutcome Outcome);
    private sealed record RacePair(Reply Left, Reply Right, int ObservedSqlWaiters);

    private sealed class SqlAudit
    {
        public string Status { get; set; } = "";
        public string OutcomeCode { get; set; } = "";
        public string? ReasonCode { get; set; }
        public string CommandActor { get; set; } = "";
        public Guid? SourceId { get; set; }
        public string? SourceKind { get; set; }
        public string? SourceNumber { get; set; }
        public string? SourceActor { get; set; }
        public Guid? SourceTrip { get; set; }
        public int Lines { get; set; }
        public int Effects { get; set; }
    }

    private sealed class SqlNumbers
    {
        public decimal WarehouseQty { get; set; }
        public decimal WarehouseCost { get; set; }
        public decimal VehicleQty { get; set; }
        public decimal VehicleCost { get; set; }
        public decimal PoolAllocated { get; set; }
        public decimal AGranted { get; set; }
        public decimal BGranted { get; set; }
        public decimal AConsumed { get; set; }
        public decimal BConsumed { get; set; }
        public decimal BReleased { get; set; }
        public decimal ArA { get; set; }
        public decimal ArB { get; set; }
        public decimal Cogs { get; set; }
        public decimal Custody { get; set; }
        public decimal Fund { get; set; }
        public decimal ReceiptApplied { get; set; }
        public int ARows { get; set; }
        public int BRows { get; set; }
        public int Offers { get; set; }
        public int Acceptances { get; set; }
        public int Unloads { get; set; }
        public int ManifestsLoaded { get; set; }
        public int InvoiceEligible { get; set; }

        public SortedDictionary<string, decimal> Cells() => new(StringComparer.Ordinal)
        {
            [nameof(WarehouseQty)] = WarehouseQty,
            [nameof(WarehouseCost)] = WarehouseCost,
            [nameof(VehicleQty)] = VehicleQty,
            [nameof(VehicleCost)] = VehicleCost,
            [nameof(PoolAllocated)] = PoolAllocated,
            [nameof(AGranted)] = AGranted,
            [nameof(BGranted)] = BGranted,
            [nameof(AConsumed)] = AConsumed,
            [nameof(BConsumed)] = BConsumed,
            [nameof(BReleased)] = BReleased,
            [nameof(ArA)] = ArA,
            [nameof(ArB)] = ArB,
            [nameof(Cogs)] = Cogs,
            [nameof(Custody)] = Custody,
            [nameof(Fund)] = Fund,
            [nameof(ReceiptApplied)] = ReceiptApplied,
            [nameof(ARows)] = ARows,
            [nameof(BRows)] = BRows,
            [nameof(Offers)] = Offers,
            [nameof(Acceptances)] = Acceptances,
            [nameof(Unloads)] = Unloads,
            [nameof(ManifestsLoaded)] = ManifestsLoaded,
            [nameof(InvoiceEligible)] = InvoiceEligible
        };
    }

    private sealed record SqlSnapshot(string Database, string Tenant, string ManifestStatus,
        string TripStatus, SqlNumbers Numbers, string LedgerSha256);
    private sealed record Cell(string Field, decimal Expected, decimal Actual)
    {
        public decimal Diff => Actual - Expected;
        public bool Match => Diff == 0m;
    }

    private static string Id() => Guid.NewGuid().ToString("N");
    private string Connection(string tenant) => tenant switch
    {
        "tenant-a" => fixture.TenantAConnectionString,
        "tenant-b" => fixture.TenantBConnectionString,
        _ => throw new ArgumentException("Unsupported tenant", nameof(tenant))
    };
    private SqlConnection Open(string tenant) => new(Connection(tenant));

    private static HttpClient Client(TestApiFactory factory, string tenant,
        string actor, string role, DelegatingHandler? handler = null) =>
        SqlScratchFixture.WithTenant(
            handler is null ? factory.CreateClient() : factory.CreateDefaultClient(handler),
            tenant, actor, [role]);

    private static async Task<Reply> Send<T>(HttpClient client, string path, T command)
    {
        using var content = JsonContent.Create(command, command!.GetType());
        using var response = await client.PostAsync(
            path, content, TestContext.Current.CancellationToken);
        BusinessCommandOutcome? outcome =
            await response.Content.ReadFromJsonAsync<BusinessCommandOutcome>(
                TestContext.Current.CancellationToken);
        return new Reply(response.StatusCode, Assert.IsType<BusinessCommandOutcome>(outcome));
    }

    private static async Task<BusinessCommandOutcome> Apply<T>(
        HttpClient client, string path, T command, string code)
    {
        var reply = await Send(client, path, command);
        Assert.Equal(HttpStatusCode.OK, reply.HttpStatus);
        Assert.Equal("APPLIED", reply.Outcome.Status);
        Assert.Equal(code, reply.Outcome.OutcomeCode);
        Assert.NotNull(reply.Outcome.SourceId);
        Assert.False(reply.Outcome.Replayed);
        return reply.Outcome;
    }

    private Scene Seed(string tenant, decimal orderQty = 40m)
    {
        var s = new Scene(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        using var db = Open(tenant);
        db.Execute("""
            INSERT dbo.Customer(Id,TenantId,Code,Name)
                VALUES(@Customer,@Tenant,@CustomerCode,N'APP01E2 Customer');
            INSERT dbo.ShipToSnapshot(Id,TenantId,CustomerId,DisplayName,Address)
                VALUES(@ShipTo,@Tenant,@Customer,N'E2 ShipTo',N'Synthetic');
            INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom)
                VALUES(@Sku,@Tenant,@SkuCode,N'APP01E2 SKU',N'EA');
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
                VALUES(@OrderLine,@Tenant,@Order,1,@Sku,@OrderQty,N'EA',1,15);
            INSERT dbo.AOrderTermsApproval(TenantId,AOrderId,TripId,CustomerId,
                TermsDays,Decision,PolicyVersion,ProposedBy,CheckedBy,
                EvidenceRef,ApprovedAtUtc,EffectiveFromUtc,EffectiveUntilUtc)
                VALUES(@Tenant,@Order,@Trip,@Customer,7,N'APPROVED',N'E2-SYNTHETIC-v1',
                    N'e2-maker',N'e2-checker',N'E2-terms',
                    DATEADD(minute,-1,SYSUTCDATETIME()),
                    DATEADD(day,-1,SYSUTCDATETIME()),DATEADD(day,7,SYSUTCDATETIME()));
            INSERT dbo.BCreditPool(TenantId,TripId,CustomerId,PoolCode,CurrencyUnit,ApprovedLimit)
                VALUES(@Tenant,@Trip,@Customer,N'B',N'SYNTHETIC_UNITLESS',100);
            INSERT dbo.CreditPolicyApproval(Id,TenantId,TripId,CustomerId,PoolCode,
                CurrencyUnit,CaseNumber,PolicyVersion,ProposedBy,CheckedBy,
                Decision,Reason,EvidenceRef,ApprovedLimit,
                EffectiveFromUtc,EffectiveUntilUtc,ReviewedAtUtc)
                VALUES(@Approval,@Tenant,@Trip,@Customer,N'B',N'SYNTHETIC_UNITLESS',
                    @ApprovalCase,N'E2-SYNTHETIC-v1',N'e2-maker',N'e2-checker',
                    N'APPROVED',N'Test only',N'approved-fixture',100,
                    DATEADD(day,-1,SYSUTCDATETIME()),DATEADD(day,1,SYSUTCDATETIME()),
                    SYSUTCDATETIME());
            INSERT dbo.StockPool(TenantId,StockLocationId,SkuId,OnHandBaseQuantity)
                VALUES(@Tenant,@Warehouse,@Sku,100);
            INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,
                SnapshotCode,BaseQuantity,CostAmount)
                VALUES(@Tenant,@Warehouse,@Sku,N'APP01E2-T0',100,1000);
            INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,
                OnHandBaseQuantity,OnHandCostAmount)
                VALUES(@Tenant,@Warehouse,@Sku,100,1000);
            INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,
                SnapshotCode,BaseQuantity,CostAmount)
                VALUES(@Tenant,@Vehicle,@Sku,N'APP01E2-T0',0,0);
            INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,
                OnHandBaseQuantity,OnHandCostAmount)
                VALUES(@Tenant,@Vehicle,@Sku,0,0);
            """, new {
                Tenant = tenant, s.Customer, s.ShipTo, s.Sku, s.Warehouse, s.Vehicle,
                s.Trip, s.Order, s.OrderLine, OrderQty = orderQty,
                CustomerCode = Id(), SkuCode = Id(), WhCode = Id(), VehicleCode = Id(),
                TripCode = Id(), OrderCode = Id(), Approval = Guid.NewGuid(),
                ApprovalCase = Id()
            });
        return s;
    }

    private async Task Prepare(HttpClient client, string tenant, Scene s, bool load)
    {
        await Apply(client, A + "reserve-a-stock", new ReserveAStockCommand(
            Producer, Id(), Id(), s.Order, s.OrderLine, s.Warehouse, 40m), "A_STOCK_RESERVED");
        await Apply(client, A + "grant-b-quota", new GrantBQuotaCommand(
            Producer, Id(), Id(), s.Trip, s.Warehouse, s.Sku, 30m), "B_QUOTA_GRANTED");
        await Apply(client, A + "confirm-a-order", new ConfirmAOrderCommand(
            Producer, Id(), Id(), s.Order), "A_ORDER_CONFIRMED");
        await Apply(client, A + "grant-b-credit", new GrantBCreditCommand(
            Producer, Id(), Id(), s.Trip, s.Customer, s.ShipTo,
            "B", "SYNTHETIC_UNITLESS", 100m), "B_CREDIT_GRANTED");

        using (var db = Open(tenant))
        {
            var rightA = db.QuerySingle<Guid>("""
                SELECT Id FROM dbo.StockRight
                WHERE TenantId=@Tenant AND TripId=@Trip AND RightKind='A'
                """, new { Tenant = tenant, s.Trip });
            var rightB = db.QuerySingle<Guid>("""
                SELECT Id FROM dbo.StockRight
                WHERE TenantId=@Tenant AND TripId=@Trip AND RightKind='B'
                """, new { Tenant = tenant, s.Trip });
            db.Execute("""
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
                """, new {
                    Tenant = tenant, s.Manifest, s.Trip, s.Warehouse, s.Vehicle, s.Sku,
                    ALine = s.AManifestLine, BLine = s.BManifestLine,
                    RightA = rightA, RightB = rightB, Number = Id()
                });
        }
        if (load)
            await Apply(client, B + "load-manifest",
                new LoadManifestCommand(Producer, Id(), Id(), s.Manifest), "MANIFEST_LOADED");
    }

    private string CashWitness(string tenant, Scene s)
    {
        string reference = Id();
        using var db = Open(tenant);
        db.Execute("""
            INSERT dbo.VerifiedCashEvidence(TenantId,EvidenceRef,TripId,CustomerId,
                ShipToSnapshotId,PaymentMethod,VerificationStatus,VerifiedAmount,
                VerifiedBy,VerifiedAtUtc)
            VALUES(@Tenant,@Ref,@Trip,@Customer,@ShipTo,N'CASH',N'VERIFIED',200,
                N'e2-cash-checker',SYSUTCDATETIME())
            """, new { Tenant = tenant, Ref = reference, s.Trip, s.Customer, s.ShipTo });
        return reference;
    }

    private PostBSaleDeliveryReceiptCommand B20(Scene s, string evidence) =>
        new(Producer, Id(), Id(), s.Trip, s.Customer, s.ShipTo,
            s.BManifestLine, s.Sku, 20m, 15m, 200m, evidence);

    private PostAPartialDeliveryCommand A25(Scene s) =>
        new(Producer, Id(), Id(), s.Trip, s.Customer, s.ShipTo,
            s.Order, s.OrderLine, s.AManifestLine, s.Sku, 25m);

    private UnloadTripRemainderCommand Unload(Scene s) =>
        new(Producer, Id(), Id(), s.Trip, s.Manifest);

    private async Task<(Guid Offer, string CountRef)> SeedCashier(HttpClient client,
        string tenant, Scene s)
    {
        await Apply(client, C + "post-b-sale-delivery-receipt",
            B20(s, CashWitness(tenant, s)), "B_SALE_DELIVERY_POSTED");
        var offer = await Apply(client, D + "offer-cash-handover",
            new OfferCashHandoverCommand(Producer, Id(), Id(), s.Trip, 200m),
            "CASH_HANDOVER_OFFERED");
        string countRef = Id();
        using var db = Open(tenant);
        db.Execute("""
            INSERT dbo.CashierCountEvidence(TenantId,EvidenceRef,TripId,
                CountedAmount,CountedBy,VerificationStatus,CountedAtUtc)
            VALUES(@Tenant,@CountRef,@Trip,200,@Cashier,N'VERIFIED',SYSUTCDATETIME())
            """, new { Tenant = tenant, CountRef = countRef,
                s.Trip, Cashier = CashierActor });
        return (Assert.IsType<Guid>(offer.SourceId), countRef);
    }

    private static Request Req(string path, IApp01aCommand command, string role,
        string actor, string kind, int lines, int effects) =>
        new(path, command, role, actor, kind, lines, effects);

    private static async Task<RacePair> Race(TestApiFactory factory, string tenant,
        App01e2SqlContentionGate sqlGate, Request left, Request right)
    {
        var admission = new TwoClientReleaseGate();
        using var a = Client(factory, tenant, left.Actor, left.Role,
            new TwoClientGateHandler(admission));
        using var b = Client(factory, tenant, right.Actor, right.Role,
            new TwoClientGateHandler(admission));

        Task<Reply> first = Send(a, left.Path, left.Payload);
        Task<Reply> second = Send(b, right.Path, right.Payload);
        int waiterCount = 0;
        try
        {
            await admission.BothReady.WaitAsync(
                TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.Equal(2, admission.Arrived);
            admission.Release();
            waiterCount = await sqlGate.WaitForBothSqlWaitersAsync(
                TestContext.Current.CancellationToken);
            Assert.False(first.IsCompleted, "First command committed while gate was held.");
            Assert.False(second.IsCompleted, "Second command committed while gate was held.");
        }
        finally
        {
            admission.Release();
            await sqlGate.ReleaseAsync();
        }
        var replies = await Task.WhenAll(first, second);
        return new RacePair(replies[0], replies[1], waiterCount);
    }

    private SqlAudit Audit(string tenant, IApp01aCommand command)
    {
        using var db = Open(tenant);
        return db.QuerySingle<SqlAudit>("""
            SELECT c.Status,c.OutcomeCode,c.ReasonCode,c.ActorId AS CommandActor,
                s.Id AS SourceId,s.SourceKind,s.SourceNumber,s.ActorId AS SourceActor,
                s.TripId AS SourceTrip,
                (SELECT COUNT(*) FROM dbo.BusinessSourceLine sl
                    WHERE sl.TenantId=c.TenantId AND sl.BusinessSourceId=s.Id) AS Lines,
                (SELECT COUNT(*) FROM dbo.LedgerEffect e
                    JOIN dbo.BusinessSourceLine sl
                        ON sl.TenantId=e.TenantId AND sl.Id=e.BusinessSourceLineId
                    WHERE sl.TenantId=c.TenantId AND sl.BusinessSourceId=s.Id) AS Effects
            FROM dbo.BusinessCommand c
            LEFT JOIN dbo.BusinessSource s ON s.TenantId=c.TenantId
                AND s.BusinessCommandId=c.Id
            WHERE c.TenantId=@Tenant AND c.ProducerId=@Producer AND c.CommandId=@Command
            """, new { Tenant = tenant, Producer, Command = command.CommandId });
    }

    private void AssertCommandFromSql(string tenant, Scene s, Request request, Reply reply,
        SqlAudit audit)
    {
        Assert.Equal(HttpStatusCode.OK, reply.HttpStatus);
        Assert.False(reply.Outcome.Replayed);
        Assert.Equal(reply.Outcome.Status, audit.Status);
        Assert.Equal(reply.Outcome.OutcomeCode, audit.OutcomeCode);
        Assert.Equal(reply.Outcome.ReasonCode, audit.ReasonCode);
        Assert.Equal(request.Actor, audit.CommandActor);
        if (audit.Status == "APPLIED")
        {
            Assert.NotNull(reply.Outcome.SourceId);
            Assert.Equal(reply.Outcome.SourceId, audit.SourceId);
            Assert.Equal(request.SourceKind, audit.SourceKind);
            Assert.Equal(request.Payload.SourceNumber, audit.SourceNumber);
            Assert.Equal(request.Actor, audit.SourceActor);
            Assert.Equal<Guid?>(s.Trip, audit.SourceTrip);
            Assert.Equal(request.Lines, audit.Lines);
            Assert.Equal(request.Effects, audit.Effects);
        }
        else
        {
            Assert.Equal("REJECTED", audit.Status);
            Assert.Equal("REJECTED", audit.OutcomeCode);
            Assert.Null(reply.Outcome.SourceId);
            Assert.Null(audit.SourceId);
            Assert.Equal(0, audit.Lines);
            Assert.Equal(0, audit.Effects);
        }

        // A successful command on this physical tenant cannot materialize in
        // another tenant's database, even with exactly the same producer/id.
        string other = tenant == "tenant-a" ? "tenant-b" : "tenant-a";
        using var db = Open(other);
        Assert.Equal(0, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.BusinessCommand
            WHERE TenantId=@Tenant AND ProducerId=@Producer AND CommandId=@Command
            """, new { Tenant = other, Producer, Command = request.Payload.CommandId }));
    }

    private SqlSnapshot ReadSql(string tenant, Scene s)
    {
        using var db = Open(tenant);
        var p = new { Tenant = tenant, s.Trip, s.Sku, s.Warehouse, s.Vehicle, s.Manifest };
        string name = db.QuerySingle<string>("SELECT DB_NAME()");
        var nums = db.QuerySingle<SqlNumbers>("""
            SELECT
              (SELECT OnHandBaseQuantity FROM dbo.PhysicalStockBalance
                 WHERE TenantId=@Tenant AND StockLocationId=@Warehouse AND SkuId=@Sku) AS WarehouseQty,
              (SELECT OnHandCostAmount FROM dbo.PhysicalStockBalance
                 WHERE TenantId=@Tenant AND StockLocationId=@Warehouse AND SkuId=@Sku) AS WarehouseCost,
              (SELECT OnHandBaseQuantity FROM dbo.PhysicalStockBalance
                 WHERE TenantId=@Tenant AND StockLocationId=@Vehicle AND SkuId=@Sku) AS VehicleQty,
              (SELECT OnHandCostAmount FROM dbo.PhysicalStockBalance
                 WHERE TenantId=@Tenant AND StockLocationId=@Vehicle AND SkuId=@Sku) AS VehicleCost,
              (SELECT AllocatedBaseQuantity FROM dbo.StockPool
                 WHERE TenantId=@Tenant AND StockLocationId=@Warehouse AND SkuId=@Sku) AS PoolAllocated,
              (SELECT COALESCE(SUM(GrantedBaseQuantity),0) FROM dbo.StockRight
                 WHERE TenantId=@Tenant AND TripId=@Trip AND RightKind='A') AS AGranted,
              (SELECT COALESCE(SUM(GrantedBaseQuantity),0) FROM dbo.StockRight
                 WHERE TenantId=@Tenant AND TripId=@Trip AND RightKind='B') AS BGranted,
              (SELECT COALESCE(SUM(ConsumedBaseQuantity),0) FROM dbo.StockRight
                 WHERE TenantId=@Tenant AND TripId=@Trip AND RightKind='A') AS AConsumed,
              (SELECT COALESCE(SUM(ConsumedBaseQuantity),0) FROM dbo.StockRight
                 WHERE TenantId=@Tenant AND TripId=@Trip AND RightKind='B') AS BConsumed,
              (SELECT COALESCE(SUM(ReleasedBaseQuantity),0) FROM dbo.StockRight
                 WHERE TenantId=@Tenant AND TripId=@Trip AND RightKind='B') AS BReleased,
              (SELECT COALESCE(SUM(AmountDelta),0)
                 FROM dbo.LedgerEffect e
                 JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId
                   AND l.Id=e.BusinessSourceLineId
                 JOIN dbo.BusinessSource src ON src.TenantId=l.TenantId
                   AND src.Id=l.BusinessSourceId
                 WHERE src.TenantId=@Tenant AND src.TripId=@Trip
                   AND e.LedgerKind='AR' AND src.SourceKind='A_DELIVERY') AS ArA,
              (SELECT COALESCE(SUM(AmountDelta),0)
                 FROM dbo.LedgerEffect e
                 JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId
                   AND l.Id=e.BusinessSourceLineId
                 JOIN dbo.BusinessSource src ON src.TenantId=l.TenantId
                   AND src.Id=l.BusinessSourceId
                 WHERE src.TenantId=@Tenant AND src.TripId=@Trip
                   AND e.LedgerKind='AR' AND src.SourceKind='B_SALE_DELIVERY') AS ArB,
              (SELECT COALESCE(SUM(e.AmountDelta),0) FROM dbo.LedgerEffect e
                 JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId
                   AND l.Id=e.BusinessSourceLineId
                 JOIN dbo.BusinessSource src ON src.TenantId=l.TenantId
                   AND src.Id=l.BusinessSourceId
                 WHERE src.TenantId=@Tenant AND src.TripId=@Trip
                   AND e.LedgerKind='COGS') AS Cogs,
              (SELECT COALESCE(SUM(e.AmountDelta),0) FROM dbo.LedgerEffect e
                 JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId
                   AND l.Id=e.BusinessSourceLineId
                 JOIN dbo.BusinessSource src ON src.TenantId=l.TenantId
                   AND src.Id=l.BusinessSourceId
                 WHERE src.TenantId=@Tenant AND src.TripId=@Trip
                   AND e.LedgerKind='CUSTODY') AS Custody,
              (SELECT COALESCE(SUM(e.AmountDelta),0) FROM dbo.LedgerEffect e
                 JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId
                   AND l.Id=e.BusinessSourceLineId
                 JOIN dbo.BusinessSource src ON src.TenantId=l.TenantId
                   AND src.Id=l.BusinessSourceId
                 WHERE src.TenantId=@Tenant AND src.TripId=@Trip
                   AND e.LedgerKind='FUND') AS Fund,
              (SELECT COALESCE(SUM(a.AppliedAmount),0) FROM dbo.ReceiptAllocation a
                 JOIN dbo.CashReceipt r ON r.TenantId=a.TenantId AND r.Id=a.CashReceiptId
                 WHERE r.TenantId=@Tenant AND r.TripId=@Trip) AS ReceiptApplied,
              (SELECT COUNT(*) FROM dbo.SaleDelivery
                 WHERE TenantId=@Tenant AND TripId=@Trip AND RightKind='A') AS ARows,
              (SELECT COUNT(*) FROM dbo.SaleDelivery
                 WHERE TenantId=@Tenant AND TripId=@Trip AND RightKind='B') AS BRows,
              (SELECT COUNT(*) FROM dbo.CashHandoverOffer
                 WHERE TenantId=@Tenant AND TripId=@Trip) AS Offers,
              (SELECT COUNT(*) FROM dbo.CashHandoverAcceptance
                 WHERE TenantId=@Tenant AND TripId=@Trip) AS Acceptances,
              (SELECT COUNT(*) FROM dbo.TripUnload
                 WHERE TenantId=@Tenant AND TripId=@Trip) AS Unloads,
              (SELECT COUNT(*) FROM dbo.TripManifest
                 WHERE TenantId=@Tenant AND TripId=@Trip AND Status='LOADED') AS ManifestsLoaded,
              (SELECT COUNT(*) FROM dbo.SaleDelivery
                 WHERE TenantId=@Tenant AND TripId=@Trip AND InvoiceEligible=1) AS InvoiceEligible
            """, p);
        string manifest = db.QuerySingleOrDefault<string>("""
            SELECT Status FROM dbo.TripManifest WHERE TenantId=@Tenant AND Id=@Manifest
            """, p) ?? "NONE";
        string tripStatus = db.QuerySingle<string>(
            "SELECT Status FROM dbo.Trip WHERE TenantId=@Tenant AND Id=@Trip", p);
        var ledger = db.Query<(Guid SourceId, string Kind, string Number, Guid EffectId,
            string EffectKey, string LedgerKind, decimal Quantity, decimal Amount)>("""
            SELECT src.Id AS SourceId,src.SourceKind AS Kind,src.SourceNumber AS Number,
                e.Id AS EffectId,e.EffectKey,e.LedgerKind,e.QuantityDelta AS Quantity,
                e.AmountDelta AS Amount
            FROM dbo.BusinessSource src
            JOIN dbo.BusinessSourceLine l ON l.TenantId=src.TenantId
                AND l.BusinessSourceId=src.Id
            JOIN dbo.LedgerEffect e ON e.TenantId=l.TenantId
                AND e.BusinessSourceLineId=l.Id
            WHERE src.TenantId=@Tenant AND src.TripId=@Trip
            ORDER BY src.SourceKind,src.SourceNumber,l.LineNumber,e.EffectKey,e.Id
            """, p).ToArray();
        return new SqlSnapshot(name, tenant, manifest, tripStatus, nums,
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(ledger))));
    }

    private static SortedDictionary<string, decimal> Expected(string scenario,
        RacePair pair)
    {
        bool l = pair.Left.Outcome.Status == "APPLIED";
        bool r = pair.Right.Outcome.Status == "APPLIED";
        decimal aGrant = scenario == "rights-pool" ? (l ? 70m : 0m) : 40m;
        decimal bGrant = scenario == "rights-pool" ? (r ? 50m : 0m) : 30m;
        decimal aDelivered = scenario switch
        {
            "a25-vs-b20" or "a25-vs-unload" when l => 25m,
            _ => 0m
        };
        decimal bDelivered = scenario switch
        {
            "manifest-vs-b20" or "a25-vs-b20" when r => 20m,
            "b20-vs-unload" when l => 20m,
            "cashier-accept-vs-unload" or "cashier-accept-vs-accept"
                or "cashier-offer-vs-accept" => 20m,
            _ => 0m
        };
        bool unload = scenario switch
        {
            "a25-vs-unload" or "b20-vs-unload" or "cashier-accept-vs-unload" => r,
            _ => false
        };
        bool accepted = scenario switch
        {
            "cashier-accept-vs-unload" => l,
            "cashier-accept-vs-accept" => l || r,
            "cashier-offer-vs-accept" => r,
            _ => false
        };
        int offers = scenario switch
        {
            "cashier-accept-vs-unload" or "cashier-accept-vs-accept" => 1,
            "cashier-offer-vs-accept" => 1 + (l ? 1 : 0),
            _ => 0
        };
        decimal bReleased = unload ? bGrant - bDelivered : 0m;
        bool loaded = scenario != "rights-pool";
        decimal returned = unload ? (aGrant - aDelivered) + (bGrant - bDelivered) : 0m;
        decimal mainQty = loaded ? 100m - aGrant - bGrant + returned : 100m;
        decimal vehicleQty = loaded ? aGrant + bGrant - aDelivered - bDelivered - returned : 0m;
        return new SqlNumbers
        {
            WarehouseQty = mainQty, WarehouseCost = mainQty * 10m,
            VehicleQty = vehicleQty, VehicleCost = vehicleQty * 10m,
            PoolAllocated = aGrant + bGrant - bReleased,
            AGranted = aGrant, BGranted = bGrant,
            AConsumed = aDelivered, BConsumed = bDelivered, BReleased = bReleased,
            ArA = aDelivered * 15m, ArB = bDelivered * 5m,
            Cogs = (aDelivered + bDelivered) * 10m,
            Custody = bDelivered * 10m - (accepted ? 200m : 0m),
            Fund = accepted ? 200m : 0m, ReceiptApplied = bDelivered * 10m,
            ARows = aDelivered > 0 ? 1 : 0, BRows = bDelivered > 0 ? 1 : 0,
            Offers = offers, Acceptances = accepted ? 1 : 0, Unloads = unload ? 1 : 0,
            ManifestsLoaded = loaded ? 1 : 0, InvoiceEligible = 0
        }.Cells();
    }

    private static void CheckPermittedOutcomes(string scenario, RacePair pair)
    {
        var a = pair.Left.Outcome;
        var b = pair.Right.Outcome;
        static void Applied(BusinessCommandOutcome x, string code)
        {
            Assert.Equal("APPLIED", x.Status);
            Assert.Equal(code, x.OutcomeCode);
        }
        static void Rejected(BusinessCommandOutcome x, params string[] reasons)
        {
            Assert.Equal("REJECTED", x.Status);
            Assert.Contains(x.ReasonCode, reasons);
        }
        switch (scenario)
        {
            case "rights-pool":
                Assert.Equal(1, new[] { a, b }.Count(x => x.Status == "APPLIED"));
                if (a.Status == "APPLIED")
                {
                    Applied(a, "A_STOCK_RESERVED");
                    Rejected(b, "STOCK_INSUFFICIENT");
                }
                else
                {
                    Applied(b, "B_QUOTA_GRANTED");
                    Rejected(a, "STOCK_INSUFFICIENT");
                }
                break;
            case "manifest-vs-b20":
                Applied(a, "MANIFEST_LOADED");
                if (b.Status == "APPLIED") Applied(b, "B_SALE_DELIVERY_POSTED");
                else Rejected(b, "B_LOADED_RIGHT_NOT_FOUND");
                break;
            case "a25-vs-b20":
                Applied(a, "A_PARTIAL_DELIVERY_POSTED");
                Applied(b, "B_SALE_DELIVERY_POSTED");
                break;
            case "a25-vs-unload":
                Applied(b, "TRIP_REMAINDER_UNLOADED");
                if (a.Status == "APPLIED") Applied(a, "A_PARTIAL_DELIVERY_POSTED");
                else Rejected(a, "TRIP_NOT_ELIGIBLE");
                break;
            case "b20-vs-unload":
                Assert.Equal(1, new[] { a, b }.Count(x => x.Status == "APPLIED"));
                if (a.Status == "APPLIED")
                {
                    Applied(a, "B_SALE_DELIVERY_POSTED");
                    Rejected(b, "CUSTODY_NOT_SETTLED");
                }
                else
                {
                    Applied(b, "TRIP_REMAINDER_UNLOADED");
                    Rejected(a, "TRIP_NOT_ELIGIBLE");
                }
                break;
            case "cashier-accept-vs-unload":
                Applied(a, "CASH_HANDOVER_ACCEPTED");
                if (b.Status == "APPLIED") Applied(b, "TRIP_REMAINDER_UNLOADED");
                else Rejected(b, "CUSTODY_NOT_SETTLED");
                break;
            case "cashier-accept-vs-accept":
                Assert.Equal(1, new[] { a, b }.Count(x => x.Status == "APPLIED"));
                if (a.Status == "APPLIED")
                {
                    Applied(a, "CASH_HANDOVER_ACCEPTED");
                    Rejected(b, "OFFER_ALREADY_ACCEPTED");
                }
                else
                {
                    Applied(b, "CASH_HANDOVER_ACCEPTED");
                    Rejected(a, "OFFER_ALREADY_ACCEPTED");
                }
                break;
            case "cashier-offer-vs-accept":
                Applied(b, "CASH_HANDOVER_ACCEPTED");
                if (a.Status == "APPLIED") Applied(a, "CASH_HANDOVER_OFFERED");
                else Rejected(a, "CUSTODY_INSUFFICIENT");
                break;
            default: throw new InvalidOperationException("Unknown APP-01E.2 scenario.");
        }
    }

    private static void AssertState(string scenario, RacePair pair,
        SqlSnapshot snapshot, SortedDictionary<string, decimal> expected)
    {
        var actual = snapshot.Numbers.Cells();
        Assert.Equal(23, actual.Count);
        Assert.Equal(expected.Keys, actual.Keys);
        foreach (var (field, value) in expected)
            Assert.Equal(value, actual[field]);
        if (snapshot.Numbers.Unloads == 1)
            Assert.Equal("RECONCILING", snapshot.TripStatus);
        else
            Assert.Contains(snapshot.TripStatus, new[] { "PREPARED", "IN_PROGRESS" });
        Assert.Equal(scenario == "rights-pool" ? "NONE" : "LOADED",
            snapshot.ManifestStatus);
        Assert.Equal(100m,
            actual["WarehouseQty"] + actual["VehicleQty"] +
            actual["AConsumed"] + actual["BConsumed"]);
        Assert.Equal(1000m,
            actual["WarehouseCost"] + actual["VehicleCost"] + actual["Cogs"]);
        Assert.True(actual["AConsumed"] <= actual["AGranted"]);
        Assert.True(actual["BConsumed"] + actual["BReleased"] <= actual["BGranted"]);
        Assert.True(actual["PoolAllocated"] <= 100m);
        Assert.True(pair.ObservedSqlWaiters >= 2);
    }

    private static void WriteEvidence(string tenant, string scenario,
        RacePair pair, SqlSnapshot sql, Request left, Request right,
        SqlAudit a, SqlAudit b, SortedDictionary<string, decimal> expected)
    {
        var actual = sql.Numbers.Cells();
        var cells = expected.Select(kv => new Cell(kv.Key, kv.Value, actual[kv.Key])).ToArray();
        var report = new
        {
            Schema = "APP01E2_RACE_V1",
            TestRunId = Environment.GetEnvironmentVariable("APP01E2_TEST_RUN_ID")
                ?? "LOCAL_UNTRACKED",
            SourceHeadSha = Environment.GetEnvironmentVariable("APP01E2_SOURCE_HEAD_SHA"),
            WorkflowSha = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            TenantId = tenant, Scenario = scenario,
            Sql = sql, ObservedSqlWaiters = pair.ObservedSqlWaiters,
            Left = new { left.Payload.ProducerId, left.Payload.CommandId,
                left.Payload.SourceNumber, left.SourceKind, left.Actor,
                Http = (int)pair.Left.HttpStatus, pair.Left.Outcome, Audit = a },
            Right = new { right.Payload.ProducerId, right.Payload.CommandId,
                right.Payload.SourceNumber, right.SourceKind, right.Actor,
                Http = (int)pair.Right.HttpStatus, pair.Right.Outcome, Audit = b },
            ExpectedCells = cells.Length, MatchedCells = cells.Count(x => x.Match),
            Cells = cells,
            Status = pair.ObservedSqlWaiters >= 2 && cells.All(x => x.Match) ? "PASS" : "FAIL"
        };
        var directory = Path.Combine(AppContext.BaseDirectory, "TestResults", "APP-01E2");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{tenant}-{scenario}-race.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    [Fact]
    public async Task Contention_gate_refuses_non_scratch_and_cross_tenant_databases()
    {
        var trip = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        var sku = Guid.NewGuid();
        var master = new SqlConnectionStringBuilder(Connection("tenant-a"))
        {
            InitialCatalog = "master"
        };
        var notLocalDb = new SqlConnectionStringBuilder(Connection("tenant-a"))
        {
            DataSource = "localhost"
        };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var gate = await App01e2SqlContentionGate.HoldAsync(
                master.ConnectionString, "tenant-a", trip, warehouse, sku, "trip-app");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var gate = await App01e2SqlContentionGate.HoldAsync(
                notLocalDb.ConnectionString, "tenant-a", trip, warehouse, sku, "trip-app");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var gate = await App01e2SqlContentionGate.HoldAsync(
                Connection("tenant-a"), "tenant-b", trip, warehouse, sku, "trip-app");
        });
    }

    [Theory]
    [InlineData("tenant-a", "rights-pool")]
    [InlineData("tenant-b", "rights-pool")]
    [InlineData("tenant-a", "manifest-vs-b20")]
    [InlineData("tenant-b", "manifest-vs-b20")]
    [InlineData("tenant-a", "a25-vs-b20")]
    [InlineData("tenant-b", "a25-vs-b20")]
    [InlineData("tenant-a", "a25-vs-unload")]
    [InlineData("tenant-b", "a25-vs-unload")]
    [InlineData("tenant-a", "b20-vs-unload")]
    [InlineData("tenant-b", "b20-vs-unload")]
    [InlineData("tenant-a", "cashier-accept-vs-unload")]
    [InlineData("tenant-b", "cashier-accept-vs-unload")]
    [InlineData("tenant-a", "cashier-accept-vs-accept")]
    [InlineData("tenant-b", "cashier-accept-vs-accept")]
    [InlineData("tenant-a", "cashier-offer-vs-accept")]
    [InlineData("tenant-b", "cashier-offer-vs-accept")]
    public async Task Two_clients_contend_on_real_SQL_resources_and_reconcile_every_source(
        string tenant, string scenario)
    {
        using var factory = fixture.CreateFactory();
        using var setup = SqlScratchFixture.WithTenant(factory.CreateClient(), tenant);
        Scene s = Seed(tenant, scenario == "rights-pool" ? 70m : 40m);
        if (scenario != "rights-pool")
            await Prepare(setup, tenant, s, load: scenario != "manifest-vs-b20");

        string? witness = scenario is "manifest-vs-b20" or "a25-vs-b20" or
            "b20-vs-unload" ? CashWitness(tenant, s) : null;
        Guid offer = Guid.Empty;
        string count = "";
        if (scenario.StartsWith("cashier-", StringComparison.Ordinal))
            (offer, count) = await SeedCashier(setup, tenant, s);

        Request left;
        Request right;
        string lockKind = "trip-app";
        switch (scenario)
        {
            case "rights-pool":
                lockKind = "stock-pool";
                left = Req(A + "reserve-a-stock",
                    new ReserveAStockCommand(Producer, Id(), Id(),
                        s.Order, s.OrderLine, s.Warehouse, 70m),
                    "warehouse", WarehouseActor, "STOCK_ALLOC", 1, 1);
                right = Req(A + "grant-b-quota",
                    new GrantBQuotaCommand(Producer, Id(), Id(),
                        s.Trip, s.Warehouse, s.Sku, 50m),
                    "warehouse", WarehouseActor, "STOCK_ALLOC", 1, 1);
                break;
            case "manifest-vs-b20":
                lockKind = "trip-row";
                left = Req(B + "load-manifest",
                    new LoadManifestCommand(Producer, Id(), Id(), s.Manifest),
                    "warehouse", WarehouseActor, "MANIFEST_LOAD", 2, 4);
                right = Req(C + "post-b-sale-delivery-receipt",
                    B20(s, Assert.IsType<string>(witness)),
                    "sales", SalesActor, "B_SALE_DELIVERY", 1, 7);
                break;
            case "a25-vs-b20":
                left = Req(C + "post-a-partial-delivery", A25(s),
                    "sales", SalesActor, "A_DELIVERY", 1, 4);
                right = Req(C + "post-b-sale-delivery-receipt",
                    B20(s, Assert.IsType<string>(witness)),
                    "sales", SalesActor, "B_SALE_DELIVERY", 1, 7);
                break;
            case "a25-vs-unload":
                left = Req(C + "post-a-partial-delivery", A25(s),
                    "sales", SalesActor, "A_DELIVERY", 1, 4);
                right = Req(D + "unload-trip-remainder", Unload(s),
                    "warehouse", WarehouseActor, "TRIP_UNLOAD", 2, 5);
                break;
            case "b20-vs-unload":
                left = Req(C + "post-b-sale-delivery-receipt",
                    B20(s, Assert.IsType<string>(witness)),
                    "sales", SalesActor, "B_SALE_DELIVERY", 1, 7);
                right = Req(D + "unload-trip-remainder", Unload(s),
                    "warehouse", WarehouseActor, "TRIP_UNLOAD", 2, 5);
                break;
            case "cashier-accept-vs-unload":
                left = Req(D + "accept-cash-handover",
                    new AcceptCashHandoverCommand(Producer, Id(), Id(), s.Trip, offer, count),
                    "cashier", CashierActor, "CASH_HANDOVER_ACCEPT", 1, 2);
                right = Req(D + "unload-trip-remainder", Unload(s),
                    "warehouse", WarehouseActor, "TRIP_UNLOAD", 2, 5);
                break;
            case "cashier-accept-vs-accept":
                left = Req(D + "accept-cash-handover",
                    new AcceptCashHandoverCommand(Producer, Id(), Id(), s.Trip, offer, count),
                    "cashier", CashierActor, "CASH_HANDOVER_ACCEPT", 1, 2);
                right = Req(D + "accept-cash-handover",
                    new AcceptCashHandoverCommand(Producer, Id(), Id(), s.Trip, offer, count),
                    "cashier", CashierActor, "CASH_HANDOVER_ACCEPT", 1, 2);
                break;
            case "cashier-offer-vs-accept":
                left = Req(D + "offer-cash-handover",
                    new OfferCashHandoverCommand(Producer, Id(), Id(), s.Trip, 200m),
                    "sales", SalesActor, "CASH_HANDOVER_OFFER", 1, 0);
                right = Req(D + "accept-cash-handover",
                    new AcceptCashHandoverCommand(Producer, Id(), Id(), s.Trip, offer, count),
                    "cashier", CashierActor, "CASH_HANDOVER_ACCEPT", 1, 2);
                break;
            default: throw new InvalidOperationException("Unknown E.2 scenario.");
        }

        await using var sqlGate = await App01e2SqlContentionGate.HoldAsync(
            Connection(tenant), tenant, s.Trip, s.Warehouse, s.Sku, lockKind);
        RacePair pair = await Race(factory, tenant, sqlGate, left, right);
        CheckPermittedOutcomes(scenario, pair);

        var leftAudit = Audit(tenant, left.Payload);
        var rightAudit = Audit(tenant, right.Payload);
        AssertCommandFromSql(tenant, s, left, pair.Left, leftAudit);
        AssertCommandFromSql(tenant, s, right, pair.Right, rightAudit);

        SqlSnapshot actual = ReadSql(tenant, s);
        var expected = Expected(scenario, pair);
        AssertState(scenario, pair, actual, expected);

        // Durable same-identity replays must not change any persisted source,
        // financial posting, right, physical state or full-trip ledger digest.
        using var replayLeft = Client(factory, tenant, left.Actor, left.Role);
        using var replayRight = Client(factory, tenant, right.Actor, right.Role);
        var replayA = await Send(replayLeft, left.Path, left.Payload);
        var replayB = await Send(replayRight, right.Path, right.Payload);
        Assert.True(replayA.Outcome.Replayed);
        Assert.True(replayB.Outcome.Replayed);
        Assert.Equal(pair.Left.Outcome, replayA.Outcome with { Replayed = false });
        Assert.Equal(pair.Right.Outcome, replayB.Outcome with { Replayed = false });
        SqlSnapshot afterReplay = ReadSql(tenant, s);
        Assert.Equal(actual.LedgerSha256, afterReplay.LedgerSha256);
        Assert.Equal(actual.Numbers.Cells().ToArray(), afterReplay.Numbers.Cells().ToArray());
        Assert.Equal(actual.TripStatus, afterReplay.TripStatus);
        Assert.Equal(actual.ManifestStatus, afterReplay.ManifestStatus);

        WriteEvidence(tenant, scenario, pair, actual,
            left, right, leftAudit, rightAudit, expected);
    }
}
