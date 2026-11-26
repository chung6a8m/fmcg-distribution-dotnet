using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed partial class App01a2CommandService
{
    private sealed record HeldExecutionCandidate(Guid Id, Guid BindingId, Guid OriginalRightId, Guid OriginTripId,
        string OriginTrip, Guid DestinationTripId, Guid OrderId, Guid OrderLineId, Guid CustomerId,
        Guid WarehouseId, string WarehouseCode, Guid SkuId, string SkuCode, string BaseUom, decimal Quantity)
    {
        internal C3RightView ToView() =>
            new(Id, OrderId, WarehouseId, WarehouseCode, SkuId, SkuCode, BaseUom, Quantity, 0, Quantity, OriginTrip);
    }

    private static Task<IEnumerable<HeldExecutionCandidate>> PendingHeldExecution(SqlConnection db, SqlTransaction tx,
        string tenant, Guid tripId, Guid[]? requested, CancellationToken ct) =>
        db.QueryAsync<HeldExecutionCandidate>(Sql($"""
            SELECT h.SuccessorRightId AS Id,h.BindingId,h.OriginalRightId,h.OriginTripId,origin.Code AS OriginTrip,
                b.DestinationTripId,s.AOrderId AS OrderId,h.OrderLineId,s.CustomerId,h.WarehouseId,w.Code AS WarehouseCode,
                h.SkuId,k.Code AS SkuCode,p.BaseUom,h.NeverLoaded+h.Returned AS Quantity
            FROM dbo.App05AHeldTransfer h
            JOIN dbo.App05ASuccessor b ON b.TenantId=h.TenantId AND b.BindingId=h.BindingId
            JOIN dbo.Trip origin ON origin.TenantId=h.TenantId AND origin.Id=h.OriginTripId AND origin.Status='CLOSED_WITH_OPEN_CASES'
            JOIN dbo.Trip t ON t.TenantId=b.TenantId AND t.Id=b.DestinationTripId AND t.Status='PREPARED'
                AND t.BusinessDate={LotBusinessDate}
            JOIN dbo.StockRight r ON r.TenantId=h.TenantId AND r.Id=h.OriginalRightId AND r.Status='HELD'
                AND r.GrantedBaseQuantity-r.ConsumedBaseQuantity-r.ReleasedBaseQuantity>=h.NeverLoaded+h.Returned
            JOIN dbo.AConfirmedLineSnapshot s ON s.TenantId=b.TenantId AND s.AOrderLineId=b.OrderLineId
                AND s.ConfirmBusinessSourceId=b.ConfirmSourceId AND s.TripId=b.OriginTripId AND s.SkuId=b.SkuId
            JOIN dbo.AOrder o ON o.TenantId=s.TenantId AND o.Id=s.AOrderId AND o.Status IN ('CONFIRMED','PARTIALLY_DELIVERED')
            JOIN dbo.Customer c ON c.TenantId=s.TenantId AND c.Id=s.CustomerId AND c.IsActive=1
            JOIN dbo.C2LinePolicy p ON p.TenantId=s.TenantId AND p.OrderLineId=s.AOrderLineId
            JOIN dbo.Sku k ON k.TenantId=h.TenantId AND k.Id=h.SkuId AND k.IsActive=1
                AND CONVERT(varbinary(48),k.BaseUom)=CONVERT(varbinary(48),p.BaseUom)
            JOIN dbo.StockLocation w ON w.TenantId=h.TenantId AND w.Id=h.WarehouseId AND w.IsActive=1 AND w.Kind='WAREHOUSE'
            WHERE h.TenantId=@Tenant AND b.DestinationTripId=@TripId
                AND NOT EXISTS(SELECT 1 FROM dbo.App05AHeldExecution e WHERE e.TenantId=h.TenantId AND e.RightId=h.SuccessorRightId)
                {(requested is null ? "" : "AND h.SuccessorRightId IN @Requested")}
            ORDER BY w.Code,k.Code,h.SuccessorRightId;
            """, new { Tenant = tenant, TripId = tripId, Requested = requested }, tx, ct));

    private static Task<IEnumerable<F03LotAvailability>> HeldExecutionLots(SqlConnection db, SqlTransaction tx,
        string tenant, Guid right, CancellationToken ct) =>
        db.QueryAsync<F03LotAvailability>(Sql("""
            SELECT h.SuccessorRightId AS SourceId,h.LotId,l.Code,l.ExpiresOn,h.NeverLoaded+h.Returned AS Quantity
            FROM dbo.App05AHeldTransferLot h JOIN dbo.F03Lot l ON l.TenantId=h.TenantId AND l.Id=h.LotId
            WHERE h.TenantId=@Tenant AND h.SuccessorRightId=@Right
            ORDER BY l.ExpiresOn,l.Code COLLATE Latin1_General_100_BIN2,l.Id;
            """, new { Tenant = tenant, Right = right }, tx, ct));

    private static async Task MaterializeHeldExecution(SqlConnection db, SqlTransaction tx, string tenant, string actor,
        HeldExecutionCandidate right, IReadOnlyList<F03Pick>? lots, CancellationToken ct)
    {
        var scope = new { Tenant = tenant, right.Id, right.BindingId, right.OrderLineId, right.OriginalRightId,
            right.OriginTripId, right.DestinationTripId, right.WarehouseId, right.SkuId, right.Quantity };
        int changed = await db.ExecuteAsync(Sql("""
            UPDATE dbo.StockRight SET ReleasedBaseQuantity=ReleasedBaseQuantity+@Quantity,
                Status=CASE WHEN GrantedBaseQuantity=ConsumedBaseQuantity+ReleasedBaseQuantity+@Quantity
                    THEN 'RELEASED' ELSE 'HELD' END
            WHERE TenantId=@Tenant AND Id=@OriginalRightId AND RightKind='A' AND Status='HELD'
                AND GrantedBaseQuantity-ConsumedBaseQuantity-ReleasedBaseQuantity>=@Quantity;
            """, scope, tx, ct));
        if (changed != 1) throw new InvalidOperationException("The original HELD quantity changed before execution.");

        Guid commandId = Guid.NewGuid();
        string number = "APP05-HELD-" + right.Id.ToString("N");
        var command = new ReserveAStockCommand("APP05-HELD", right.Id.ToString("N"), number,
            right.OrderId, right.OrderLineId, right.WarehouseId, right.Quantity);
        var hash = CommandPayloadHash.Compute("APP05_HELD_EXECUTION", tenant, actor, number,
            ("Binding", right.BindingId.ToString("N")), ("Right", right.Id.ToString("N")));
        await InsertReceivedCommand(db, tx, tenant, actor, command, commandId, hash, "warehouse", null, null, ct);
        var (source, _) = await RecordAStockRight(db, tx, tenant, actor, commandId, number, right.CustomerId,
            right.DestinationTripId, right.OrderLineId, right.SkuId, right.WarehouseId, right.Quantity, lots, ct, right.Id);
        Guid sourceLine = await AddLine(db, tx, tenant, source, 2, right.SkuId, right.Quantity, null, ct);
        await AddRightEffect(db, tx, tenant, sourceLine, "RIGHT_A_TRANSFER_OUT", right.CustomerId,
            right.SkuId, right.WarehouseId, -right.Quantity, 0, ct);

        await db.ExecuteAsync(Sql($"""
            INSERT dbo.App05AHeldExecution(TenantId,RightId,BindingId,OrderLineId,OriginalRightId,OriginTripId,
                DestinationTripId,WarehouseId,SkuId,RightKind)
            SELECT @Tenant,@Id,@BindingId,@OrderLineId,@OriginalRightId,@OriginTripId,@DestinationTripId,@WarehouseId,@SkuId,'A';
            UPDATE dbo.BusinessCommand SET Status='APPLIED',OutcomeCode='A_HELD_EXECUTION_MATERIALIZED',
                CompletedAtUtc={App02c6Clock.SqlUtcNow}
            WHERE TenantId=@Tenant AND Id=@CommandId AND Status='RECEIVED';
            """, new { scope.Tenant, scope.Id, scope.BindingId, scope.OrderLineId, scope.OriginalRightId, scope.OriginTripId,
                scope.DestinationTripId, scope.WarehouseId, scope.SkuId, CommandId = commandId }, tx, ct));
    }
}
