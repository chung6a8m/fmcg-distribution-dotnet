using System.Security.Claims;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed record C2HandoffActions(bool Confirm, bool Reserve, bool Load);
public sealed record C2LineHandoff(decimal WarehouseHeld, decimal Unreserved, C2QuantitySnapshot? Quantity,
    IReadOnlyList<C2LotStep> Lots);
public sealed record C2QuantitySnapshot(string UomCode, decimal EnteredQuantity, decimal Factor);
public sealed record C2LotStep(int Stage, Guid SourceId, string TraceId, string LotCode, DateTime ExpiresOn, decimal Quantity);

public sealed partial class App01a2CommandService
{
    private sealed record HandoffAccess(bool Sales, bool Warehouse, DateTime BusinessDate, HashSet<Guid> LoadTrips)
    {
        internal C2HandoffActions Actions(C2Header order, C2OrderLineView[] lines, bool requireCurrentDay, bool canLoad) =>
            new(Sales && order.ConfirmSourceId is null && order.TripStatus == "PREPARED"
                    && (!requireCurrentDay || order.TripDate == BusinessDate),
                Warehouse && order.ConfirmSourceId is not null && order.TripStatus == "PREPARED"
                    && lines.Any(line => line.Handoff!.Unreserved > 0),
                canLoad);
    }

    private static async Task<HashSet<Guid>> OrdersReadyToLoad(SqlConnection db, SqlTransaction tx, string tenant,
        IEnumerable<Guid> assignedTrips, CancellationToken ct)
    {
        var orders = new HashSet<Guid>();
        foreach (var trips in assignedTrips.Chunk(500))
        {
            var rights = (await EligibleC3Rights(db, tx, tenant, trips, null, ct))
                .Where(right => right.WarehouseHeld > 0).ToArray();
            foreach (var right in await WithEligibleC3Lots(db, tx, tenant, rights, ct))
                if (right.OrderId is Guid order && (right.EligibleWarehouseHeld ?? right.WarehouseHeld) > 0)
                    orders.Add(order);
        }
        return orders;
    }

    private static async Task<HandoffAccess> ReadHandoffAccess(SqlConnection db, SqlTransaction tx, string tenant,
        string actor, ClaimsPrincipal principal, CancellationToken ct)
    {
        using var grid = await db.QueryMultipleAsync(Sql($"""
            SELECT RoleCode FROM dbo.App02ActorRoleGrant WHERE TenantId=@Tenant AND ActorId=@Actor AND IsActive=1;
            SELECT {LotBusinessDate};
            """, new { Tenant = tenant, Actor = actor }, tx, ct));
        var roles = (await grid.ReadAsync<string>()).AsList();
        var date = await grid.ReadSingleAsync<DateTime>();
        bool sales = roles.Contains("sales") && principal.HasClaim("synthetic_role", "sales");
        bool warehouse = roles.Contains("warehouse") && principal.HasClaim("synthetic_role", "warehouse");
        var trips = warehouse ? await AssignedLoadTrips(db, tx, tenant, actor, ct) : [];
        return new(sales, warehouse, date, trips.Select(trip => trip.Id).ToHashSet());
    }

    private sealed record HandoffLotRow(Guid LineId, int Stage, Guid SourceId, Guid EventId, Guid LotId, string LotCode,
        DateTime ExpiresOn, decimal Quantity)
    {
        internal C2LotStep ToView() => new(Stage, SourceId, $"{Stage}:{EventId:N}:{LotId:N}", LotCode, ExpiresOn, Quantity);
    }

    private sealed record HandoffQuantityRow(Guid OrderId, int LineNumber, string UomCode, decimal EnteredQuantity,
        decimal Factor)
    {
        internal C2QuantitySnapshot ToView() => new(UomCode, EnteredQuantity, Factor);
    }

    private sealed record HandoffLines(Dictionary<(Guid, int), C2QuantitySnapshot> Quantities,
        ILookup<Guid, C2LotStep> Lots)
    {
        internal C2LineHandoff View(C2LineRow line) => new(
            Math.Max(0, line.Reserved - line.HeldOnTrip),
            Math.Max(0, line.Ordered - line.Reserved - line.Delivered),
            Quantities.GetValueOrDefault((line.OrderId, line.LineNumber)),
            line.LineId is Guid id ? Lots[id].ToArray() : []);
    }

