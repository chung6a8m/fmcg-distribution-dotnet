using System.Net;
using System.Net.Http.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FmcgDms.Api.Tests;

/// <summary>
/// APP-01B.2: real SQL Server LocalDB, two nonce-owned tenants, HTTP JWT roles,
/// Dapper SERIALIZABLE load and B31 non-posting quota rejection.
/// T0-T2 39/39 golden-oracle comparison remains APP-01B.3 work.
/// </summary>
[Collection("SqlScratch")]
public sealed class App01b2ManifestCommandTests(SqlScratchFixture fixture)
{
    private const string A = "tenant-a";
    private const string B = "tenant-b";
    private const string Producer = "app01b2-tests";
    private const string RootA = "/api/synthetic/app01a/commands/";
    private const string RootB = "/api/synthetic/app01b/commands/";

    private sealed record Scene(
        Guid Sku, Guid Warehouse, Guid Vehicle, Guid Trip, Guid Order,
        Guid OrderLine, Guid Manifest, Guid ARight, Guid BRight);

    private static string Key() => Guid.NewGuid().ToString("N");
    private SqlConnection Open(string tenant) =>
        new(tenant == A ? fixture.TenantAConnectionString : fixture.TenantBConnectionString);

    private static async Task<(HttpStatusCode Status, BusinessCommandOutcome Outcome)> Send<T>(
        HttpClient client, string path, T command)
    {
        using var response = await client.PostAsJsonAsync(
            path, command, TestContext.Current.CancellationToken);
        var result = await response.Content.ReadFromJsonAsync<BusinessCommandOutcome>(
            TestContext.Current.CancellationToken);
        return (response.StatusCode, Assert.IsType<BusinessCommandOutcome>(result));
    }

    private static LoadManifestCommand Load(Scene s, string? command = null,
        string? source = null) =>
        new(Producer, command ?? Key(), source ?? Key(), s.Manifest);

    private static PreflightBSaleCommand Offer(Scene s, decimal quantity = 31m,
        decimal price = 15m, decimal offered = 465m, string? command = null,
        string? source = null) =>
        new(Producer, command ?? Key(), source ?? Key(),
            s.Trip, s.Sku, quantity, price, offered);

    private Scene Seed(string tenant, decimal physicalQty, decimal physicalCost,
        bool openVehicle)
    {
        var s = new Scene(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.Empty, Guid.Empty);
        using var db = Open(tenant);
        db.Execute("""
            INSERT dbo.Customer(Id,TenantId,Code,Name)
                VALUES(@Customer,@Tenant,@CustomerCode,N'APP01B2 Customer');
            INSERT dbo.ShipToSnapshot(Id,TenantId,CustomerId,DisplayName,Address)
                VALUES(@ShipTo,@Tenant,@Customer,N'ShipTo',N'Synthetic');
            INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom)
                VALUES(@Sku,@Tenant,@SkuCode,N'APP01B2 SKU',N'EA');
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
            INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,
                SnapshotCode,BaseQuantity,CostAmount)
                VALUES(@Tenant,@Warehouse,@Sku,N'APP01B2-T0',@Quantity,@Cost);
            INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,
                OnHandBaseQuantity,OnHandCostAmount)
                VALUES(@Tenant,@Warehouse,@Sku,@Quantity,@Cost);
            """, new
        {
            Tenant = tenant, Customer = Guid.NewGuid(), ShipTo = Guid.NewGuid(),
            s.Sku, s.Warehouse, s.Vehicle, s.Trip, s.Order, s.OrderLine,
            CustomerCode = Key(), SkuCode = Key(), WhCode = Key(),
            VehicleCode = Key(), TripCode = Key(), OrderCode = Key(),
            Quantity = physicalQty, Cost = physicalCost
        });
        if (openVehicle)
            db.Execute("""
                INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,
                    SnapshotCode,BaseQuantity,CostAmount)
                    VALUES(@Tenant,@Vehicle,@Sku,N'APP01B2-T0',0,0);
                INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,
                    OnHandBaseQuantity,OnHandCostAmount)
                    VALUES(@Tenant,@Vehicle,@Sku,0,0);
                """, new { Tenant = tenant, s.Vehicle, s.Sku });
        return s;
    }

