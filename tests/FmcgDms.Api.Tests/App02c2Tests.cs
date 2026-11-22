using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FmcgDms.Api.Tests;

[Collection("SqlScratch")]
public sealed class App02c2Tests(SqlScratchFixture fixture)
{
    private const string Root = "/api/synthetic/app02c2";
    private readonly CancellationToken ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task C4_list_requires_current_sales_authority_and_a_delivery_writer()
    {
        using var factory = fixture.CreateFactory(c2Enabled: true, c3Enabled: true, c4Enabled: true);
        await using var db = new SqlConnection(fixture.TenantAConnectionString);
        var seed = await Seed(db);
        const string root = "/api/synthetic/app02c4";
        using var sales = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a", seed.Actor, ["sales"]);
        using var warehouse = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a", seed.Actor, ["warehouse"]);
        var rows = await sales.GetFromJsonAsync<JsonElement>(root + "/trips", ct);
        Assert.Empty(rows.EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync(root + $"/trips/{seed.Trip}/lines", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await warehouse.GetAsync(root + "/trips", ct)).StatusCode);
    }

    [Fact]
    public async Task C4_uses_loaded_right_frozen_policy_and_versioned_shipto_and_recovers_late_rollback()
    {
        using var factory = fixture.CreateFactory(c2Enabled: true, c3Enabled: true, c4Enabled: true);
        await using var db = new SqlConnection(fixture.TenantAConnectionString);
        var seed = await Seed(db);
        string checker = seed.Actor + "-checker";
        await db.ExecuteAsync("""
            INSERT dbo.C3TripWriter VALUES(N'tenant-a',@Trip,@Actor);
            INSERT dbo.C4DeliveryWriter VALUES(N'tenant-a',@Trip,@Actor);
            INSERT dbo.App02ActorRoleGrant(TenantId,ActorId,RoleCode,IsActive,ChangedBy)
            VALUES(N'tenant-a',@Checker,'sales',1,N'test'),(N'tenant-a',@Checker,'case_checker',1,N'test');
            UPDATE dbo.Trip SET BusinessDate=CONVERT(date,'2026-01-01') WHERE Id=@Trip;
            INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,SnapshotCode,BaseQuantity,CostAmount)
            VALUES(N'tenant-a',@Warehouse,@Sku,N'C4',100,1000),(N'tenant-a',@Vehicle,@Sku,N'C4',0,0);
            INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,OnHandBaseQuantity,OnHandCostAmount)
            VALUES(N'tenant-a',@Warehouse,@Sku,100,1000),(N'tenant-a',@Vehicle,@Sku,0,0);
            """, new { seed.Trip, seed.Actor, seed.Warehouse, seed.Vehicle, seed.Sku, Checker = checker });
        using var actor = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a", seed.Actor, ["sales", "warehouse"]);
        using var approval = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a", checker, ["sales", "case_checker"]);
        Guid order = await Draft(actor, seed, await Direct(actor, seed));
        await AddPolicy(db, seed, 2, "APPROVED", discount: 2m, tax: 0.1m);
        var confirmInput = new C2ConfirmInput(Guid.NewGuid(), order);
        Guid confirm = Source(await Post(actor, Root + "/confirm", confirmInput));
        Guid line = (await Order(actor, order)).GetProperty("lines")[0].GetProperty("lineId").GetGuid();
        await Post(actor, Root + "/reserve", new C2ReserveInput(Guid.NewGuid(), order, line, seed.Warehouse, 25, confirm));
        Guid right = await db.QuerySingleAsync<Guid>("SELECT Id FROM dbo.StockRight WHERE SkuId=@Sku", seed);
        await Post(actor, "/api/synthetic/app02c3/load", new C3LoadInput(Guid.NewGuid(), seed.Trip, [new(right, 25, 25)]));
        Guid manifest = await db.QuerySingleAsync<Guid>("SELECT Id FROM dbo.TripManifestLine WHERE SkuId=@Sku", seed);
        await AddPolicy(db, seed, 3, "UNSET");

        const string root = "/api/synthetic/app02c4";
        var first = new C4DeliveryInput(Guid.NewGuid(), seed.Trip, manifest, 1, 25);
        await Post(actor, root + "/deliver", first with { CommandId = confirmInput.CommandId }, HttpStatusCode.Conflict);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.C4DeliveryEvidence WHERE TripId=@Trip", seed));
        var competing = await Task.WhenAll(Post(actor, root + "/deliver", first), Post(actor, root + "/deliver", first));
        var applied = Assert.Single(competing, x => !x.GetProperty("replayed").GetBoolean());
        Assert.Equal(Source(applied), Source(await Post(actor, root + "/deliver", first)));
        await Post(actor, root + "/deliver", first with { Quantity = 26 }, HttpStatusCode.Conflict);
        var ar = Assert.Single((await actor.GetFromJsonAsync<C4ArView[]>(root + $"/trips/{seed.Trip}/ar", ct))!);
        Assert.Equal(357.5m, ar.Amount);
        Assert.Equal(250m, ar.Cost);
        Assert.NotEqual(new DateTime(2026, 1, 1), ar.BusinessDate.Date);
        Assert.Equal(ar.BusinessDate.Date.AddDays(7), ar.DueBusinessDate.Date);
        Assert.Equal(25m, await db.ExecuteScalarAsync<decimal>("SELECT ConsumedBaseQuantity FROM dbo.StockRight WHERE Id=@right", new { right }));
        await Post(actor, Root + "/reserve", new C2ReserveInput(Guid.NewGuid(), order, line, seed.Warehouse, 16, confirm),
            HttpStatusCode.UnprocessableEntity);
        await Post(actor, Root + "/reserve", new C2ReserveInput(Guid.NewGuid(), order, line, seed.Warehouse, 15, confirm));
        Guid remainderRight = await db.QuerySingleAsync<Guid>(
            "SELECT Id FROM dbo.StockRight WHERE SkuId=@Sku AND GrantedBaseQuantity=15", seed);
        await Post(actor, "/api/synthetic/app02c3/load",
            new C3LoadInput(Guid.NewGuid(), seed.Trip, [new(remainderRight, 15, 15)]));
        Guid remainderManifest = await db.QuerySingleAsync<Guid>(
            "SELECT Id FROM dbo.TripManifestLine WHERE StockRightId=@remainderRight", new { remainderRight });
        first = first with { ManifestLineId = remainderManifest };

        Guid selection = (await Post(actor, "/api/synthetic/app02c1/ship-to-selections", new
        {
            commandId = Guid.NewGuid(), customerId = seed.Customer,
            address = new { country = "VN", region = "HCM", locality = "Khu DEV", line = "C4 dia chi moi", recipient = "Nguoi nhan" }
        })).GetProperty("id").GetGuid();
        var amend = new C4AmendInput(Guid.NewGuid(), seed.Trip, order, selection, 1, "Địa chỉ mới cho phần chưa giao");
        Assert.Equal(HttpStatusCode.Forbidden, (await actor.PostAsJsonAsync(root + "/amend-shipto", amend, ct)).StatusCode);
        await Post(approval, root + "/amend-shipto", amend);
        Assert.Equal("C4_STALE_SHIPTO", Reason(await Post(actor, root + "/deliver",
            first with { CommandId = Guid.NewGuid(), Quantity = 15 }, HttpStatusCode.UnprocessableEntity)));
        Assert.Equal("C4_QUANTITY_NOT_AVAILABLE", Reason(await Post(actor, root + "/deliver",
            first with { CommandId = Guid.NewGuid(), ShipToVersion = 2, Quantity = 16 }, HttpStatusCode.UnprocessableEntity)));

        var last = first with { CommandId = Guid.NewGuid(), ShipToVersion = 2, Quantity = 15 };
        string trigger = "TR_C4Abort_" + remainderManifest.ToString("N");
        await db.ExecuteAsync($"""
            CREATE TRIGGER dbo.[{trigger}] ON dbo.LedgerEffect AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.SaleDelivery d
                    ON d.TenantId=i.TenantId AND d.BusinessSourceLineId=i.BusinessSourceLineId
                    WHERE d.TripManifestLineId='{remainderManifest:D}' AND d.BaseQuantity=15 AND i.EffectKey='AR-GROSS')
                    THROW 51609, 'C.4 late AR fault', 1;
            END;
            """);
        try
        {
            var fault = await Assert.ThrowsAsync<SqlException>(() => actor.PostAsJsonAsync(root + "/deliver", last, ct));
            Assert.Equal(51609, fault.Number);
            var cases = await actor.GetFromJsonAsync<C4CaseView[]>(root + $"/trips/{seed.Trip}/cases", ct);
            Assert.Contains(cases!, x => x.CommandId == last.CommandId && x.ReasonCode is null && x.CanRetry && x.ShipToVersion == 2);
            await Post(actor, Root + "/reserve", new C2ReserveInput(last.CommandId, order, line, seed.Warehouse, 1, confirm),
                HttpStatusCode.Conflict);
            Assert.Equal("C4_EXISTING_DELIVERY_PENDING", Reason(await Post(actor, root + "/deliver",
                last with { CommandId = Guid.NewGuid() }, HttpStatusCode.UnprocessableEntity)));
            Assert.Equal("C4_UNRESOLVED_DELIVERY_EVIDENCE", Reason(await Post(approval, root + "/amend-shipto",
                amend with { CommandId = Guid.NewGuid(), ExpectedVersion = 2 }, HttpStatusCode.UnprocessableEntity)));
            Assert.Single((await actor.GetFromJsonAsync<C4ArView[]>(root + $"/trips/{seed.Trip}/ar", ct))!);
            Assert.Equal(25m, await db.ExecuteScalarAsync<decimal>("SELECT ConsumedBaseQuantity FROM dbo.StockRight WHERE Id=@right", new { right }));
        }
        finally { await db.ExecuteAsync($"DROP TRIGGER dbo.[{trigger}];"); }

        var retry = await Post(actor, root + "/deliver", last);
        Assert.Equal(Source(retry), Source(await actor.GetFromJsonAsync<JsonElement>(root + $"/trips/{seed.Trip}/commands/{last.CommandId}", ct)));
        var receipts = (await actor.GetFromJsonAsync<C4ArView[]>(root + $"/trips/{seed.Trip}/ar", ct))!;
        Assert.Equal(2, receipts.Length);
        Assert.Contains(receipts, x => x.Quantity == 25m && x.ShipTo == ar.ShipTo);
        Assert.Contains(receipts, x => x.Quantity == 15m && x.ShipTo.StartsWith("C4 dia chi moi", StringComparison.Ordinal));
        Assert.Equal(572m, receipts.Sum(x => x.Amount));
        Assert.Equal("C4_NO_UNDELIVERED_SCOPE", Reason(await Post(approval, root + "/amend-shipto",
            amend with { CommandId = Guid.NewGuid(), ExpectedVersion = 2 }, HttpStatusCode.UnprocessableEntity)));
    }

