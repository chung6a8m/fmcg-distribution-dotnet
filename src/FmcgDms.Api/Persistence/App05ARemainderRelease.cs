using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

internal static partial class App05CloseReadiness
{
    private sealed record ClosureRelease(TransferRight Right, TransferAllocation Allocation)
    {
        internal decimal Quantity => Allocation.NeverLoaded + Allocation.Returned;
    }

    private static async Task LockClosurePools(SqlConnection db, SqlTransaction tx, string tenant, Guid tripId,
        Guid lineId, CancellationToken ct)
    {
        // Match C.2's warehouse lock before order access, even on its rejected legacy Reserve path.
        var pools = await db.QueryAsync<(Guid Warehouse, Guid Sku)>(App02a3Sql.Cmd("""
            SELECT DISTINCT r.StockLocationId,r.SkuId
            FROM dbo.AReservation a WITH (READCOMMITTEDLOCK)
            JOIN dbo.StockRight r WITH (READCOMMITTEDLOCK) ON r.TenantId=a.TenantId AND r.Id=a.StockRightId
            WHERE a.TenantId=@Tenant AND a.AOrderLineId=@LineId AND r.TripId=@TripId AND r.RightKind='A';
            """, new { Tenant = tenant, TripId = tripId, LineId = lineId }, tx, ct));
        foreach (var (warehouse, sku) in pools.OrderBy(pool => pool.Warehouse).ThenBy(pool => pool.Sku))
            await App01a2CommandService.LockStockAllocation(db, tx, tenant, warehouse, sku, ct);
    }

    private static async Task<IReadOnlyList<ClosureRelease>?> PlanClosureRelease(SqlConnection db, SqlTransaction tx,
        string tenant, Guid tripId, SuccessorSource source, decimal quantity, CancellationToken ct)
    {
        if (quantity == 0) return [];
        var rights = (await ReadTransferRights(db, tx, tenant, tripId, source.Line, ct)).AsList();
        foreach (Guid rightId in rights.Select(r => r.RightId).Order())
            await App01a2CommandService.LockHeldOriginRight(db, tx, tenant, rightId, ct);
        foreach (Guid warehouse in rights.Select(r => r.Warehouse).Distinct().Order())
        {
            await App02a3Sql.Lock(db, tx, $"DEVF03:RESERVE:{source.Line.SkuId:N}:{warehouse:N}", ct);
            if (!await App01dCashierUnloadService.VerifyMainHeldPool(db, tx, tenant, new(warehouse, source.Line.SkuId), ct, successor: true))
                return null;
        }

        var plan = new List<ClosureRelease>();
        foreach (var right in rights)
        {
            decimal take = Math.Min(right.Held, quantity);
            if (take <= 0) continue;
            var allocation = await AllocateTransferLots(db, tx, tenant, source.Line, right, take, source.BusinessDate, ct, closing: true);
            if (allocation is null) return null;
            plan.Add(new(right, allocation));
            quantity -= take;
            if (quantity == 0) return plan;
        }
        return null;
    }