    private async Task<HandoffLines> ReadHandoffLines(SqlConnection db, SqlTransaction tx, string tenant,
        Guid[] orderIds, CancellationToken ct)
    {
        var lots = new List<HandoffLotRow>();
        var quantities = new Dictionary<(Guid, int), C2QuantitySnapshot>();
        foreach (var batch in orderIds.Chunk(500))
        {
            using var grid = await db.QueryMultipleAsync(Sql($"""
                SELECT DraftId AS OrderId,LineNumber,UomCode,EnteredQuantity,Factor
                FROM dbo.F03DraftQuantity WHERE TenantId=@Tenant AND DraftId IN @Orders;

                WITH traces AS (
                    SELECT a.Id AS LineId,1 AS Stage,sl.BusinessSourceId AS SourceId,sr.Id AS EventId,h.LotId,h.Quantity
                    FROM dbo.AOrderLine a
                    JOIN dbo.AReservation r ON r.TenantId=a.TenantId AND r.AOrderLineId=a.Id
                    JOIN dbo.StockRight sr ON sr.TenantId=r.TenantId AND sr.Id=r.StockRightId
                    JOIN dbo.BusinessSourceLine sl ON sl.TenantId=sr.TenantId AND sl.Id=sr.BusinessSourceLineId
                    JOIN dbo.F03LotHold h ON h.TenantId=sr.TenantId AND h.StockRightId=sr.Id
                    WHERE a.TenantId=@Tenant AND a.AOrderId IN @Orders
                    UNION ALL
                    SELECT a.Id,2,m.LoadBusinessSourceId,m.Id,l.LotId,l.Quantity
                    FROM dbo.AOrderLine a
                    JOIN dbo.AReservation r ON r.TenantId=a.TenantId AND r.AOrderLineId=a.Id
                    JOIN dbo.TripManifestLine m ON m.TenantId=r.TenantId AND m.StockRightId=r.StockRightId AND m.IsC3=1
                    JOIN dbo.F03LotLoad l ON l.TenantId=m.TenantId AND l.ManifestLineId=m.Id
                    WHERE a.TenantId=@Tenant AND a.AOrderId IN @Orders AND m.LoadBusinessSourceId IS NOT NULL
                    UNION ALL
                    SELECT a.Id,3,d.BusinessSourceId,d.Id,i.LotId,i.Quantity
                    FROM dbo.AOrderLine a
                    JOIN dbo.SaleDelivery d ON d.TenantId=a.TenantId AND d.AOrderLineId=a.Id AND d.RightKind='A'
                    JOIN dbo.F03LotIssue i ON i.TenantId=d.TenantId AND i.DeliveryId=d.Id
                    WHERE a.TenantId=@Tenant AND a.AOrderId IN @Orders
                    {(connections.UsesApp03Settlement ? """
                    UNION ALL
                    SELECT a.Id,4,d.TripUnloadId,m.Id,l.LotId,l.Quantity
                    FROM dbo.AOrderLine a
                    JOIN dbo.AReservation r ON r.TenantId=a.TenantId AND r.AOrderLineId=a.Id
                    JOIN dbo.TripManifestLine m ON m.TenantId=r.TenantId AND m.StockRightId=r.StockRightId
                    JOIN dbo.D3TripUnloadLine d ON d.TenantId=m.TenantId AND d.TripManifestLineId=m.Id
                    JOIN dbo.App03LotUnload l ON l.TenantId=m.TenantId AND l.ManifestLineId=m.Id
                    WHERE a.TenantId=@Tenant AND a.AOrderId IN @Orders
                    """ : "")}
                )
                SELECT t.LineId,t.Stage,t.SourceId,t.EventId,t.LotId,l.Code AS LotCode,l.ExpiresOn,t.Quantity
                FROM traces t JOIN dbo.F03Lot l ON l.TenantId=@Tenant AND l.Id=t.LotId
                ORDER BY t.Stage,l.ExpiresOn,l.Code,t.SourceId,t.EventId;
                """, new { Tenant = tenant, Orders = batch }, tx, ct));
            foreach (var row in await grid.ReadAsync<HandoffQuantityRow>())
                quantities.Add((row.OrderId, row.LineNumber), row.ToView());
            lots.AddRange(await grid.ReadAsync<HandoffLotRow>());
        }
        return new(quantities, lots.ToLookup(row => row.LineId, row => row.ToView()));
    }
}