    private async Task<Scene> Prepare(HttpClient client, string tenant = A,
        decimal physicalQty = 100m, decimal physicalCost = 1000m,
        bool openVehicle = true, bool confirm = true)
    {
        var s = Seed(tenant, physicalQty, physicalCost, openVehicle);
        var reserve = await Send(client, RootA + "reserve-a-stock",
            new ReserveAStockCommand(Producer, Key(), Key(),
                s.Order, s.OrderLine, s.Warehouse, 40m));
        Assert.Equal("A_STOCK_RESERVED", reserve.Outcome.OutcomeCode);
        var quota = await Send(client, RootA + "grant-b-quota",
            new GrantBQuotaCommand(Producer, Key(), Key(),
                s.Trip, s.Warehouse, s.Sku, 30m));
        Assert.Equal("B_QUOTA_GRANTED", quota.Outcome.OutcomeCode);
        if (confirm)
            await Confirm(client, s);

        using var db = Open(tenant);
        Guid rightA = db.QuerySingle<Guid>("""
            SELECT Id FROM dbo.StockRight WHERE TenantId=@Tenant AND TripId=@Trip
                AND StockLocationId=@Warehouse AND SkuId=@Sku AND RightKind='A'
            """, new { Tenant = tenant, s.Trip, s.Warehouse, s.Sku });
        Guid rightB = db.QuerySingle<Guid>("""
            SELECT Id FROM dbo.StockRight WHERE TenantId=@Tenant AND TripId=@Trip
                AND StockLocationId=@Warehouse AND SkuId=@Sku AND RightKind='B'
            """, new { Tenant = tenant, s.Trip, s.Warehouse, s.Sku });
        db.Execute("""
            INSERT dbo.TripManifest(Id,TenantId,ManifestNumber,TripId,
                WarehouseLocationId,VehicleLocationId)
                VALUES(@Manifest,@Tenant,@Number,@Trip,@Warehouse,@Vehicle);
            INSERT dbo.TripManifestLine(Id,TenantId,TripManifestId,TripId,
                WarehouseLocationId,VehicleLocationId,LineNumber,SkuId,
                StockRightId,RightKind,PlannedBaseQuantity)
                VALUES(@LineA,@Tenant,@Manifest,@Trip,@Warehouse,@Vehicle,1,@Sku,
                    @RightA,'A',40);
            INSERT dbo.TripManifestLine(Id,TenantId,TripManifestId,TripId,
                WarehouseLocationId,VehicleLocationId,LineNumber,SkuId,
                StockRightId,RightKind,PlannedBaseQuantity)
                VALUES(@LineB,@Tenant,@Manifest,@Trip,@Warehouse,@Vehicle,2,@Sku,
                    @RightB,'B',30);
            """, new
        {
            Tenant = tenant, s.Manifest, s.Trip, s.Warehouse, s.Vehicle, s.Sku,
            RightA = rightA, RightB = rightB, LineA = Guid.NewGuid(),
            LineB = Guid.NewGuid(), Number = Key()
        });
        return s with { ARight = rightA, BRight = rightB };
    }

    private static async Task Confirm(HttpClient client, Scene s)
    {
        var confirmed = await Send(client, RootA + "confirm-a-order",
            new ConfirmAOrderCommand(Producer, Key(), Key(), s.Order));
        Assert.Equal("A_ORDER_CONFIRMED", confirmed.Outcome.OutcomeCode);
    }