    [Fact]
    public async Task C3_load_workspace_requires_a_current_trip_writer()
    {
        using var factory = fixture.CreateFactory(c2Enabled: true, dedicatedC2: true, c3Enabled: true);
        await using var db = new SqlConnection(fixture.TenantAConnectionString);
        var seed = await Seed(db);
        using var actor = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a", seed.Actor, ["warehouse"]);
        const string root = "/api/synthetic/app02c3";
        var rows = await actor.GetFromJsonAsync<JsonElement>(root + "/trips", ct);
        Assert.Empty(rows.EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await actor.PostAsJsonAsync(root + "/load",
            new { commandId = Guid.NewGuid(), tripId = seed.Trip,
                lines = new[] { new { rightId = Guid.NewGuid(), expectedWarehouseHeld = 1m, quantity = 1m } } }, ct)).StatusCode);
        await db.ExecuteAsync("INSERT dbo.C3TripWriter VALUES(N'tenant-a',@Trip,@Actor);", seed);
        await using var peerDb = new SqlConnection(fixture.TenantBConnectionString);
        await peerDb.ExecuteAsync("""
            INSERT dbo.App02ActorRoleGrant(TenantId,ActorId,RoleCode,IsActive,ChangedBy)
            VALUES(N'tenant-b',@Actor,N'warehouse',1,N'c3-test')
            """, seed);
        using var peer = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-b", seed.Actor, ["warehouse"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await peer.GetAsync(root + $"/trips/{seed.Trip}/rights", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await peer.PostAsJsonAsync(root + "/load",
            new C3LoadInput(Guid.NewGuid(), seed.Trip, [new(Guid.NewGuid(), 1m, 1m)]), ct)).StatusCode);
    }

    [Fact]
    public async Task C3_competing_batches_and_retry_after_multiline_rollback_preserve_reservation_and_custody()
    {
        using var factory = fixture.CreateFactory(c2Enabled: true, c3Enabled: true);
        await using var db = new SqlConnection(fixture.TenantAConnectionString);
        var seed = await Seed(db);
        await db.ExecuteAsync("""
            INSERT dbo.C3TripWriter VALUES(N'tenant-a',@Trip,@Actor);
            INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,SnapshotCode,BaseQuantity,CostAmount)
            VALUES(N'tenant-a',@Warehouse,@Sku,N'C3-test-v1',100,1000),(N'tenant-a',@Vehicle,@Sku,N'C3-test-v1',0,0);
            INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,OnHandBaseQuantity,OnHandCostAmount)
            VALUES(N'tenant-a',@Warehouse,@Sku,100,1000),(N'tenant-a',@Vehicle,@Sku,0,0);
            """, seed);
        using var actor = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a", seed.Actor, ["sales", "warehouse"]);
        Guid order = await Draft(actor, seed, await Direct(actor, seed));
        Guid confirm = Source(await Post(actor, Root + "/confirm", new { commandId = Guid.NewGuid(), orderId = order }));
        Guid line = (await Order(actor, order)).GetProperty("lines")[0].GetProperty("lineId").GetGuid();
        foreach (int quantity in new[] { 20, 20 })
            await Post(actor, Root + "/reserve", new C2ReserveInput(Guid.NewGuid(), order, line, seed.Warehouse, quantity, confirm));

        const string root = "/api/synthetic/app02c3";
        string trip = root + $"/trips/{seed.Trip}";
        Guid[] rights = (await actor.GetFromJsonAsync<C3RightView[]>(trip + "/rights", ct))!.Select(x => x.Id).Order().ToArray();
        Assert.Equal(2, rights.Length);
        C3LoadInput attempt = new(Guid.NewGuid(), seed.Trip, [new(rights[0], 20m, 5m)]);
        C3LoadInput rival = attempt with { CommandId = Guid.NewGuid() };
        var replies = await Task.WhenAll(actor.PostAsJsonAsync(root + "/load", attempt, ct), actor.PostAsJsonAsync(root + "/load", rival, ct));
        using var first = replies[0];
        using var second = replies[1];
        Assert.Single(replies, r => r.StatusCode == HttpStatusCode.OK);
        var denied = Assert.Single(replies, r => r.StatusCode == HttpStatusCode.UnprocessableEntity);
        Assert.Equal("C3_STALE_WAREHOUSE_HELD", Reason(await denied.Content.ReadFromJsonAsync<JsonElement>(ct)));
        var winner = first.IsSuccessStatusCode ? attempt : rival;
        Assert.True((await Post(actor, root + "/load", winner)).GetProperty("replayed").GetBoolean());
        await Post(actor, root + "/load", winner with { Lines = [new(rights[0], 20m, 6m)] }, HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.NotFound, (await actor.GetAsync(Root + $"/commands/{winner.CommandId}", ct)).StatusCode);

        var batch = new C3LoadInput(Guid.NewGuid(), seed.Trip, [new(rights[1], 20m, 13m), new(rights[0], 15m, 2m)]);
        string trigger = "TR_C3Abort_" + seed.Sku.ToString("N");
        var before = await LoadState(db, seed);
        // The failing right sorts after the first right, so a movement and both of its ledger legs have run.
        await db.ExecuteAsync($"""
            CREATE TRIGGER dbo.[{trigger}] ON dbo.PhysicalStockMovement AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.TripManifestLine l
                    ON l.TenantId=i.TenantId AND l.Id=i.TripManifestLineId
                    WHERE l.SkuId='{seed.Sku:D}' AND i.BaseQuantity=13)
                    THROW 51603, 'Synthetic late abort', 1;
            END;
            """);
        try
        {
            var fault = await Assert.ThrowsAsync<SqlException>(() => actor.PostAsJsonAsync(root + "/load", batch, ct));
            Assert.Equal(51603, fault.Number);
            Assert.Equal(HttpStatusCode.NotFound, (await actor.GetAsync(trip + $"/commands/{batch.CommandId}", ct)).StatusCode);
            Assert.Equal(before, await LoadState(db, seed));
        }
        finally { await db.ExecuteAsync($"DROP TRIGGER dbo.[{trigger}];"); }

        var accepted = await Post(actor, root + "/load", batch);
        Assert.False(accepted.GetProperty("replayed").GetBoolean());
        var reordered = batch with { Lines = batch.Lines!.Reverse().ToArray() };
        Assert.Equal(Source(accepted), Source(await Post(actor, root + "/load", reordered)));
        Assert.Equal("80.000|20.000|800.0000|20.000|200.0000|40.000|0.000|3|6|2|3",
            await LoadState(db, seed));

        var empty = new C3LoadInput(Guid.NewGuid(), seed.Trip, [null]);
        Assert.Equal("INVALID_LOAD_LINES", Reason(await Post(actor, root + "/load", empty, HttpStatusCode.UnprocessableEntity)));
        await Post(actor, root + "/load", empty with { Lines = [new(rights[0], 13m, 1m)] }, HttpStatusCode.Conflict);

        using var legacyFactory = fixture.CreateFactory();
        using var legacy = SqlScratchFixture.WithTenant(legacyFactory.CreateClient(), "tenant-a", seed.Actor, ["warehouse"]);
        Guid manifest = await db.QuerySingleAsync<Guid>("SELECT Id FROM dbo.TripManifest WHERE LoadBusinessSourceId=@Id", new { Id = Source(accepted) });
        var unload = await Post(legacy, "/api/synthetic/app01d/commands/unload-trip-remainder", new
        {
            producerId = seed.Actor, commandId = Guid.NewGuid().ToString("N"), sourceNumber = Guid.NewGuid().ToString("N"),
            tripId = seed.Trip, manifestId = manifest
        });
        Assert.Equal("LOADED_MANIFEST_NOT_ELIGIBLE", Reason(unload));

        await db.ExecuteAsync("DELETE dbo.C3TripWriter WHERE TenantId='tenant-a' AND TripId=@Trip", seed);
        Assert.Equal(HttpStatusCode.Forbidden, (await actor.GetAsync(trip + $"/commands/{batch.CommandId}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await actor.PostAsJsonAsync(root + "/load", batch, ct)).StatusCode);
    }

    private static Task<string> LoadState(SqlConnection db, Scene seed) => db.QuerySingleAsync<string>("""
        SELECT CONCAT(p.OnHandBaseQuantity,'|',p.AllocatedBaseQuantity,'|',m.OnHandCostAmount,'|',
            v.OnHandBaseQuantity,'|',v.OnHandCostAmount,'|',
            (SELECT SUM(GrantedBaseQuantity) FROM dbo.StockRight WHERE SkuId=@Sku),'|',
            (SELECT SUM(ConsumedBaseQuantity+ReleasedBaseQuantity) FROM dbo.StockRight WHERE SkuId=@Sku),'|',
            (SELECT COUNT(*) FROM dbo.PhysicalStockMovement x JOIN dbo.TripManifestLine l
                ON l.TenantId=x.TenantId AND l.Id=x.TripManifestLineId WHERE l.SkuId=@Sku),'|',
            (SELECT COUNT(*) FROM dbo.LedgerEffect WHERE SkuId=@Sku AND LedgerKind='PHYSICAL_STOCK'),'|',
            (SELECT COUNT(*) FROM dbo.TripManifest WHERE TripId=@Trip),'|',
            (SELECT COUNT(*) FROM dbo.C2CommandScope WHERE LoadTripId=@Trip))
        FROM dbo.StockPool p
        JOIN dbo.PhysicalStockBalance m ON m.TenantId=p.TenantId AND m.StockLocationId=p.StockLocationId AND m.SkuId=p.SkuId
        JOIN dbo.PhysicalStockBalance v ON v.TenantId=p.TenantId AND v.StockLocationId=@Vehicle AND v.SkuId=p.SkuId
        WHERE p.StockLocationId=@Warehouse AND p.SkuId=@Sku
        """, seed);

    [Fact]
    public async Task Catalog_and_commands_require_current_grants_and_owned_tenant()
    {
        using var factory = fixture.CreateFactory(c2Enabled: true, dedicatedC2: true);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Root + "/catalog", ct)).StatusCode);
        using var actor = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a", "c2-authority", ["sales"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await actor.GetAsync(Root + "/catalog", ct)).StatusCode);
        await using var db = new SqlConnection(fixture.TenantAConnectionString);
        await db.ExecuteAsync("""
            INSERT dbo.App02ActorRoleGrant(TenantId,ActorId,RoleCode,IsActive,ChangedBy)
            VALUES(N'tenant-a',N'c2-authority',N'sales',1,N'c2-test')
            """);
        Assert.Equal(HttpStatusCode.OK, (await actor.GetAsync(Root + "/catalog", ct)).StatusCode);
        var reserve = new { commandId = Guid.NewGuid(), orderId = Guid.NewGuid() };
        Assert.Equal(HttpStatusCode.Forbidden, (await actor.PostAsJsonAsync(Root + "/reserve", reserve, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await actor.PostAsJsonAsync("/api/synthetic/app01a/commands/grant-b-quota", reserve, ct)).StatusCode);
        using var peer = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-b", "c2-authority", ["sales"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await peer.GetAsync(Root + "/catalog", ct)).StatusCode);
    }

    [Fact]
    public async Task Confirm_freezes_policy_and_direct_ShipTo_and_Reserve_replays_under_current_authority()
    {
        using var factory = fixture.CreateFactory(c2Enabled: true);
        await using var db = new SqlConnection(fixture.TenantAConnectionString);
        var seed = await Seed(db);
        using var actor = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a", seed.Actor, ["sales", "warehouse"]);
        var selection = await Direct(actor, seed);
        var draft = await Draft(actor, seed, selection);
        Assert.Equal(40m, await db.QuerySingleAsync<decimal>("""
            SELECT l.BaseQuantity FROM dbo.BusinessSourceLine l JOIN dbo.BusinessSource s
                ON s.TenantId=l.TenantId AND s.Id=l.BusinessSourceId
            WHERE s.Id=@draft AND s.SourceKind='C2_A_DRAFT' AND l.LineNumber=1
                AND NOT EXISTS(SELECT 1 FROM dbo.LedgerEffect e
                    WHERE e.TenantId=l.TenantId AND e.BusinessSourceLineId=l.Id)
            """, new { draft }));
        var confirm = new { commandId = Guid.NewGuid(), orderId = draft };
        Guid source = Source(await Post(actor, Root + "/confirm", confirm));
        Assert.True((await Post(actor, Root + "/confirm", confirm)).GetProperty("replayed").GetBoolean());
        Assert.Equal(0m, await db.ExecuteScalarAsync<decimal>(
            "SELECT AllocatedBaseQuantity FROM dbo.StockPool WHERE SkuId=@Sku", seed));
        Assert.Equal("CONFIRMED_UNRESERVED", (await Order(actor, draft)).GetProperty("status").GetString());
        Assert.Equal("C2_SHIP_TO_ALREADY_BOUND", Reason(await Post(actor, Root + "/confirm", new
        {
            commandId = Guid.NewGuid(), orderId = await Draft(actor, seed, selection)
        }, HttpStatusCode.UnprocessableEntity)));

        await AddPolicy(db, seed, 2, "UNSET");
        var denied = await Draft(actor, seed, await Direct(actor, seed));
        Assert.Equal("C2_POLICY_REQUIRED", Reason(await Post(actor, Root + "/confirm",
            new { commandId = Guid.NewGuid(), orderId = denied }, HttpStatusCode.UnprocessableEntity)));
        var line = (await Order(actor, draft)).GetProperty("lines")[0];
        Assert.Equal(15m, line.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(7, line.GetProperty("termsDays").GetInt32());
        Assert.Equal(1, line.GetProperty("policyVersion").GetInt32());
        Guid lineId = line.GetProperty("lineId").GetGuid();
        var reserve = new { commandId = Guid.NewGuid(), orderId = draft, lineId, locationId = seed.Warehouse,
            quantity = 15m, confirmSourceId = source };
        using var peer = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-b", seed.Actor, ["sales", "warehouse"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await peer.PostAsJsonAsync(Root + "/reserve", reserve, ct)).StatusCode);

        Guid rightSource = Source(await Post(actor, Root + "/reserve", reserve));
        var replay = await Post(actor, Root + "/reserve", reserve);
        Assert.Equal(rightSource, Source(replay));
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        Assert.Equal("CONFLICT", (await Post(actor, Root + "/reserve",
            new { reserve.commandId, reserve.orderId, reserve.lineId, reserve.locationId, quantity = 16m, reserve.confirmSourceId },
            HttpStatusCode.Conflict)).GetProperty("status").GetString());
        Assert.Equal("CONFIRMED_PARTIALLY_RESERVED", (await Order(actor, draft)).GetProperty("status").GetString());
        Assert.Equal(15m, await db.ExecuteScalarAsync<decimal>("SELECT AllocatedBaseQuantity FROM dbo.StockPool WHERE SkuId=@Sku", seed));
        Assert.Equal(0m, await db.ExecuteScalarAsync<decimal>(
            "SELECT SUM(ConsumedBaseQuantity+ReleasedBaseQuantity) FROM dbo.StockRight WHERE SkuId=@Sku", seed));

        await db.ExecuteAsync("""
            UPDATE dbo.App02ActorRoleGrant SET IsActive=0 WHERE TenantId='tenant-a' AND ActorId=@Actor AND RoleCode='warehouse'
            """, seed);
        Assert.Equal(HttpStatusCode.Forbidden, (await actor.PostAsJsonAsync(Root + "/reserve", reserve, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await actor.GetAsync(Root + $"/commands/{reserve.commandId}", ct)).StatusCode);
    }

    [Fact]
    public async Task Approval_without_effective_checker_and_superseded_registered_address_cannot_confirm()
    {
        using var factory = fixture.CreateFactory(c2Enabled: true);
        await using var db = new SqlConnection(fixture.TenantAConnectionString);
        var seed = await Seed(db);
        using var actor = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a", seed.Actor, ["sales", "warehouse"]);
        var registration = await Post(actor, "/api/synthetic/app02c1/registered-ship-tos/save",
            new { commandId = Guid.NewGuid(), customerId = seed.Customer, address = Address });
        Guid registeredId = registration.GetProperty("id").GetGuid();
        var selected = await Post(actor, "/api/synthetic/app02c1/ship-to-selections", new
        {
            commandId = Guid.NewGuid(), customerId = seed.Customer, registeredId, revision = 1
        });
        Guid draft = await Draft(actor, seed, selected.GetProperty("id").GetGuid());
        await Post(actor, "/api/synthetic/app02c1/registered-ship-tos/save",
            new { commandId = Guid.NewGuid(), customerId = seed.Customer, id = registeredId, revision = 1, address = Address });
        Assert.Equal("C2_SHIP_TO_STALE", Reason(await Post(actor, Root + "/confirm",
            new { commandId = Guid.NewGuid(), orderId = draft }, HttpStatusCode.UnprocessableEntity)));
        var direct = await Draft(actor, seed, await Direct(actor, seed));
        await AddPolicy(db, seed, 2, "APPROVED", expired: true);
        Assert.Equal("C2_POLICY_REQUIRED", Reason(await Post(actor, Root + "/confirm",
            new { commandId = Guid.NewGuid(), orderId = direct }, HttpStatusCode.UnprocessableEntity)));
        await AddPolicy(db, seed, 3, "APPROVED", checker: seed.Actor);
        Assert.Equal("C2_POLICY_REQUIRED", Reason(await Post(actor, Root + "/confirm",
            new { commandId = Guid.NewGuid(), orderId = direct }, HttpStatusCode.UnprocessableEntity)));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.AOrder WHERE CustomerId=@Customer", seed));
    }

    [Fact]
    public async Task Competing_A_reservation_and_legacy_B_quota_share_the_pool()
    {
        using var factory = fixture.CreateFactory(c2Enabled: true);
        await using var db = new SqlConnection(fixture.TenantAConnectionString);
        var seed = await Seed(db);
        using var actor = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a", seed.Actor, ["sales", "warehouse"]);
        Guid draft = await Draft(actor, seed, await Direct(actor, seed), 70m);
        Guid source = Source(await Post(actor, Root + "/confirm", new { commandId = Guid.NewGuid(), orderId = draft }));
        Guid lineId = (await Order(actor, draft)).GetProperty("lines")[0].GetProperty("lineId").GetGuid();
        var a = actor.PostAsJsonAsync(Root + "/reserve", new { commandId = Guid.NewGuid(), orderId = draft, lineId,
            locationId = seed.Warehouse, quantity = 70m, confirmSourceId = source }, ct);
        var b = actor.PostAsJsonAsync("/api/synthetic/app01a/commands/grant-b-quota", new
        {
            producerId = seed.Actor, commandId = Guid.NewGuid().ToString("N"), sourceNumber = Guid.NewGuid().ToString("N"),
            tripId = seed.Trip, stockLocationId = seed.Warehouse, skuId = seed.Sku, baseQuantity = 60m
        }, ct);
        await Task.WhenAll(a, b);
        using var aResponse = await a;
        using var bResponse = await b;
        var outcomes = new[] { await aResponse.Content.ReadFromJsonAsync<JsonElement>(ct),
            await bResponse.Content.ReadFromJsonAsync<JsonElement>(ct) };
        Assert.Single(outcomes, result => result.GetProperty("status").GetString() == "APPLIED");
        Assert.Single(outcomes, result => Reason(result) == "STOCK_INSUFFICIENT");
        Assert.Contains(await db.ExecuteScalarAsync<decimal>(
            "SELECT AllocatedBaseQuantity FROM dbo.StockPool WHERE SkuId=@Sku", seed), new[] { 60m, 70m });
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.StockRight WHERE SkuId=@Sku", seed));
    }

    [Fact]
    public async Task Failed_confirm_rolls_back_snapshot_binding_and_command_then_retries_once()
    {
        using var factory = fixture.CreateFactory(c2Enabled: true);
        await using var db = new SqlConnection(fixture.TenantAConnectionString);
        var seed = await Seed(db);
        using var actor = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a", seed.Actor, ["sales", "warehouse"]);
        Guid draft = await Draft(actor, seed, await Direct(actor, seed));
        Guid commandId = Guid.NewGuid();
        var confirm = new { commandId, orderId = draft };
        await db.ExecuteAsync("""
            CREATE TRIGGER dbo.C2_TestPolicyFreezeFault ON dbo.C2LinePolicy AFTER INSERT AS
            BEGIN
                THROW 51024, 'C.2 synthetic fault', 1;
            END
            """);
        try
        {
            try
            {
                using var failed = await actor.PostAsJsonAsync(Root + "/confirm", confirm, ct);
                Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            }
            catch (SqlException error) { Assert.Equal(51024, error.Number); }
        }
        finally { await db.ExecuteAsync("DROP TRIGGER dbo.C2_TestPolicyFreezeFault"); }
        Assert.Equal(HttpStatusCode.NotFound, (await actor.GetAsync(Root + $"/commands/{commandId}", ct)).StatusCode);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.ShipToSnapshot WHERE CustomerId=@Customer", seed));
        Assert.Equal("DRAFT", (await Order(actor, draft)).GetProperty("status").GetString());
        Assert.False((await Post(actor, Root + "/confirm", confirm)).GetProperty("replayed").GetBoolean());
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.ShipToSnapshot WHERE CustomerId=@Customer", seed));
    }

    [Fact]
    public async Task Fully_reserved_C2_order_is_not_eligible_for_legacy_manifest_load()
    {
        using var factory = fixture.CreateFactory(c2Enabled: true);
        await using var db = new SqlConnection(fixture.TenantAConnectionString);
        var seed = await Seed(db);
        using var actor = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a", seed.Actor, ["sales", "warehouse"]);
        Guid draft = await Draft(actor, seed, await Direct(actor, seed));
        Guid source = Source(await Post(actor, Root + "/confirm", new { commandId = Guid.NewGuid(), orderId = draft }));
        Guid lineId = (await Order(actor, draft)).GetProperty("lines")[0].GetProperty("lineId").GetGuid();
        await Post(actor, Root + "/reserve", new { commandId = Guid.NewGuid(), orderId = draft, lineId,
            locationId = seed.Warehouse, quantity = 40m, confirmSourceId = source });
        Assert.Equal("CONFIRMED_FULLY_RESERVED", (await Order(actor, draft)).GetProperty("status").GetString());
        Guid manifest = Guid.NewGuid();
        await db.ExecuteAsync("""
            INSERT dbo.TripManifest(Id,TenantId,ManifestNumber,TripId,WarehouseLocationId,VehicleLocationId)
            VALUES(@Manifest,N'tenant-a',@Actor,@Trip,@Warehouse,@Vehicle);
            INSERT dbo.TripManifestLine(Id,TenantId,TripManifestId,TripId,WarehouseLocationId,VehicleLocationId,
                LineNumber,SkuId,StockRightId,RightKind,PlannedBaseQuantity)
            SELECT NEWID(),TenantId,@Manifest,TripId,@Warehouse,@Vehicle,1,SkuId,Id,RightKind,GrantedBaseQuantity
            FROM dbo.StockRight WHERE TenantId=N'tenant-a' AND SkuId=@Sku;
            """, new { Manifest = manifest, seed.Actor, seed.Trip, seed.Warehouse, seed.Vehicle, seed.Sku });
        var outcome = await Post(actor, "/api/synthetic/app01b/commands/load-manifest", new
        {
            producerId = seed.Actor, commandId = Guid.NewGuid().ToString("N"), sourceNumber = Guid.NewGuid().ToString("N"),
            manifestId = manifest
        });
        Assert.Equal("MANIFEST_RIGHT_NOT_ELIGIBLE", Reason(outcome));
        Assert.Equal(0m, await db.ExecuteScalarAsync<decimal>(
            "SELECT LoadedBaseQuantity FROM dbo.TripManifestLine WHERE TripManifestId=@manifest", new { manifest }));
    }

    [Fact]
    public async Task Gross_is_calculated_from_approved_discount_and_tax_and_capped_before_Confirm()
    {
        using var factory = fixture.CreateFactory(c2Enabled: true);
        await using var db = new SqlConnection(fixture.TenantAConnectionString);
        var seed = await Seed(db);
        using var actor = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a", seed.Actor, ["sales"]);
        await AddPolicy(db, seed, 2, "APPROVED", discount: 2m, tax: 0.1m, creditCap: 571.9999m);
        var draft = await Draft(actor, seed, await Direct(actor, seed));
        var denied = await Post(actor, Root + "/confirm", new { commandId = Guid.NewGuid(), orderId = draft }, HttpStatusCode.UnprocessableEntity);
        Assert.Equal("C2_CREDIT_CAP_EXCEEDED", Reason(denied));
        Assert.Equal("DRAFT", (await Order(actor, draft)).GetProperty("status").GetString());
        await AddPolicy(db, seed, 3, "APPROVED", discount: 2m, tax: 0.1m, creditCap: 572m);
        await Post(actor, Root + "/confirm", new { commandId = Guid.NewGuid(), orderId = draft });
        Assert.Equal(572m, await db.ExecuteScalarAsync<decimal>("""
            SELECT p.GrossAmount FROM dbo.C2LinePolicy p JOIN dbo.AOrderLine l
                ON l.TenantId=p.TenantId AND l.Id=p.OrderLineId WHERE l.AOrderId=@draft AND p.PolicyVersion=3
            """, new { draft }));
    }

    [Fact]
    public async Task Draft_and_confirmed_quantities_cannot_be_reinterpreted_under_a_changed_base_unit()
    {
        using var factory = fixture.CreateFactory(c2Enabled: true);
        await using var db = new SqlConnection(fixture.TenantAConnectionString);
        var seed = await Seed(db);
        using var actor = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a", seed.Actor, ["sales", "warehouse"]);
        Guid draft = await Draft(actor, seed, await Direct(actor, seed));
        await db.ExecuteAsync("UPDATE dbo.Sku SET BaseUom=N'CASE' WHERE Id=@Sku", seed);
        Assert.Equal("EA", (await Order(actor, draft)).GetProperty("lines")[0].GetProperty("baseUom").GetString());
        Assert.Equal("C2_BASE_UOM_CHANGED", Reason(await Post(actor, Root + "/confirm",
            new { commandId = Guid.NewGuid(), orderId = draft }, HttpStatusCode.UnprocessableEntity)));
        await db.ExecuteAsync("UPDATE dbo.Sku SET BaseUom=N'EA' WHERE Id=@Sku", seed);
        Guid source = Source(await Post(actor, Root + "/confirm", new { commandId = Guid.NewGuid(), orderId = draft }));
        Guid lineId = (await Order(actor, draft)).GetProperty("lines")[0].GetProperty("lineId").GetGuid();
        await db.ExecuteAsync("UPDATE dbo.Sku SET BaseUom=N'CASE' WHERE Id=@Sku", seed);
        Assert.Equal("C2_FROZEN_LINE_INVALID", Reason(await Post(actor, Root + "/reserve",
            new { commandId = Guid.NewGuid(), orderId = draft, lineId, locationId = seed.Warehouse,
                quantity = 40m, confirmSourceId = source }, HttpStatusCode.UnprocessableEntity)));
        Assert.Equal(0m, await db.ExecuteScalarAsync<decimal>("SELECT AllocatedBaseQuantity FROM dbo.StockPool WHERE SkuId=@Sku", seed));
    }

    [Fact]
    public async Task Draft_lines_cannot_be_appended_after_commit()
    {
        using var factory = fixture.CreateFactory(c2Enabled: true);
        await using var db = new SqlConnection(fixture.TenantAConnectionString);
        var seed = await Seed(db);
        using var actor = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-a", seed.Actor, ["sales"]);
        Guid draft = await Draft(actor, seed, await Direct(actor, seed));
        Guid other = Guid.NewGuid();
        await db.ExecuteAsync("INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom) VALUES(@other,N'tenant-a',@code,N'DEV',N'EA')",
            new { other, code = other.ToString("N") });
        var failure = await Assert.ThrowsAsync<SqlException>(() => db.ExecuteAsync("""
            INSERT dbo.C2OrderDraftLine(TenantId,DraftId,LineNumber,SkuId,BaseQuantity,BaseUom)
            VALUES(N'tenant-a',@draft,2,@other,1,N'EA')
            """, new { draft, other }));
        Assert.Equal(51025, failure.Number);
    }

    private sealed record Scene(Guid Customer, Guid Sku, Guid Warehouse, Guid Vehicle, Guid Trip, string Actor);
    private static readonly object Address = new
    {
        country = "VN", region = "HCM", locality = "Khu DEV", line = "Dia chi DEV", recipient = "Nguoi Nhan"
    };

    private static async Task<Scene> Seed(SqlConnection db)
    {
        var scene = new Scene(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "c2-" + Guid.NewGuid().ToString("N")[..10]);
        await db.ExecuteAsync("""
            INSERT dbo.App02ActorRoleGrant(TenantId,ActorId,RoleCode,IsActive,ChangedBy)
            VALUES(N'tenant-a',@Actor,N'sales',1,N'c2-test'),(N'tenant-a',@Actor,N'warehouse',1,N'c2-test');
            INSERT dbo.Customer(Id,TenantId,Code,Name) VALUES(@Customer,N'tenant-a',@Actor,N'C.2 DEV');
            INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom) VALUES(@Sku,N'tenant-a',@Actor,N'Hàng DEV',N'EA');
            INSERT dbo.StockLocation(Id,TenantId,Code,Kind)
            VALUES(@Warehouse,N'tenant-a',@Actor+N'-MAIN',N'WAREHOUSE'),(@Vehicle,N'tenant-a',@Actor+N'-TRUCK',N'VEHICLE');
            INSERT dbo.Trip(Id,TenantId,Code,BusinessDate,VehicleLocationId)
            VALUES(@Trip,N'tenant-a',@Actor,CAST(SYSUTCDATETIME() AS date),@Vehicle);
            INSERT dbo.StockPool(TenantId,StockLocationId,SkuId,OnHandBaseQuantity)
            VALUES(N'tenant-a',@Warehouse,@Sku,100);
            """, scene);
        await AddPolicy(db, scene, 1, "APPROVED");
        return scene;
    }

    private static Task AddPolicy(SqlConnection db, Scene seed, int version, string decision,
        bool expired = false, string checker = "c2-test-checker",
        decimal discount = 0, decimal tax = 0, decimal creditCap = 2000) => db.ExecuteAsync("""
            DECLARE @now datetime2(7)=SYSUTCDATETIME();
            INSERT dbo.C2CommercialPolicy(TenantId,Id,CustomerId,SkuId,Version,Decision,EffectiveFromUtc,EffectiveUntilUtc,
                Maker,Checker,ApprovedAtUtc,EvidenceRef,UnitPrice,DiscountPerUnit,TaxRate,TermsDays,CreditCap,SellerTimeZone)
            VALUES(N'tenant-a',NEWID(),@Customer,@Sku,@Version,@Decision,DATEADD(day,-2,@now),DATEADD(day,@Until,@now),
                N'c2-test-maker',@Checker,DATEADD(day,-2,@now),N'c2-test-proof',15,@discount,@tax,7,@creditCap,'Asia/Ho_Chi_Minh');
            """, new { seed.Customer, seed.Sku, Version = version, Decision = decision, Until = expired ? -1 : 1, Checker = checker,
                discount, tax, creditCap });

    private async Task<Guid> Direct(HttpClient actor, Scene seed) => (await Post(actor, "/api/synthetic/app02c1/ship-to-selections",
        new { commandId = Guid.NewGuid(), customerId = seed.Customer, address = Address })).GetProperty("id").GetGuid();

    private async Task<Guid> Draft(HttpClient actor, Scene seed, Guid selectionId, decimal quantity = 40) =>
        Source(await Post(actor, Root + "/drafts", new { commandId = Guid.NewGuid(), customerId = seed.Customer,
            tripId = seed.Trip, selectionId, lines = new[] { new { skuId = seed.Sku, quantity } } }));

    private async Task<JsonElement> Order(HttpClient actor, Guid id) =>
        (await actor.GetFromJsonAsync<JsonElement[]>(Root + "/orders", ct))!.Single(o => o.GetProperty("id").GetGuid() == id);

    private async Task<JsonElement> Post(HttpClient actor, string path, object request, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await actor.PostAsJsonAsync(path, request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == expected, $"{path}: expected {expected}, got {response.StatusCode} {body}");
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    private static Guid Source(JsonElement outcome) => outcome.GetProperty("sourceId").GetGuid();
    private static string? Reason(JsonElement outcome) => outcome.GetProperty("reasonCode").GetString();
}