    private static async Task PostClosureRelease(SqlConnection db, SqlTransaction tx, string tenant, Guid sourceId,
        Guid tripId, ALine line, Guid customerId, IReadOnlyList<ClosureRelease> plan, CancellationToken ct)
    {
        foreach (var released in plan.GroupBy(item => item.Right.Warehouse).OrderBy(group => group.Key))
        {
            int changed = await db.ExecuteAsync(App02a3Sql.Cmd("""
                UPDATE dbo.StockPool SET AllocatedBaseQuantity=AllocatedBaseQuantity-@Quantity
                WHERE TenantId=@Tenant AND StockLocationId=@Warehouse AND SkuId=@SkuId AND AllocatedBaseQuantity>=@Quantity;
                """, new { Tenant = tenant, Warehouse = released.Key, line.SkuId, Quantity = released.Sum(item => item.Quantity) }, tx, ct));
            if (changed != 1) throw new InvalidOperationException("Closure allocation changed under lock.");
        }

        for (int i = 0; i < plan.Count; i++)
        {
            var item = plan[i];
            var rightId = item.Right.RightId;
            Guid sourceLine = await App01a2CommandService.RecordClosureRightEffect(db, tx, tenant, sourceId, i + 2,
                customerId, line.SkuId, item.Right.Warehouse, item.Quantity, ct);
            var scope = new { Tenant = tenant, SourceId = sourceId, StockRightId = rightId, line.OrderLineId, TripId = tripId,
                item.Right.Warehouse, line.SkuId, item.Quantity, SourceLineId = sourceLine,
                item.Allocation.NeverLoaded, item.Allocation.Returned };
            await db.ExecuteAsync(App02a3Sql.Cmd("""
                UPDATE dbo.StockRight SET ReleasedBaseQuantity=ReleasedBaseQuantity+@Quantity,
                    Status=CASE WHEN GrantedBaseQuantity=ConsumedBaseQuantity+ReleasedBaseQuantity+@Quantity
                        THEN 'RELEASED' ELSE 'HELD' END
                WHERE TenantId=@Tenant AND Id=@StockRightId AND TripId=@TripId AND StockLocationId=@Warehouse
                    AND SkuId=@SkuId AND RightKind='A' AND Status='HELD'
                    AND GrantedBaseQuantity-ConsumedBaseQuantity-ReleasedBaseQuantity>=@Quantity;
                IF @@ROWCOUNT<>1 THROW 51934,'APP-05 remainder closure right changed under lock.',1;
                INSERT dbo.App05ARemainderRelease(TenantId,SourceId,StockRightId,OrderLineId,RightTripId,WarehouseId,SkuId,
                    SourceLineId,RightKind,NeverLoaded,Returned)
                VALUES(@Tenant,@SourceId,@StockRightId,@OrderLineId,@TripId,@Warehouse,@SkuId,@SourceLineId,'A',@NeverLoaded,@Returned);
                """, scope, tx, ct));
            foreach (var lot in item.Allocation.Lots)
                await db.ExecuteAsync(App02a3Sql.Cmd("""
                    INSERT dbo.App05ARemainderReleaseLot(TenantId,SourceId,StockRightId,LotId,NeverLoaded,Returned)
                    VALUES(@Tenant,@SourceId,@StockRightId,@LotId,@NeverLoaded,@Returned);
                    """, new { scope.Tenant, scope.SourceId, scope.StockRightId, lot.LotId, lot.NeverLoaded, lot.Returned }, tx, ct));
        }
    }
}

public sealed partial class App01a2CommandService
{
    private sealed record ClosureSourceCommand(string ProducerId, string CommandId, string SourceNumber) : IApp01aCommand;

    internal static async Task<Guid> RecordClosureApprovalSource(SqlConnection db, SqlTransaction tx, string tenant,
        string actor, Guid proposalId, Guid tripId, Guid customerId, Guid sku, decimal quantity, byte[] hash, CancellationToken ct)
    {
        Guid commandRow = Guid.NewGuid();
        string sourceNumber = "APP05-CLOSURE-" + proposalId.ToString("N");
        await InsertReceivedCommand(db, tx, tenant, actor, new ClosureSourceCommand("APP05-CLOSURE", commandRow.ToString("N"),
            sourceNumber), commandRow, hash, null, null, null, ct);
        Guid sourceId = await AddSource(db, tx, tenant, commandRow, "A_REMAINDER_CLOSED", sourceNumber, actor, customerId, tripId, ct);
        await AddLine(db, tx, tenant, sourceId, 1, sku, quantity, null, ct);
        await db.ExecuteAsync(Sql($"""
            UPDATE dbo.BusinessCommand SET Status='APPLIED',OutcomeCode='A_REMAINDER_CLOSED',
                CompletedAtUtc={App02c6Clock.SqlUtcNow}
            WHERE TenantId=@Tenant AND Id=@Id AND Status='RECEIVED';
            """, new { Tenant = tenant, Id = commandRow }, tx, ct));
        return sourceId;
    }

    internal static async Task<Guid> RecordClosureRightEffect(SqlConnection db, SqlTransaction tx, string tenant, Guid sourceId,
        int number, Guid customerId, Guid sku, Guid warehouse, decimal quantity, CancellationToken ct)
    {
        Guid sourceLine = await AddLine(db, tx, tenant, sourceId, number, sku, quantity, null, ct);
        await AddRightEffect(db, tx, tenant, sourceLine, "RIGHT_A_RELEASED", customerId, sku, warehouse, -quantity, 0, ct);
        return sourceLine;
    }
}
