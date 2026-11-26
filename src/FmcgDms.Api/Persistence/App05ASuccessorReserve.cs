using System.Text.Json.Serialization;
using Dapper;
using FmcgDms.Api.Tenancy;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

internal static partial class App05CloseReadiness
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record SuccessorReserveInput(Guid CommandId, Guid DestinationTripId, Guid ConfirmSourceId,
        [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] decimal Quantity,
        Guid BindingId, Guid WarehouseId, Guid FollowUpId, string? ExpectedRevision);

    private static void MapSuccessorReserve(RouteGroupBuilder sources)
    {
        sources.MapGet("/reserve-options", async (Guid tripId, Guid lineId, HttpContext context,
            TenantConnections connections, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await Run(connections, context.User, tripId, false, null, async (db, tx, tenant, actor, _) =>
            {
                var source = await ReadSuccessorSource(db, tx, tenant, tripId, lineId, ct);
                if (source is null) return Results.NotFound();
                var state = await ReadSuccessorState(db, tx, tenant, actor, tripId, source, ct, warehouse: true);
                return Results.Ok(new
                {
                    source.FollowUpId, source.Line.ConfirmSourceId, state.Revision,
                    Unheld = Unheld(source, state), source.Step,
                    Bindings = state.Bindings.Where(b => b.DestinationVisible && state.Trips.Any(t => t.TripId == b.DestinationTripId))
                        .Select(b => new { b.BindingId, b.DestinationTripId, b.Code, b.Quantity, b.Transferred, b.Reserved, b.Unfilled })
                });
            }, ct, warehouse: true);
        });

        sources.MapPost("/reserve-unheld", async (Guid tripId, Guid lineId, SuccessorReserveInput input,
            HttpContext context, TenantConnections connections, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (input.CommandId == Guid.Empty || input.DestinationTripId == Guid.Empty || tripId == input.DestinationTripId ||
                input.BindingId == Guid.Empty || input.WarehouseId == Guid.Empty || input.ConfirmSourceId == Guid.Empty ||
                input.FollowUpId == Guid.Empty || input.ExpectedRevision is not { Length: 64 } revision ||
                !revision.All(char.IsAsciiHexDigit) || input.Quantity <= 0 || input.Quantity > 999999999999999.999m ||
                decimal.Round(input.Quantity, 3) != input.Quantity)
                return Results.BadRequest(new { Code = "A_SUCCESSOR_INPUT_INVALID" });
            var command = new SuccessorInput(input.CommandId, input.DestinationTripId, input.ConfirmSourceId,
                input.Quantity, input.BindingId, null, input.FollowUpId, revision.ToUpperInvariant());
            return await Run(connections, context.User, tripId, true, input.CommandId, (db, tx, tenant, actor, _) =>
                ExecuteSuccessor(db, tx, tenant, actor, tripId, lineId, command, false, ct, input.WarehouseId),
                ct, destination: input.DestinationTripId, warehouse: true);
        });
    }

    private static decimal Unheld(SuccessorSource source, SuccessorState state) =>
        source.Line.Remaining - source.Line.Held - state.Bindings.Sum(b => b.Reserved);
}

public sealed partial class App01a2CommandService
{
    internal sealed record SuccessorStockResult(string? Code, Guid? SourceId = null, Guid? RightId = null);

    internal static async Task<SuccessorStockResult> ReserveSuccessorStock(SqlConnection db, SqlTransaction tx,
        string tenant, string actor, Guid binding, Guid destination, Guid orderId, Guid lineId, Guid confirmSource,
        Guid warehouse, Guid sku, decimal quantity, byte[] hash, CancellationToken ct)
    {
        var customer = await db.ExecuteScalarAsync<Guid?>(Sql("""
            SELECT s.CustomerId FROM dbo.AConfirmedLineSnapshot s
            JOIN dbo.Customer c ON c.TenantId=s.TenantId AND c.Id=s.CustomerId AND c.IsActive=1
            JOIN dbo.AOrder o ON o.TenantId=s.TenantId AND o.Id=s.AOrderId AND o.Status IN ('CONFIRMED','PARTIALLY_DELIVERED')
            JOIN dbo.C2LinePolicy p ON p.TenantId=s.TenantId AND p.OrderLineId=s.AOrderLineId
            JOIN dbo.Sku k ON k.TenantId=s.TenantId AND k.Id=s.SkuId AND k.IsActive=1
                AND CONVERT(varbinary(48),p.BaseUom)=CONVERT(varbinary(48),k.BaseUom)
            WHERE s.TenantId=@Tenant AND s.AOrderId=@OrderId AND s.AOrderLineId=@LineId
                AND s.ConfirmBusinessSourceId=@ConfirmSource AND s.SkuId=@Sku;
            """, new { Tenant = tenant, OrderId = orderId, LineId = lineId, ConfirmSource = confirmSource, Sku = sku }, tx, ct));
        if (customer is null) return new("C2_FROZEN_LINE_INVALID");

        IReadOnlyList<F03Pick>? lots = null;
        if (await IsLotTracked(db, tx, tenant, lineId, byRight: false, ct))
        {
            lots = await PlanLotHold(db, tx, tenant, lineId, sku, warehouse, quantity, returns: true, ct, successor: true);
            if (lots.Count == 0) return new(LotUnavailable);
        }

        var error = await AllocateStock(db, tx, tenant, warehouse, sku, quantity, ct);
        if (error is not null) return new(error);

        Guid commandRow = Guid.NewGuid();
        var command = new ReserveAStockCommand("APP05-RESERVE", commandRow.ToString("N"),
            "APP05-RESERVE-" + commandRow.ToString("N"), orderId, lineId, warehouse, quantity);
        // The APP-05 receipt owns idempotency. The regular allocation command/source
        // remains available to the C.2, D.3 and F03 stock and lot proofs.
        await InsertReceivedCommand(db, tx, tenant, actor, command, commandRow, hash, "warehouse", null, null, ct);
        var (source, right) = await RecordAStockRight(db, tx, tenant, actor, commandRow, command.SourceNumber,
            customer.Value, destination, lineId, sku, warehouse, quantity, lots, ct);
        await db.ExecuteAsync(Sql($"""
            INSERT dbo.App05ASuccessorReserve(TenantId,BindingId,OrderLineId,DestinationTripId,WarehouseId,SkuId,RightId)
            VALUES(@Tenant,@Binding,@LineId,@Destination,@Warehouse,@Sku,@Right);
            UPDATE dbo.BusinessCommand SET Status='APPLIED',OutcomeCode='A_STOCK_RESERVED',
                CompletedAtUtc={App02c6Clock.SqlUtcNow}
            WHERE TenantId=@Tenant AND Id=@CommandRow AND Status='RECEIVED';
            """, new { Tenant = tenant, Binding = binding, LineId = lineId, Destination = destination, Warehouse = warehouse,
                Sku = sku, Right = right, CommandRow = commandRow }, tx, ct));
        return new(null, source, right);
    }
}
