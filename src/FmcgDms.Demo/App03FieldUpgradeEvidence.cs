using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static partial class App03FieldEvidence
{
    private const string C1 = "/api/synthetic/app02c1";
    private const string C2 = "/api/synthetic/app02c2";
    private const string C3 = "/api/synthetic/app02c3";
    private const string C4 = "/api/synthetic/app02c4";

    private static readonly string[] UpgradeTables =
    [
        "C2OrderDraft", "C2Confirmation", "AOrder", "AOrderLine", "AReservation",
        "AConfirmedLineSnapshot", "StockRight", "TripManifest", "TripManifestLine", "PhysicalStockMovement",
        "C4DeliveryEvidence", "SaleDelivery", "DeliveryStockIssue", "ReceivableOpenItem", "F03LotHold",
        "F03LotLoad", "F03LotIssue", "F04PriceLine", "F04DeliveryPricing", "BusinessCommand",
        "BusinessSource", "BusinessSourceLine", "LedgerEffect"
    ];

    internal static async Task<Predecessor> CheckPredecessor(DemoRun run, App03DevHost host, CancellationToken ct, bool withRemainder = false)
    {
        int loaded = withRemainder ? 3 : 2;
        using var health = await host.Client.GetFromJsonAsync<JsonDocument>(host.WebUrl + "/health/live", ct);
        using var noD2 = await Send(host, B + "/trips", ct);
        if (health?.RootElement.GetProperty("workspaceProfile").GetString() != App03A1Profile.Name
            || noD2.StatusCode != HttpStatusCode.NotFound)
            throw new InvalidOperationException("The predecessor must serve its A/F profile before the upgrade.");

        var catalog = await Read<C2Catalog>(host, C2 + "/catalog", ct);
        var customer = catalog.Customers.Single(row => row.Label.StartsWith("C2-FIXTURE", StringComparison.Ordinal));
        var trip = catalog.Trips.Single(row => row.Label == "DEV-20261123-A");
        var sku = catalog.Skus.Single(row => row.Label.StartsWith("R-05 ·", StringComparison.Ordinal));
        var warehouse = catalog.Warehouses.Single(row => row.Label == "C2-MAIN");
        var detail = await Read<C1Detail>(host, C1 + $"/customers/{customer.Id}", ct);
        var shipTo = detail.RegisteredShipTos.Single();
        var address = await Read<C1Outcome>(host, C1 + "/ship-to-selections", ct,
            new C1SelectionInput(Guid.NewGuid(), customer.Id, null, shipTo.Id, shipTo.Revision, null));
        var draft = await Read<BusinessCommandOutcome>(host, C2 + "/drafts", ct,
            new C2DraftInput(Guid.NewGuid(), customer.Id, trip.Id, address.Id!.Value, [new(sku.Id, loaded)]));
        Guid orderId = draft.SourceId!.Value;
        var confirmation = await Read<BusinessCommandOutcome>(host, C2 + "/confirm", ct,
            new C2ConfirmInput(Guid.NewGuid(), orderId));
        var order = (await Read<C2OrderView[]>(host, C2 + "/orders", ct)).Single(row => row.Id == orderId);
        var reservation = await Read<BusinessCommandOutcome>(host, C2 + "/reserve", ct,
            new C2ReserveInput(Guid.NewGuid(), orderId, order.Lines.Single().LineId!.Value, warehouse.Id, loaded,
                confirmation.SourceId!.Value), host.ReceiverToken);
        var right = (await Read<RightId[]>(host, C3 + $"/trips/{trip.Id}/rights", ct, token: host.ReceiverToken)).Single();
        var load = await Read<BusinessCommandOutcome>(host, C3 + "/load", ct,
            new C3LoadInput(Guid.NewGuid(), trip.Id, [new(right.Id, loaded, loaded)]), host.ReceiverToken);
        var line = (await Read<DeliveryLine[]>(host, C4 + $"/trips/{trip.Id}/lines", ct)).Single();
        var delivery = new C4DeliveryInput(Guid.NewGuid(), trip.Id, line.Id, line.ShipToVersion, 2);
        // Drop the response body. SQL independently identifies the posted source.
        using (var dropped = await Send(host, C4 + "/deliver", ct, delivery))
            dropped.EnsureSuccessStatusCode();
        var posted = (await Read<C4ArView[]>(host, C4 + $"/trips/{trip.Id}/ar", ct)).Single();
        await run.Verify("tenant-a", app03Session: true);
        await using var db = new SqlConnection(run.Connection("tenant-a"));
        var sql = await db.QuerySingleAsync<PostedPredecessor>(new CommandDefinition("""
            SELECT d.BusinessSourceId AS SourceId,d.AOrderId AS OrderId,d.StockRightId,s.BusinessCommandId AS CommandRowId,
                d.BaseQuantity AS Delivered,i.OutstandingAmount,f.GrossAmount,r.ConsumedBaseQuantity AS Consumed,
                m.LoadedBaseQuantity AS Loaded
            FROM dbo.SaleDelivery d
            JOIN dbo.BusinessSource s ON s.TenantId=d.TenantId AND s.Id=d.BusinessSourceId
            JOIN dbo.ReceivableOpenItem i ON i.TenantId=d.TenantId AND i.SaleDeliveryId=d.Id
            JOIN dbo.F04DeliveryPricing f ON f.TenantId=d.TenantId AND f.DeliveryId=d.Id
            JOIN dbo.StockRight r ON r.TenantId=d.TenantId AND r.Id=d.StockRightId
            JOIN dbo.TripManifestLine m ON m.TenantId=d.TenantId AND m.Id=d.TripManifestLineId
            WHERE d.TenantId='tenant-a' AND d.TripManifestLineId=@ManifestLineId;
            """, new { delivery.ManifestLineId }, cancellationToken: ct));
        if (reservation.Status != "APPLIED" || load.Status != "APPLIED" || posted.Quantity != 2
            || posted.Outstanding != 20000 || posted.Cost != 6667 || posted.Currency != "VND"
            || sql.SourceId != posted.SourceId || sql.OrderId != orderId || sql.StockRightId != right.Id
            || sql is not { Delivered: 2, OutstandingAmount: 20000, GrossAmount: 20000, Consumed: 2 } || sql.Loaded != loaded)
            throw new InvalidOperationException("The predecessor delivery, rights or AR fixture was not posted.");

        return new(orderId, confirmation.SourceId.Value, reservation.SourceId!.Value, load.SourceId!.Value,
            right.Id, delivery, posted, sql, await CaptureUpgrade(run, ct));
    }

    internal static async Task<UpgradeReview> CheckUpgrade(DemoRun run, App03DevHost host, Predecessor before,
        CancellationToken ct)
    {
        var after = await CaptureUpgrade(run, ct);
        var delivery = before.Delivery;
        var outcome = await Read<BusinessCommandOutcome>(host,
            C4 + $"/trips/{delivery.TripId}/commands/{delivery.CommandId}", ct);
        var replay = await Read<BusinessCommandOutcome>(host, C4 + "/deliver", ct, delivery);
        var ar = (await Read<C4ArView[]>(host, C4 + $"/trips/{delivery.TripId}/ar", ct)).Single();
        var afterReplay = await CaptureUpgrade(run, ct);
        if (!Same(before.Snapshot, after) || !Same(after, afterReplay)
            || outcome is not { Status: "APPLIED", Replayed: true } || outcome.SourceId != before.Ar.SourceId
            || replay is not { Status: "APPLIED", Replayed: true } || replay.SourceId != before.Ar.SourceId
            || ar != before.Ar)
            throw new InvalidOperationException("Upgrade or replay changed predecessor source, counters, price, AR or stock.");
        return new(before, after, afterReplay);

        static bool Same(UpgradeSnapshot a, UpgradeSnapshot b) =>
            a.Tables.SequenceEqual(b.Tables) && a.Stock.SequenceEqual(b.Stock);
    }

    private static async Task<UpgradeSnapshot> CaptureUpgrade(DemoRun run, CancellationToken ct)
    {
        var stock = new List<PredecessorStock>();
        foreach (string tenant in DemoRun.Tenants)
        {
            await run.Verify(tenant, app03Session: true);
            await using var db = new SqlConnection(run.Connection(tenant));
            stock.AddRange(await db.QueryAsync<PredecessorStock>(new CommandDefinition("""
                SELECT l.TenantId,l.Id AS LocationId,k.Id AS SkuId,o.BaseQuantity AS OpeningQuantity,
                    o.CostAmount AS OpeningCost,p.OnHandBaseQuantity AS PoolQuantity,p.AllocatedBaseQuantity AS Allocated,
                    b.OnHandBaseQuantity AS PhysicalQuantity,b.OnHandCostAmount AS PhysicalCost
                FROM dbo.PhysicalStockOpening o
                JOIN dbo.Sku k ON k.TenantId=o.TenantId AND k.Id=o.SkuId
                JOIN dbo.StockLocation l ON l.TenantId=o.TenantId AND l.Id=o.StockLocationId
                JOIN dbo.PhysicalStockBalance b ON b.TenantId=o.TenantId AND b.SkuId=o.SkuId AND b.StockLocationId=o.StockLocationId
                LEFT JOIN dbo.StockPool p ON p.TenantId=o.TenantId AND p.SkuId=o.SkuId AND p.StockLocationId=o.StockLocationId
                WHERE o.TenantId=@Tenant AND k.Code='R-05'
                ORDER BY l.Id;
                """, new { Tenant = tenant }, cancellationToken: ct)));
        }
        return new(await DevF01Evidence.Capture(run, UpgradeTables), stock.ToArray());
    }

    private static async Task<T> Read<T>(App03DevHost host, string path, CancellationToken ct,
        object? input = null, string? token = null)
    {
        using var response = await Send(host, path, ct, input, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new InvalidOperationException($"Missing upgrade evidence response for {path}.");
    }

    internal sealed record Predecessor(Guid OrderId, Guid ConfirmSourceId, Guid ReserveSourceId, Guid LoadSourceId,
        Guid StockRightId, C4DeliveryInput Delivery, C4ArView Ar, PostedPredecessor Sql, UpgradeSnapshot Snapshot);
    private sealed record DeliveryLine(Guid Id, int ShipToVersion);
    private sealed record RightId(Guid Id);
    internal sealed record PostedPredecessor(Guid SourceId, Guid OrderId, Guid StockRightId, Guid CommandRowId,
        decimal Delivered, decimal OutstandingAmount, long GrossAmount, decimal Consumed, decimal Loaded);
    internal sealed record PredecessorStock(string TenantId, Guid LocationId, Guid SkuId,
        decimal OpeningQuantity, decimal OpeningCost, decimal? PoolQuantity, decimal? Allocated,
        decimal PhysicalQuantity, decimal PhysicalCost);
    internal sealed record UpgradeSnapshot(DevF01Evidence.TableState[] Tables, PredecessorStock[] Stock);
    internal sealed record UpgradeReview(Predecessor Before, UpgradeSnapshot After, UpgradeSnapshot AfterReplay);
}