    private (decimal WhQty, decimal WhCost, decimal VehicleQty, decimal VehicleCost,
        decimal PoolAllocated, decimal AAvail, decimal BAvail,
        int Sources, int SourceLines, int Movements, int Effects, int Financial) State(
        string tenant, Scene s)
    {
        using var db = Open(tenant);
        (decimal, decimal) Balance(Guid location) =>
            db.QuerySingle<(decimal, decimal)>("""
                SELECT OnHandBaseQuantity,OnHandCostAmount
                FROM dbo.PhysicalStockBalance
                WHERE TenantId=@Tenant AND StockLocationId=@Location AND SkuId=@Sku
                """, new { Tenant = tenant, Location = location, s.Sku });
        var w = Balance(s.Warehouse);
        (decimal, decimal) v = db.QuerySingleOrDefault<(decimal, decimal)>("""
            SELECT OnHandBaseQuantity,OnHandCostAmount FROM dbo.PhysicalStockBalance
            WHERE TenantId=@Tenant AND StockLocationId=@Vehicle AND SkuId=@Sku
            """, new { Tenant = tenant, s.Vehicle, s.Sku });
        var pool = db.QuerySingle<decimal>("""
            SELECT AllocatedBaseQuantity FROM dbo.StockPool
            WHERE TenantId=@Tenant AND StockLocationId=@Warehouse AND SkuId=@Sku
            """, new { Tenant = tenant, s.Warehouse, s.Sku });
        (decimal, decimal) rights = db.QuerySingle<(decimal, decimal)>("""
            SELECT
                COALESCE(SUM(CASE WHEN RightKind='A' THEN
                    GrantedBaseQuantity-ConsumedBaseQuantity-ReleasedBaseQuantity ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN RightKind='B' THEN
                    GrantedBaseQuantity-ConsumedBaseQuantity-ReleasedBaseQuantity ELSE 0 END),0)
            FROM dbo.StockRight WHERE TenantId=@Tenant AND TripId=@Trip
            """, new { Tenant = tenant, s.Trip });
        int sources = db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.BusinessSource
            WHERE TenantId=@Tenant AND TripId=@Trip
            """, new { Tenant = tenant, s.Trip });
        int sourceLines = db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.BusinessSourceLine l
            JOIN dbo.BusinessSource src ON src.TenantId=l.TenantId
                AND src.Id=l.BusinessSourceId
            WHERE src.TenantId=@Tenant AND src.TripId=@Trip
            """, new { Tenant = tenant, s.Trip });
        int effects = db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.LedgerEffect e
            JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId
                AND l.Id=e.BusinessSourceLineId
            JOIN dbo.BusinessSource src ON src.TenantId=l.TenantId
                AND src.Id=l.BusinessSourceId
            WHERE src.TenantId=@Tenant AND src.TripId=@Trip
            """, new { Tenant = tenant, s.Trip });
        int financial = db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.LedgerEffect e
            JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId
                AND l.Id=e.BusinessSourceLineId
            JOIN dbo.BusinessSource src ON src.TenantId=l.TenantId
                AND src.Id=l.BusinessSourceId
            WHERE src.TenantId=@Tenant AND src.TripId=@Trip
                AND e.LedgerKind IN (N'AR',N'COGS',N'REVENUE',N'CASH',N'CUSTODY',N'FUND')
            """, new { Tenant = tenant, s.Trip });
        int movements = db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.PhysicalStockMovement mv
            JOIN dbo.TripManifestLine l ON l.TenantId=mv.TenantId
                AND l.Id=mv.TripManifestLineId
            WHERE l.TenantId=@Tenant AND l.TripManifestId=@Manifest
            """, new { Tenant = tenant, s.Manifest });
        return (w.Item1, w.Item2, v.Item1, v.Item2, pool,
            rights.Item1, rights.Item2, sources, sourceLines, movements, effects, financial);
    }

    [Fact]
    public async Task T1_load_transfers_70_units_and_700_cost_without_touching_A_B_rights()
    {
        using var factory = fixture.CreateFactory();
        using var client = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var s = await Prepare(client);
        var t0 = State(A, s);
        Assert.Equal((100m, 1000m, 0m, 0m), (t0.WhQty, t0.WhCost, t0.VehicleQty, t0.VehicleCost));
        Assert.Equal((70m, 40m, 30m), (t0.PoolAllocated, t0.AAvail, t0.BAvail));

        var loaded = await Send(client, RootB + "load-manifest", Load(s));
        Assert.Equal(HttpStatusCode.OK, loaded.Status);
        Assert.Equal("MANIFEST_LOADED", loaded.Outcome.OutcomeCode);
        Assert.NotNull(loaded.Outcome.SourceId);

        var t1 = State(A, s);
        Assert.Equal((30m, 300m, 70m, 700m),
            (t1.WhQty, t1.WhCost, t1.VehicleQty, t1.VehicleCost));
        Assert.Equal((70m, 40m, 30m), (t1.PoolAllocated, t1.AAvail, t1.BAvail));
        Assert.Equal(t0.Sources + 1, t1.Sources);
        Assert.Equal(t0.SourceLines + 2, t1.SourceLines);
        Assert.Equal(t0.Effects + 4, t1.Effects);
        Assert.Equal(2, t1.Movements);
        Assert.Equal(t0.Financial, t1.Financial);

        using var db = Open(A);
        Assert.Equal("LOADED", db.ExecuteScalar<string>("""
            SELECT Status FROM dbo.TripManifest WHERE TenantId=@Tenant AND Id=@Manifest
            """, new { Tenant = A, s.Manifest }));
        Assert.Equal(new[] { (40m, 400m), (30m, 300m) },
            db.Query<(decimal, decimal)>("""
                SELECT BaseQuantity,CostAmount FROM dbo.PhysicalStockMovement mv
                JOIN dbo.TripManifestLine l ON l.TenantId=mv.TenantId
                    AND l.Id=mv.TripManifestLineId
                WHERE l.TenantId=@Tenant AND l.TripManifestId=@Manifest
                ORDER BY l.LineNumber
                """, new { Tenant = A, s.Manifest }).ToArray());
        Assert.Equal((0m, 0m), db.QuerySingle<(decimal, decimal)>("""
            SELECT SUM(QuantityDelta),SUM(AmountDelta) FROM dbo.LedgerEffect
            WHERE TenantId=@Tenant AND LedgerKind=N'PHYSICAL_STOCK'
                AND BusinessSourceLineId IN (
                    SELECT Id FROM dbo.BusinessSourceLine
                    WHERE TenantId=@Tenant AND BusinessSourceId=@Source)
            """, new { Tenant = A, Source = loaded.Outcome.SourceId }));
    }

    [Fact]
    public async Task T2_B31_price15_cash465_rejected_quota_durably_with_zero_business_effect()
    {
        using var factory = fixture.CreateFactory();
        using var client = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var s = await Prepare(client);
        Assert.Equal("MANIFEST_LOADED",
            (await Send(client, RootB + "load-manifest", Load(s))).Outcome.OutcomeCode);
        var before = State(A, s);
        var b31 = Offer(s);
        var denied = await Send(client, RootB + "preflight-b-sale", b31);
        Assert.Equal(HttpStatusCode.OK, denied.Status);
        Assert.Equal("REJECTED", denied.Outcome.Status);
        Assert.Equal("REJECTED_QUOTA", denied.Outcome.OutcomeCode);
        Assert.Equal("B_QUOTA_INSUFFICIENT", denied.Outcome.ReasonCode);
        Assert.Null(denied.Outcome.SourceId);
        Assert.False(denied.Outcome.Replayed);

        var retry = await Send(client, RootB + "preflight-b-sale", b31);
        Assert.Equal(denied.Outcome.OutcomeCode, retry.Outcome.OutcomeCode);
        Assert.Equal("B_QUOTA_INSUFFICIENT", retry.Outcome.ReasonCode);
        Assert.True(retry.Outcome.Replayed);
        Assert.Null(retry.Outcome.SourceId);
        var conflict = await Send(client, RootB + "preflight-b-sale",
            b31 with { BaseQuantity = 30m, OfferedCashAmount = 450m });
        Assert.Equal(HttpStatusCode.Conflict, conflict.Status);
        Assert.Equal("IDEMPOTENCY_PAYLOAD_MISMATCH", conflict.Outcome.ReasonCode);
        Assert.Equal(before, State(A, s));

        using var db = Open(A);
        Assert.Equal(1, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.BusinessCommand WHERE TenantId=@Tenant
                AND ProducerId=@Producer AND CommandId=@Command
                AND Status=N'REJECTED' AND OutcomeCode=N'REJECTED_QUOTA'
                AND ReasonCode=N'B_QUOTA_INSUFFICIENT'
            """, new { Tenant = A, Producer, Command = b31.CommandId }));
        Assert.Equal("B_SALE_NOT_IMPLEMENTED",
            (await Send(client, RootB + "preflight-b-sale",
                Offer(s, 20m, 15m, 300m))).Outcome.ReasonCode);
        Assert.Equal(before, State(A, s));
    }

    [Fact]
    public async Task Load_same_identity_replays_and_changed_payload_or_cross_kind_conflicts()
    {
        using var factory = fixture.CreateFactory();
        using var client = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var s = await Prepare(client);
        var command = Load(s);
        var first = await Send(client, RootB + "load-manifest", command);
        var snapshot = State(A, s);
        var replay = await Send(client, RootB + "load-manifest", command);
        var changed = await Send(client, RootB + "load-manifest",
            command with { SourceNumber = Key() });
        var crossKind = await Send(client, RootA + "grant-b-quota",
            new GrantBQuotaCommand(Producer, command.CommandId, Key(),
                s.Trip, s.Warehouse, s.Sku, 1m));
        Assert.Equal("APPLIED", first.Outcome.Status);
        Assert.Equal(first.Outcome.SourceId, replay.Outcome.SourceId);
        Assert.True(replay.Outcome.Replayed);
        Assert.Equal(HttpStatusCode.Conflict, changed.Status);
        Assert.Equal(HttpStatusCode.Conflict, crossKind.Status);
        Assert.Equal(snapshot, State(A, s));
    }

    [Fact]
    public async Task A_right_requires_confirmed_A_order_before_manifest_load()
    {
        using var factory = fixture.CreateFactory();
        using var client = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var s = await Prepare(client, confirm: false);
        var before = State(A, s);
        Assert.Equal("MANIFEST_RIGHT_NOT_ELIGIBLE",
            (await Send(client, RootB + "load-manifest", Load(s))).Outcome.ReasonCode);
        Assert.Equal(before, State(A, s));
        await Confirm(client, s);
        Assert.Equal("MANIFEST_LOADED",
            (await Send(client, RootB + "load-manifest", Load(s))).Outcome.OutcomeCode);
    }

    [Fact]
    public async Task Physical_short_stock_or_missing_vehicle_opening_reject_without_partial_load()
    {
        using var factory = fixture.CreateFactory();
        using var client = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var low = await Prepare(client, physicalQty: 60m, physicalCost: 600m);
        var lowBefore = State(A, low);
        Assert.Equal("PHYSICAL_STOCK_INSUFFICIENT",
            (await Send(client, RootB + "load-manifest", Load(low))).Outcome.ReasonCode);
        Assert.Equal(lowBefore, State(A, low));
        var missing = await Prepare(client, openVehicle: false);
        var before = State(A, missing);
        Assert.Equal("PHYSICAL_OPENING_REQUIRED",
            (await Send(client, RootB + "load-manifest", Load(missing))).Outcome.ReasonCode);
        Assert.Equal(before, State(A, missing));
    }

    [Fact]
    public async Task Injected_SQL_error_after_first_movement_rolls_back_and_same_key_can_retry()
    {
        using var factory = fixture.CreateFactory();
        using var client = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var s = await Prepare(client);
        var before = State(A, s);
        var command = Load(s);
        using var db = Open(A);
        Guid line = db.QuerySingle<Guid>("""
            SELECT Id FROM dbo.TripManifestLine
            WHERE TenantId=@Tenant AND TripManifestId=@Manifest AND LineNumber=1
            """, new { Tenant = A, s.Manifest });
        // No application changes; a deliberately failing SQL trigger after the
        // first movement probes transaction rollback across every table.
        db.Execute($"""
            CREATE TRIGGER dbo.TR_App01b2_ForceRollback ON dbo.PhysicalStockMovement
            AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM inserted
                    WHERE TripManifestLineId='{line:D}')
                    THROW 51070, 'APP01B2 forced rollback', 1;
            END
            """);
        try
        {
            bool failed = false;
            try
            {
                using var response = await client.PostAsJsonAsync(
                    RootB + "load-manifest", command, TestContext.Current.CancellationToken);
                failed = !response.IsSuccessStatusCode;
            }
            catch (Exception) { failed = true; } // TestServer can propagate SQL error.
            Assert.True(failed);
            Assert.Equal(before, State(A, s));
            Assert.Equal("DRAFT", db.ExecuteScalar<string>(
                "SELECT Status FROM dbo.TripManifest WHERE Id=@Manifest",
                new { s.Manifest }));
            Assert.Equal(0, db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM dbo.BusinessCommand
                WHERE TenantId=@Tenant AND ProducerId=@Producer AND CommandId=@Command
                """, new { Tenant = A, Producer, Command = command.CommandId }));
        }
        finally { db.Execute("DROP TRIGGER IF EXISTS dbo.TR_App01b2_ForceRollback"); }

        var applied = await Send(client, RootB + "load-manifest", command);
        Assert.Equal("MANIFEST_LOADED", applied.Outcome.OutcomeCode);
        Assert.False(applied.Outcome.Replayed);
        Assert.Equal(2, State(A, s).Movements);
    }

    [Fact]
    public async Task Concurrent_loads_with_different_keys_only_post_once()
    {
        using var factory = fixture.CreateFactory();
        using var a = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        using var b = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var s = await Prepare(a);
        var pending = await Task.WhenAll(
            Send(a, RootB + "load-manifest", Load(s)),
            Send(b, RootB + "load-manifest", Load(s)));
        Assert.Equal(1, pending.Count(x => x.Outcome.OutcomeCode == "MANIFEST_LOADED"));
        Assert.Equal(1, pending.Count(x => x.Outcome.ReasonCode == "MANIFEST_ALREADY_LOADED"));
        Assert.Equal(2, State(A, s).Movements);
    }

    [Fact]
    public async Task JWT_role_and_two_tenant_routing_prevent_foreign_manifest_access()
    {
        using var factory = fixture.CreateFactory();
        using var a = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        using var b = SqlScratchFixture.WithTenant(factory.CreateClient(), B);
        var aScene = await Prepare(a, A);
        var bScene = await Prepare(b, B);
        var beforeB = State(B, bScene);
        b.DefaultRequestHeaders.Add("X-Tenant-Id", A);
        Assert.Equal("MANIFEST_NOT_FOUND",
            (await Send(b, RootB + "load-manifest", Load(aScene))).Outcome.ReasonCode);
        Assert.Equal(beforeB, State(B, bScene));

        using var salesOnly = SqlScratchFixture.WithTenant(
            factory.CreateClient(), A, roles: ["sales"]);
        using var warehouseOnly = SqlScratchFixture.WithTenant(
            factory.CreateClient(), A, roles: ["warehouse"]);
        using var loadDenied = await salesOnly.PostAsJsonAsync(
            RootB + "load-manifest", Load(aScene), TestContext.Current.CancellationToken);
        using var offerDenied = await warehouseOnly.PostAsJsonAsync(
            RootB + "preflight-b-sale", Offer(aScene), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, loadDenied.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, offerDenied.StatusCode);

        Assert.Equal("MANIFEST_LOADED",
            (await Send(a, RootB + "load-manifest", Load(aScene))).Outcome.OutcomeCode);
        Assert.Equal(beforeB, State(B, bScene));
        Assert.Equal("MANIFEST_LOADED",
            (await Send(b, RootB + "load-manifest", Load(bScene))).Outcome.OutcomeCode);
        Assert.Equal((30m, 300m, 70m, 700m),
            (State(B, bScene).WhQty, State(B, bScene).WhCost,
             State(B, bScene).VehicleQty, State(B, bScene).VehicleCost));
    }

    [Fact]
    public async Task Full_depletion_moves_all_fractional_cost_without_orphaned_residual()
    {
        using var factory = fixture.CreateFactory();
        using var client = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var s = await Prepare(client, physicalQty: 70m, physicalCost: 1001.2500m);
        Assert.Equal("MANIFEST_LOADED",
            (await Send(client, RootB + "load-manifest", Load(s))).Outcome.OutcomeCode);
        var state = State(A, s);
        Assert.Equal((0m, 0m, 70m, 1001.2500m),
            (state.WhQty, state.WhCost, state.VehicleQty, state.VehicleCost));
        Assert.Equal((70m, 40m, 30m), (state.PoolAllocated, state.AAvail, state.BAvail));
        using var db = Open(A);
        var amounts = db.Query<decimal>("""
            SELECT mv.CostAmount FROM dbo.PhysicalStockMovement mv
            JOIN dbo.TripManifestLine l ON l.TenantId=mv.TenantId
                AND l.Id=mv.TripManifestLineId
            WHERE l.TenantId=@Tenant AND l.TripManifestId=@Manifest
            ORDER BY l.LineNumber
            """, new { Tenant = A, s.Manifest }).ToArray();
        Assert.Equal(2, amounts.Length);
        Assert.Equal(1001.2500m, amounts.Sum());
        Assert.All(amounts, amount => Assert.Equal(decimal.Round(amount, 4), amount));
        Assert.Equal(0, state.Financial);
    }

    [Fact]
    public async Task Forged_allocation_source_lineage_cannot_drive_a_physical_load()
    {
        using var factory = fixture.CreateFactory();
        using var client = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var s = await Prepare(client);
        var before = State(A, s);
        using var db = Open(A);
        Guid allocationSource = db.QuerySingle<Guid>("""
            SELECT src.Id FROM dbo.BusinessSource src
            JOIN dbo.BusinessSourceLine sl ON sl.TenantId=src.TenantId
                AND sl.BusinessSourceId=src.Id
            JOIN dbo.StockRight r ON r.TenantId=sl.TenantId
                AND r.BusinessSourceLineId=sl.Id
            WHERE r.TenantId=@Tenant AND r.Id=@Right
            """, new { Tenant = A, Right = s.ARight });
        db.Execute("""
            UPDATE dbo.BusinessSource SET SourceKind=N'TAMPERED_ALLOC'
            WHERE TenantId=@Tenant AND Id=@Source
            """, new { Tenant = A, Source = allocationSource });
        Assert.Equal("MANIFEST_RIGHT_NOT_ELIGIBLE",
            (await Send(client, RootB + "load-manifest", Load(s))).Outcome.ReasonCode);
        Assert.Equal(before, State(A, s));
        db.Execute("""
            UPDATE dbo.BusinessSource SET SourceKind=N'STOCK_ALLOC'
            WHERE TenantId=@Tenant AND Id=@Source
            """, new { Tenant = A, Source = allocationSource });
        Assert.Equal("MANIFEST_LOADED",
            (await Send(client, RootB + "load-manifest", Load(s))).Outcome.OutcomeCode);
    }

    [Fact]
    public async Task Confirmed_A_order_line_cannot_change_sku_behind_held_stock_right()
    {
        using var factory = fixture.CreateFactory();
        using var client = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var s = await Prepare(client);
        using var db = Open(A);
        var otherSku = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom)
            VALUES(@Sku,@Tenant,@Code,N'Mutated SKU',N'EA')
            """, new { Sku = otherSku, Tenant = A, Code = Key() });
        // C.3 freezes exact confirm SKU/quantity via a composite FK.
        // SQL itself now rejects a forbidden mutation before manifest load;
        // prior C.2 tests used to permit the mutation and reject on load.
        var before = State(A, s);
        Assert.Throws<SqlException>(() => db.Execute("""
            UPDATE dbo.AOrderLine SET SkuId=@Sku
            WHERE TenantId=@Tenant AND Id=@OrderLine
            """, new { Sku = otherSku, Tenant = A, s.OrderLine }));
        Assert.Equal(before, State(A, s));
        Assert.Equal("MANIFEST_LOADED",
            (await Send(client, RootB + "load-manifest", Load(s))).Outcome.OutcomeCode);
        Assert.Equal(2, State(A, s).Movements);
        Assert.Equal(s.Sku, db.ExecuteScalar<Guid>("""
            SELECT SkuId FROM dbo.AOrderLine WHERE TenantId=@Tenant AND Id=@Line
            """, new { Tenant = A, Line = s.OrderLine }));
    }

    [Fact]
    public async Task Confirmed_A_quantity_mutation_cannot_authorize_stale_manifest_load()
    {
        using var factory = fixture.CreateFactory();
        using var client = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var s = await Prepare(client);
        using var db = Open(A);
        var before = State(A, s);
        Assert.Throws<SqlException>(() => db.Execute("""
            UPDATE dbo.AOrderLine SET BaseQuantity=20
            WHERE TenantId=@Tenant AND Id=@Line
            """, new { Tenant = A, Line = s.OrderLine }));
        Assert.Equal(before, State(A, s));
        Assert.Equal("DRAFT", db.ExecuteScalar<string>("""
            SELECT Status FROM dbo.TripManifest
            WHERE TenantId=@Tenant AND Id=@Manifest
            """, new { Tenant = A, s.Manifest }));
        Assert.Equal(40m, db.ExecuteScalar<decimal>("""
            SELECT BaseQuantity FROM dbo.AOrderLine
            WHERE TenantId=@Tenant AND Id=@Line
            """, new { Tenant = A, Line = s.OrderLine }));
        Assert.Equal("MANIFEST_LOADED",
            (await Send(client, RootB + "load-manifest", Load(s))).Outcome.OutcomeCode);
    }

    [Fact]
    public async Task Split_A_reservations_for_one_confirmed_line_allow_valid_manifest_load()
    {
        using var factory = fixture.CreateFactory();
        using var client = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var s = Seed(A, 100m, 1000m, openVehicle: true);
        foreach (var amount in new[] { 15m, 25m })
            Assert.Equal("A_STOCK_RESERVED", (await Send(client,
                RootA + "reserve-a-stock", new ReserveAStockCommand(
                    Producer, Key(), Key(), s.Order, s.OrderLine, s.Warehouse, amount)))
                .Outcome.OutcomeCode);
        Assert.Equal("B_QUOTA_GRANTED", (await Send(client,
            RootA + "grant-b-quota", new GrantBQuotaCommand(
                Producer, Key(), Key(), s.Trip, s.Warehouse, s.Sku, 30m)))
            .Outcome.OutcomeCode);
        await Confirm(client, s);

        using var db = Open(A);
        Guid a15 = db.QuerySingle<Guid>("""
            SELECT r.Id FROM dbo.StockRight r
            JOIN dbo.AReservation a ON a.TenantId=r.TenantId AND a.StockRightId=r.Id
            WHERE a.TenantId=@Tenant AND a.AOrderLineId=@Line AND r.GrantedBaseQuantity=15
            """, new { Tenant = A, Line = s.OrderLine });
        Guid a25 = db.QuerySingle<Guid>("""
            SELECT r.Id FROM dbo.StockRight r
            JOIN dbo.AReservation a ON a.TenantId=r.TenantId AND a.StockRightId=r.Id
            WHERE a.TenantId=@Tenant AND a.AOrderLineId=@Line AND r.GrantedBaseQuantity=25
            """, new { Tenant = A, Line = s.OrderLine });
        Guid b30 = db.QuerySingle<Guid>("""
            SELECT r.Id FROM dbo.StockRight r
            JOIN dbo.BQuota b ON b.TenantId=r.TenantId AND b.StockRightId=r.Id
            WHERE r.TenantId=@Tenant AND r.TripId=@Trip
            """, new { Tenant = A, s.Trip });

        db.Execute("""
            INSERT dbo.TripManifest(Id,TenantId,ManifestNumber,TripId,
                WarehouseLocationId,VehicleLocationId)
                VALUES(@Manifest,@Tenant,@Number,@Trip,@Warehouse,@Vehicle);
            INSERT dbo.TripManifestLine(Id,TenantId,TripManifestId,TripId,
                WarehouseLocationId,VehicleLocationId,LineNumber,SkuId,
                StockRightId,RightKind,PlannedBaseQuantity)
                VALUES(NEWID(),@Tenant,@Manifest,@Trip,@Warehouse,@Vehicle,1,@Sku,@A15,'A',15);
            INSERT dbo.TripManifestLine(Id,TenantId,TripManifestId,TripId,
                WarehouseLocationId,VehicleLocationId,LineNumber,SkuId,
                StockRightId,RightKind,PlannedBaseQuantity)
                VALUES(NEWID(),@Tenant,@Manifest,@Trip,@Warehouse,@Vehicle,2,@Sku,@A25,'A',25);
            INSERT dbo.TripManifestLine(Id,TenantId,TripManifestId,TripId,
                WarehouseLocationId,VehicleLocationId,LineNumber,SkuId,
                StockRightId,RightKind,PlannedBaseQuantity)
                VALUES(NEWID(),@Tenant,@Manifest,@Trip,@Warehouse,@Vehicle,3,@Sku,@B30,'B',30);
            """, new { Tenant = A, s.Manifest, s.Trip, s.Warehouse, s.Vehicle,
                s.Sku, A15 = a15, A25 = a25, B30 = b30, Number = Key() });

        var loaded = await Send(client, RootB + "load-manifest", Load(s));
        Assert.Equal("MANIFEST_LOADED", loaded.Outcome.OutcomeCode);
        var state = State(A, s);
        Assert.Equal((30m, 300m, 70m, 700m),
            (state.WhQty, state.WhCost, state.VehicleQty, state.VehicleCost));
        Assert.Equal((70m, 40m, 30m), (state.PoolAllocated, state.AAvail, state.BAvail));
        Assert.Equal(3, state.Movements);
        Assert.Equal(0, state.Financial);
    }

}
