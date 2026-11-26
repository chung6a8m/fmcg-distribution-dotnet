using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dapper;
using FmcgDms.Api.Tenancy;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

internal static partial class App05CloseReadiness
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record SuccessorInput(Guid CommandId, Guid DestinationTripId, Guid ConfirmSourceId,
        [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] decimal Quantity, Guid? BindingId, Guid? OriginalRightId, Guid FollowUpId, string? ExpectedRevision);

    private sealed record SuccessorReceipt(byte[] PayloadHash, string Receipt, bool Applied);
    private sealed record SuccessorTrip(Guid TripId, string Code, DateTime BusinessDate, string Status);
    private sealed record SuccessorBinding(Guid BindingId, Guid DestinationTripId, string Code, DateTime BusinessDate,
        decimal Quantity, decimal Transferred, decimal Reserved, bool DestinationVisible)
    {
        internal decimal Unfilled => Quantity - Transferred - Reserved;
    }
    private sealed record SuccessorState(SuccessorTrip[] Trips, SuccessorBinding[] Bindings, TransferRight[] Rights, string Revision);
    private sealed record TransferRight(Guid RightId, Guid Warehouse, decimal NeverLoaded, decimal Returned)
    {
        public decimal Held => NeverLoaded + Returned;
    }

    private static void MapSuccessors(RouteGroupBuilder routes)
    {
        var sources = routes.MapGroup("/{tripId:guid}/a-remainders/{lineId:guid}");
        sources.MapGet("/successors", async (Guid tripId, Guid lineId, HttpContext context,
            TenantConnections connections, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await Run(connections, context.User, tripId, false, null, async (db, tx, tenant, actor, ops) =>
            {
                var source = await ReadSuccessorSource(db, tx, tenant, tripId, lineId, ct);
                if (source is null) return Results.NotFound();
                var state = await ReadSuccessorState(db, tx, tenant, actor, tripId, source, ct);
                return Results.Ok(new
                {
                    source.FollowUpId, source.Line.ConfirmSourceId, Remaining = source.CurrentRemaining, state.Revision,
                    Unheld = Unheld(source),
                    Unscheduled = source.Line.Remaining - source.Approved - state.Bindings.Sum(b => b.Quantity),
                    CanWrite = ops, state.Trips, state.Rights,
                    Bindings = state.Bindings.Select(b => new
                    {
                        BindingId = b.DestinationVisible ? (Guid?)b.BindingId : null,
                        DestinationTripId = b.DestinationVisible ? (Guid?)b.DestinationTripId : null,
                        Code = b.DestinationVisible ? b.Code : null, b.Quantity, b.Transferred, b.Reserved
                    })
                });
            }, ct);
        });
        MapSuccessorReserve(sources);
        MapRemainderClosure(sources);
        MapCommand("/assign-successor", transfer: false);
        MapCommand("/transfer-held", transfer: true);

        void MapCommand(string path, bool transfer) =>
            sources.MapPost(path, async (Guid tripId, Guid lineId, SuccessorInput input,
                HttpContext context, TenantConnections connections, CancellationToken ct) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (input.CommandId == Guid.Empty || input.DestinationTripId == Guid.Empty ||
                    input.DestinationTripId == tripId || input.ConfirmSourceId == Guid.Empty || input.FollowUpId == Guid.Empty ||
                    input.ExpectedRevision is not { Length: 64 } revision || !revision.All(char.IsAsciiHexDigit) ||
                    input.Quantity <= 0 || input.Quantity > 999999999999999.999m ||
                    decimal.Round(input.Quantity, 3) != input.Quantity ||
                    (transfer ? input.BindingId is null || input.BindingId == Guid.Empty ||
                        input.OriginalRightId is null || input.OriginalRightId == Guid.Empty
                        : input.BindingId is not null || input.OriginalRightId is not null))
                    return Results.BadRequest(new { Code = "A_SUCCESSOR_INPUT_INVALID" });
                return await Run(connections, context.User, tripId, true, input.CommandId, (db, tx, tenant, actor, _) =>
                    ExecuteSuccessor(db, tx, tenant, actor, tripId, lineId, input with { ExpectedRevision = revision.ToUpperInvariant() }, transfer, ct), ct, destination: input.DestinationTripId);
            });
    }

    private sealed record SuccessorSource(ALine Line, Guid FollowUpId, DateTime BusinessDate, decimal Step,
        decimal Delivered, decimal Approved, decimal ActiveRights)
    {
        internal decimal CurrentRemaining => Line.Confirmed - Delivered - Approved;
    }

    private static async Task<SuccessorSource?> ReadSuccessorSource(SqlConnection db, SqlTransaction tx, string tenant,
        Guid trip, Guid lineId, CancellationToken ct)
    {
        var closure = await db.QuerySingleOrDefaultAsync<ClosureRow>(App02a3Sql.Cmd(ClosureSql + """
             AND TripId=@Trip AND Status='CLOSED_WITH_OPEN_CASES' AND EXISTS(
                SELECT 1 FROM dbo.Trip t WHERE t.TenantId=@Tenant AND t.Id=@Trip AND t.Status='CLOSED_WITH_OPEN_CASES');
            """, new { Tenant = tenant, Trip = trip }, tx, ct));
        if (closure is null) return null;

        var snapshot = JsonSerializer.Deserialize<Preview>(closure.Snapshot, ClosureJson)!;
        var followUp = snapshot.Work.SingleOrDefault(w => w.SourceKind == A && w.SourceId == lineId)?.FollowUp;
        var line = snapshot.ALines.SingleOrDefault(l => l.OrderLineId == lineId);
        if (line is null || followUp is null || line.Remaining <= 0 || line.OnVehicle != 0)
            return null;
        var step = await db.ExecuteScalarAsync<decimal?>(App02a3Sql.Cmd("""
            SELECT q.BaseStep FROM dbo.AOrderLine a JOIN dbo.F03DraftQuantity q
                ON q.TenantId=a.TenantId AND q.DraftId=a.AOrderId AND q.LineNumber=a.LineNumber
            WHERE a.TenantId=@Tenant AND a.Id=@Line;
            """, new { Tenant = tenant, Line = lineId }, tx, ct));
        decimal delivered = await db.ExecuteScalarAsync<decimal>(App02a3Sql.Cmd("""
            SELECT COALESCE(SUM(BaseQuantity),0) FROM dbo.SaleDelivery
            WHERE TenantId=@Tenant AND AOrderLineId=@Line AND RightKind='A';
            """, new { Tenant = tenant, Line = lineId }, tx, ct));
        decimal approved = await db.ExecuteScalarAsync<decimal>(App02a3Sql.Cmd("""
            SELECT COALESCE(SUM(Quantity),0) FROM dbo.App05ARemainderApproval WHERE TenantId=@Tenant AND OrderLineId=@Line;
            """, new { Tenant = tenant, Line = lineId }, tx, ct));
        decimal activeRights = await db.ExecuteScalarAsync<decimal>(App02a3Sql.Cmd("""
            SELECT COALESCE(SUM(r.GrantedBaseQuantity-r.ConsumedBaseQuantity-r.ReleasedBaseQuantity),0)
            FROM dbo.AReservation a JOIN dbo.StockRight r ON r.TenantId=a.TenantId AND r.Id=a.StockRightId
            WHERE a.TenantId=@Tenant AND a.AOrderLineId=@Line;
            """, new { Tenant = tenant, Line = lineId }, tx, ct));
        return new(line, followUp.FollowUpId, snapshot.BusinessDate.ToDateTime(TimeOnly.MinValue), step ?? 0.001m,
            delivered, approved, activeRights);
    }

    private static async Task<SuccessorState> ReadSuccessorState(SqlConnection db, SqlTransaction tx,
        string tenant, string actor, Guid trip, SuccessorSource source, CancellationToken ct, bool warehouse = false)
    {
        var scope = new { Tenant = tenant, Actor = actor, source.BusinessDate, WarehouseScope = warehouse };
        var trips = (await db.QueryAsync<SuccessorTrip>(App02a3Sql.Cmd(
            EligibleSuccessorTrips + " ORDER BY t.Id;", scope, tx, ct))).ToArray();
        var bindings = (await ReadSuccessorBindings(db, tx, tenant, actor, source.Line.OrderLineId, ct, warehouse)).ToArray();
        var rights = (await ReadTransferRights(db, tx, tenant, trip, source.Line, ct)).ToArray();
        return new(trips, bindings, rights, Convert.ToHexString(Digest(new { Source = source, Trips = trips,
            Bindings = bindings, Rights = rights })));
    }

    private static async Task<IResult> ExecuteSuccessor(SqlConnection db, SqlTransaction tx, string tenant, string actor,
        Guid trip, Guid line, SuccessorInput input, bool transfer, CancellationToken ct, Guid? warehouse = null)
    {
        // Hash the numeric value in canonical fixed-scale form for equivalent JSON decimals.
        string quantityBase = input.Quantity.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        byte[] hash = warehouse.HasValue ? Digest(new { Kind = "Reserve", Trip = trip, Line = line, Input = input with { Quantity = 0 }, Quantity = quantityBase, Warehouse = warehouse.Value })
            : Digest(new { Trip = trip, Line = line, Transfer = transfer, input.DestinationTripId,
            input.ConfirmSourceId, Quantity = quantityBase,
            input.BindingId, input.OriginalRightId, input.FollowUpId, input.ExpectedRevision });
        var scope = new { Tenant = tenant, Actor = actor, input.CommandId };
        var replay = await db.QuerySingleOrDefaultAsync<SuccessorReceipt>(App02a3Sql.Cmd("""
            SELECT PayloadHash,Receipt,Applied FROM dbo.App05ASuccessorCommand
            WHERE TenantId=@Tenant AND ActorId=@Actor AND CommandId=@CommandId;
            """, scope, tx, ct));
        if (replay is not null)
            return CryptographicOperations.FixedTimeEquals(hash, replay.PayloadHash)
                ? Respond(replay.Receipt, replay.Applied)
                : Results.Conflict(new { Code = "COMMAND_PAYLOAD_CONFLICT" });

        if (await db.ExecuteScalarAsync<bool>(App02a3Sql.Cmd("""
            SELECT CONVERT(bit,CASE WHEN EXISTS(SELECT 1 FROM dbo.App05FollowUpAssignment
                WHERE TenantId=@Tenant AND ActorId=@Actor AND CommandId=@CommandId)
                OR EXISTS(SELECT 1 FROM dbo.App05TripClosure
                WHERE TenantId=@Tenant AND ActorId=@Actor AND CommandId=@CommandId)
                OR EXISTS(SELECT 1 FROM dbo.App05TripCloseRejection
                WHERE TenantId=@Tenant AND ActorId=@Actor AND CommandId=@CommandId) THEN 1 ELSE 0 END);
            """, scope, tx, ct)))
            return Results.Conflict(new { Code = "COMMAND_PAYLOAD_CONFLICT" });

        await LockAOrderLine(db, tx, tenant, line, ct);
        var source = await ReadSuccessorSource(db, tx, tenant, trip, line, ct);
        if (source?.Line.ConfirmSourceId != input.ConfirmSourceId || source.FollowUpId != input.FollowUpId)
            return await Record(new { Code = "A_SUCCESSOR_SOURCE_INVALID" }, false);
        if (input.Quantity % source.Step != 0)
            return await Record(new { Code = "A_SUCCESSOR_QUANTITY_STEP" }, false);

        // Use the same backing-right lock as C.3 before reading the available generation.
        if (transfer && input.OriginalRightId is Guid originRight)
            await App01a2CommandService.LockHeldOriginRight(db, tx, tenant, originRight, ct);
        if (warehouse.HasValue)
            await App01a2CommandService.LockStockAllocation(db, tx, tenant, warehouse.Value, source.Line.SkuId, ct);
        var state = await ReadSuccessorState(db, tx, tenant, actor, trip, source, ct, warehouse: warehouse.HasValue);
        if (state.Revision != input.ExpectedRevision)
            return await Record(new { Code = "A_SUCCESSOR_REVISION_CHANGED" }, false);
        var destination = state.Trips.SingleOrDefault(t => t.TripId == input.DestinationTripId);
        if (destination is null)
            return await Record(new { Code = "A_SUCCESSOR_TRIP_NOT_ELIGIBLE" }, false);

        if (!transfer && warehouse is null)
        {
            if (input.Quantity > source.Line.Remaining - source.Approved - state.Bindings.Sum(b => b.Quantity))
                return await Record(new { Code = "A_SUCCESSOR_QUANTITY_EXCEEDED" }, false);
            var bindingId = Guid.NewGuid();
            await db.ExecuteAsync(App02a3Sql.Cmd("""
                INSERT dbo.App05ASuccessor(TenantId,BindingId,OriginTripId,DestinationTripId,OrderLineId,
                    ConfirmSourceId,FollowUpId,SkuId,Quantity)
                VALUES(@Tenant,@BindingId,@Trip,@DestinationTripId,@Line,@ConfirmSourceId,@FollowUpId,@SkuId,@Quantity);
                """, new { scope.Tenant, BindingId = bindingId, Trip = trip, input.DestinationTripId, Line = line,
                    input.ConfirmSourceId, source.FollowUpId, source.Line.SkuId, input.Quantity }, tx, ct));
            return await Record(new { BindingId = bindingId, input.Quantity }, true);
        }

        var binding = state.Bindings.SingleOrDefault(b => b.BindingId == input.BindingId &&
            b.DestinationTripId == input.DestinationTripId);
        if (binding is null) return await Record(new { Code = "A_SUCCESSOR_BINDING_INVALID" }, false);
        if (warehouse.HasValue)
        {
            if (input.Quantity > binding.Unfilled || input.Quantity > Unheld(source))
                return await Record(new { Code = "A_UNHELD_QUANTITY_EXCEEDED" }, false);
            var reserve = await App01a2CommandService.ReserveSuccessorStock(db, tx, tenant, actor, binding.BindingId,
                destination.TripId, source.Line.OrderId, line, input.ConfirmSourceId, warehouse.Value, source.Line.SkuId,
                input.Quantity, hash, ct);
            return reserve.Code is not null ? await Record(new { reserve.Code }, false)
                : await Record(new { binding.BindingId, input.Quantity, reserve.SourceId, reserve.RightId }, true);
        }
        var right = state.Rights.SingleOrDefault(r => r.RightId == input.OriginalRightId);
        if (right is null) return await Record(new { Code = "A_HELD_RIGHT_INVALID" }, false);
        if (input.Quantity > right.Held || input.Quantity > binding.Unfilled || input.Quantity > source.CurrentRemaining)
            return await Record(new { Code = "A_HELD_QUANTITY_EXCEEDED" }, false);
        if (!await App01dCashierUnloadService.VerifyMainHeldPool(db, tx, tenant,
            new(right.Warehouse, source.Line.SkuId), ct, successor: true))
            return await Record(new { Code = "MAIN_HELD_SOURCE_DRIFT" }, false);

        var allocation = await AllocateTransferLots(db, tx, tenant, source.Line, right, input.Quantity,
            destination.BusinessDate, ct);
        if (allocation is null) return await Record(new { Code = "A_HELD_LOT_UNAVAILABLE" }, false);
        var successorRightId = Guid.NewGuid();
        await db.ExecuteAsync(App02a3Sql.Cmd("""
            INSERT dbo.App05AHeldTransfer(TenantId,SuccessorRightId,BindingId,OrderLineId,OriginalRightId,OriginTripId,WarehouseId,SkuId,NeverLoaded,Returned)
            VALUES(@Tenant,@SuccessorRightId,@BindingId,@Line,@RightId,@Trip,@Warehouse,@SkuId,@NeverLoaded,@Returned);
            """, new { scope.Tenant, SuccessorRightId = successorRightId, binding.BindingId, Line = line, right.RightId, Trip = trip, right.Warehouse,
                source.Line.SkuId, allocation.NeverLoaded, allocation.Returned }, tx, ct));
        foreach (var lot in allocation.Lots)
        {
            await db.ExecuteAsync(App02a3Sql.Cmd("""
                INSERT dbo.App05AHeldTransferLot(TenantId,SuccessorRightId,OriginalRightId,LotId,NeverLoaded,Returned)
                VALUES(@Tenant,@SuccessorRightId,@RightId,@LotId,@NeverLoaded,@Returned);
                """, new { scope.Tenant, SuccessorRightId = successorRightId, right.RightId, lot.LotId,
                    lot.NeverLoaded, lot.Returned }, tx, ct));
        }
        return await Record(new { SuccessorRightId = successorRightId, binding.BindingId, input.Quantity,
            allocation.NeverLoaded, allocation.Returned }, true);

        async Task<IResult> Record(object body, bool applied)
        {
            string receipt = JsonSerializer.Serialize(body, ClosureJson);
            await db.ExecuteAsync(App02a3Sql.Cmd($"""
                INSERT dbo.App05ASuccessorCommand(TenantId,ActorId,CommandId,PayloadHash,Receipt,Applied,RecordedAt)
                VALUES(@Tenant,@Actor,@CommandId,@Hash,@Receipt,@Applied,TODATETIMEOFFSET({App02c6Clock.SqlUtcNow},'+00:00'));
                """, new { scope.Tenant, scope.Actor, scope.CommandId, Hash = hash, Receipt = receipt, Applied = applied }, tx, ct));
            return Respond(receipt, applied);
        }

        IResult Respond(string receipt, bool applied)
        {
            // Replay reaches here only after current authorization and an exact payload hash match.
            // Enrich the response without rewriting receipts recorded before this contract.
            var response = JsonNode.Parse(receipt)!.AsObject();
            response["outcome"] = applied ? "APPLIED" : "REJECTED";
            response["request"] = JsonSerializer.SerializeToNode(new
            {
                OriginTripId = trip, OrderLineId = line, input.CommandId, input.DestinationTripId,
                input.ConfirmSourceId, input.FollowUpId, input.ExpectedRevision, input.BindingId, input.OriginalRightId, WarehouseId = warehouse, QuantityBase = quantityBase
            }, ClosureJson);
            if (applied)
            {
                response["quantityBase"] = response["quantity"]!.ToJsonString();
                if (transfer)
                {
                    response["neverLoadedBase"] = response["neverLoaded"]!.ToJsonString();
                    response["returnedBase"] = response["returned"]!.ToJsonString();
                }
            }
            return Results.Json(response, statusCode: applied ? 200 : 409);
        }
    }

    private static Task<bool> SuccessorCommandUsed(SqlConnection db, SqlTransaction tx, string tenant, string actor,
        Guid commandId, CancellationToken ct) => db.ExecuteScalarAsync<bool>(App02a3Sql.Cmd("""
            SELECT CONVERT(bit,IIF(EXISTS(SELECT 1 FROM dbo.App05ASuccessorCommand
                WHERE TenantId=@Tenant AND ActorId=@Actor AND CommandId=@CommandId),1,0));
            """, new { Tenant = tenant, Actor = actor, CommandId = commandId }, tx, ct));

    private static readonly string EligibleSuccessorTrips = $"""
        SELECT t.Id AS TripId,t.Code,t.BusinessDate,t.Status FROM dbo.Trip t
        JOIN dbo.StockLocation v ON v.TenantId=t.TenantId AND v.Id=t.VehicleLocationId AND v.Kind='VEHICLE' AND v.IsActive=1
        WHERE t.TenantId=@Tenant AND t.Status='PREPARED' AND t.BusinessDate>@BusinessDate
            AND t.BusinessDate=CAST(({App02c6Clock.SqlUtcNow} AT TIME ZONE 'UTC') AT TIME ZONE 'SE Asia Standard Time' AS date)
            AND ((@WarehouseScope=0 AND EXISTS(SELECT 1 FROM dbo.App05TripOperator o
                WHERE o.TenantId=t.TenantId AND o.TripId=t.Id AND o.ActorId=@Actor AND o.IsActive=1))
                OR (@WarehouseScope=1 AND EXISTS(SELECT 1 FROM dbo.C3TripWriter w
                WHERE w.TenantId=t.TenantId AND w.TripId=t.Id AND w.ActorId=@Actor)))
        """;

    private static Task<IEnumerable<SuccessorBinding>> ReadSuccessorBindings(SqlConnection db, SqlTransaction tx,
        string tenant, string actor, Guid line, CancellationToken ct, bool warehouse) => db.QueryAsync<SuccessorBinding>(App02a3Sql.Cmd("""
        SELECT b.BindingId,b.DestinationTripId,t.Code,t.BusinessDate,b.Quantity,x.Transferred,y.Reserved,
            CONVERT(bit,IIF((@WarehouseScope=0 AND o.ActorId IS NOT NULL)
                OR (@WarehouseScope=1 AND w.ActorId IS NOT NULL),1,0)) AS DestinationVisible
        FROM dbo.App05ASuccessor b JOIN dbo.Trip t ON t.TenantId=b.TenantId AND t.Id=b.DestinationTripId
        LEFT JOIN dbo.App05TripOperator o ON o.TenantId=t.TenantId AND o.TripId=t.Id AND o.ActorId=@Actor AND o.IsActive=1
        LEFT JOIN dbo.C3TripWriter w ON w.TenantId=t.TenantId AND w.TripId=t.Id AND w.ActorId=@Actor
        CROSS APPLY(SELECT CONVERT(decimal(18,3),COALESCE(SUM(NeverLoaded+Returned),0)) AS Transferred
            FROM dbo.App05AHeldTransfer x WHERE x.TenantId=b.TenantId AND x.BindingId=b.BindingId) x
        CROSS APPLY(SELECT CONVERT(decimal(18,3),COALESCE(SUM(r.GrantedBaseQuantity-r.ReleasedBaseQuantity),0)) AS Reserved
            FROM dbo.App05ASuccessorReserve rr JOIN dbo.StockRight r ON r.TenantId=rr.TenantId AND r.Id=rr.RightId
            WHERE rr.TenantId=b.TenantId AND rr.BindingId=b.BindingId) y
        WHERE b.TenantId=@Tenant AND b.OrderLineId=@Line ORDER BY t.BusinessDate,t.Code,b.BindingId;
        """, new { Tenant = tenant, Actor = actor, Line = line, WarehouseScope = warehouse }, tx, ct));

    private static Task<IEnumerable<TransferRight>> ReadTransferRights(SqlConnection db, SqlTransaction tx, string tenant,
        Guid trip, ALine line, CancellationToken ct) => db.QueryAsync<TransferRight>(App02a3Sql.Cmd("""
        SELECT r.Id AS RightId,r.StockLocationId AS Warehouse,
            r.GrantedBaseQuantity-r.ReleasedBaseQuantity+x.Materialized+closed.Returned-v.Loaded-x.NeverLoaded AS NeverLoaded,
            v.Returned-x.Returned-closed.Returned AS Returned
        FROM dbo.AReservation a JOIN dbo.StockRight r ON r.TenantId=a.TenantId AND r.Id=a.StockRightId
        JOIN dbo.StockLocation w ON w.TenantId=r.TenantId AND w.Id=r.StockLocationId AND w.Kind='WAREHOUSE' AND w.IsActive=1
        CROSS APPLY(SELECT CONVERT(decimal(18,3),COALESCE(SUM(m.LoadedBaseQuantity),0)) AS Loaded,
                CONVERT(decimal(18,3),COALESCE(SUM(u.BaseQuantity),0)) AS Returned
            FROM dbo.TripManifestLine m LEFT JOIN dbo.D3TripUnloadLine u
                ON u.TenantId=m.TenantId AND u.TripManifestLineId=m.Id
            WHERE m.TenantId=r.TenantId AND m.StockRightId=r.Id) v
        CROSS APPLY(SELECT CONVERT(decimal(18,3),COALESCE(SUM(NeverLoaded),0)) AS NeverLoaded,
                CONVERT(decimal(18,3),COALESCE(SUM(Returned),0)) AS Returned,
                CONVERT(decimal(18,3),COALESCE(SUM(CASE WHEN e.RightId IS NOT NULL THEN x.NeverLoaded+x.Returned END),0)) AS Materialized
            FROM dbo.App05AHeldTransfer x LEFT JOIN dbo.App05AHeldExecution e
                ON e.TenantId=x.TenantId AND e.RightId=x.SuccessorRightId
            WHERE x.TenantId=r.TenantId AND x.OriginalRightId=r.Id) x
        CROSS APPLY(SELECT CONVERT(decimal(18,3),COALESCE(SUM(Returned),0)) AS Returned
            FROM dbo.App05ARemainderRelease c WHERE c.TenantId=r.TenantId AND c.StockRightId=r.Id) closed
        WHERE a.TenantId=@Tenant AND a.AOrderLineId=@Line AND r.TripId=@Trip AND r.SkuId=@SkuId AND r.RightKind='A' AND r.Status='HELD'
            AND v.Loaded-v.Returned=r.ConsumedBaseQuantity
            AND v.Loaded+x.NeverLoaded<=r.GrantedBaseQuantity-r.ReleasedBaseQuantity+x.Materialized+closed.Returned
            AND x.Returned+closed.Returned<=v.Returned
        ORDER BY r.Id;
        """, new { Tenant = tenant, Trip = trip, Line = line.OrderLineId, line.SkuId }, tx, ct));
}
