using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using FmcgDms.Api.Tenancy;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

internal static partial class App05CloseReadiness
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record ClosureProposalInput(Guid CommandId, Guid ConfirmSourceId, Guid FollowUpId,
        [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] decimal Quantity,
        string? Reason, string? ExpectedRevision);

    private sealed record ClosureProposal(Guid ProposalId, Guid CommandId,
        [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] decimal Quantity,
        string Reason, string Maker, DateTimeOffset RecordedAt, Guid ConfirmSourceId, Guid FollowUpId,
        [property: JsonIgnore] byte[] SourceRevision)
    {
        public string ExpectedRevision => Convert.ToHexString(SourceRevision);
    }

    private sealed record ClosureProposalScope(Guid ConfirmSourceId, Guid FollowUpId, Guid SkuId,
        [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] decimal QuantityStep,
        [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] decimal Remaining,
        [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] decimal Held,
        [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] decimal OnVehicle,
        bool PendingDelivery, bool SuccessorActive, string Revision)
    {
        public bool CanPropose => Remaining > 0 && DeliveryBlock is null;

        [JsonIgnore]
        public string? DeliveryBlock => PendingDelivery ? "A_CLOSURE_DELIVERY_PENDING"
            : OnVehicle > 0 ? "A_CLOSURE_ON_VEHICLE" : null;

        [JsonIgnore]
        public string? ApprovalBlock => DeliveryBlock ?? (SuccessorActive ? "A_CLOSURE_SUCCESSOR_ACTIVE" : null);
    }

    internal static bool IsClosureProposalInputValid(ClosureProposalInput input) =>
        input is { CommandId: var command, ConfirmSourceId: var confirm, FollowUpId: var followUp,
            Quantity: > 0 and <= 999999999999999.999m, ExpectedRevision: { Length: 64 } revision }
        && command != Guid.Empty && confirm != Guid.Empty && followUp != Guid.Empty
        && decimal.Round(input.Quantity, 3) == input.Quantity && TextValid(input.Reason, 240)
        && revision.All(char.IsAsciiHexDigit);

    private static void MapRemainderClosure(RouteGroupBuilder sources)
    {
        sources.MapGet("/closure-proposals", async (Guid tripId, Guid lineId, HttpContext context,
            TenantConnections connections, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await Run<IResult>(connections, context.User, tripId, false, null, async (db, tx, tenant, actor, ops) =>
            {
                var scope = await ReadClosureProposalScope(db, tx, tenant, tripId, lineId, ct);
                if (scope is null) return Results.NotFound();
                var proposals = await db.QueryAsync<ClosureProposal>(App02a3Sql.Cmd("""
                    SELECT ProposalId,CommandId,Quantity,Reason,Maker,RecordedAt,ConfirmSourceId,FollowUpId,SourceRevision
                    FROM dbo.App05ARemainderClosureProposal
                    WHERE TenantId=@Tenant AND TripId=@TripId AND OrderLineId=@LineId ORDER BY RecordedAt,ProposalId;
                    """, new { Tenant = tenant, TripId = tripId, LineId = lineId }, tx, ct));
                var approvals = (await db.QueryAsync<ClosureApproval>(App02a3Sql.Cmd("""
                    SELECT a.ProposalId,a.SourceId,a.CommandId,a.Checker,a.Quantity,a.Released,a.ApprovedAt
                    FROM dbo.App05ARemainderApproval a JOIN dbo.App05ARemainderClosureProposal p
                        ON p.TenantId=a.TenantId AND p.ProposalId=a.ProposalId
                    WHERE p.TenantId=@Tenant AND p.TripId=@TripId AND p.OrderLineId=@LineId;
                    """, new { Tenant = tenant, TripId = tripId, LineId = lineId }, tx, ct)))
                    .ToDictionary(a => a.ProposalId);
                var rows = proposals.Select(p =>
                {
                    var approval = approvals.GetValueOrDefault(p.ProposalId);
                    bool stale = p.ExpectedRevision != scope.Revision;
                    return new
                    {
                        p.ProposalId, p.CommandId, p.ConfirmSourceId, p.FollowUpId, p.ExpectedRevision,
                        Quantity = BaseQuantity(p.Quantity), p.Reason, p.Maker, p.RecordedAt,
                        Approval = approval,
                        Status = approval is not null ? "APPROVED" : stale ? "STALE" : "PENDING",
                        BlockedReason = approval is null ? scope.ApprovalBlock : null,
                        CanApprove = approval is null && !stale && ops && !SameClosureActor(actor, p.Maker)
                            && scope.ApprovalBlock is null && scope.CanPropose && p.Quantity <= scope.Remaining
                            && p.Quantity % scope.QuantityStep == 0
                    };
                });
                return Results.Ok(new { Scope = scope, CanPropose = ops && scope.CanPropose, Proposals = rows });
            }, ct);
        });

        sources.MapPost("/closure-proposals", async (Guid tripId, Guid lineId, ClosureProposalInput input,
            HttpContext context, TenantConnections connections, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!IsClosureProposalInputValid(input)) return Results.BadRequest(new { Code = "A_CLOSURE_PROPOSAL_INVALID" });
            var normalized = input with { Reason = input.Reason!.Trim(), ExpectedRevision = input.ExpectedRevision!.ToUpperInvariant() };
            return await Run(connections, context.User, tripId, true, input.CommandId, (db, tx, tenant, actor, _) =>
                ProposeClosure(db, tx, tenant, actor, tripId, lineId, normalized, ct), ct);
        });
        MapRemainderApproval(sources);
    }

    internal static Task LockAOrderLine(SqlConnection db, SqlTransaction tx, string tenant, Guid lineId, CancellationToken ct) =>
        App02a3Sql.Lock(db, tx, $"APP05:A-ORDER-LINE:{tenant}:{lineId:N}", ct);

    private sealed record ClosureProposalStock(decimal RightRemainder, decimal Consumed, decimal Loaded, decimal Returned);

    private static async Task<ClosureProposalScope?> ReadClosureProposalScope(SqlConnection db, SqlTransaction tx,
        string tenant, Guid tripId, Guid lineId, CancellationToken ct)
    {
        await LockAOrderLine(db, tx, tenant, lineId, ct);
        var source = await ReadSuccessorSource(db, tx, tenant, tripId, lineId, ct);
        if (source is null) return null;
        var stock = await db.QuerySingleAsync<ClosureProposalStock>(App02a3Sql.Cmd("""
            SELECT rights.RightRemainder,rights.Consumed,manifest.Loaded,manifest.Returned
            FROM (
                SELECT COALESCE(SUM(r.GrantedBaseQuantity-r.ConsumedBaseQuantity-r.ReleasedBaseQuantity),0) AS RightRemainder,
                    COALESCE(SUM(r.ConsumedBaseQuantity),0) AS Consumed
                FROM dbo.AReservation a JOIN dbo.StockRight r ON r.TenantId=a.TenantId AND r.Id=a.StockRightId
                WHERE a.TenantId=@Tenant AND a.AOrderLineId=@LineId
            ) rights
            CROSS JOIN (
                SELECT COALESCE(SUM(m.LoadedBaseQuantity),0) AS Loaded,COALESCE(SUM(u.BaseQuantity),0) AS Returned
                FROM dbo.AReservation a JOIN dbo.TripManifestLine m ON m.TenantId=a.TenantId AND m.StockRightId=a.StockRightId
                LEFT JOIN dbo.D3TripUnloadLine u ON u.TenantId=m.TenantId AND u.TripManifestLineId=m.Id
                WHERE a.TenantId=@Tenant AND a.AOrderLineId=@LineId
            ) manifest;
            """, new { Tenant = tenant, LineId = lineId }, tx, ct));
        var delivered = source.Delivered;
        var onVehicle = stock.Loaded - delivered - stock.Returned;
        var held = stock.RightRemainder - onVehicle;
        if (stock.Consumed != delivered || onVehicle < 0 || held < 0 || source.CurrentRemaining < 0 || stock.RightRemainder > source.CurrentRemaining)
            return null;
        bool pending = await db.ExecuteScalarAsync<bool>(App02a3Sql.Cmd("""
            SELECT CONVERT(bit,CASE WHEN EXISTS(
                SELECT 1 FROM dbo.C4DeliveryEvidence e
                JOIN dbo.BusinessCommand c ON c.TenantId=e.TenantId AND c.Id=e.Id AND c.Status IN ('RECEIVED','PENDING')
                JOIN dbo.TripManifestLine m ON m.TenantId=e.TenantId AND m.Id=e.ManifestLineId
                JOIN dbo.AReservation a ON a.TenantId=m.TenantId AND a.StockRightId=m.StockRightId
                WHERE a.TenantId=@Tenant AND a.AOrderLineId=@LineId
            ) THEN 1 ELSE 0 END);
            """, new { Tenant = tenant, LineId = lineId }, tx, ct));
        bool successorActive = await db.ExecuteScalarAsync<bool>(App02a3Sql.Cmd("""
            SELECT CONVERT(bit,CASE WHEN EXISTS(
                SELECT 1 FROM dbo.App05ASuccessor b WHERE b.TenantId=@Tenant AND b.OrderLineId=@LineId
                    AND b.Quantity>(SELECT COALESCE(SUM(d.BaseQuantity),0) FROM dbo.SaleDelivery d
                        WHERE d.TenantId=b.TenantId AND d.App05BindingId=b.BindingId AND d.TripId=b.DestinationTripId)
            ) THEN 1 ELSE 0 END);
            """, new { Tenant = tenant, LineId = lineId }, tx, ct));
        string sourceFingerprint = await db.ExecuteScalarAsync<string>(App02a3Sql.Cmd("""
            SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',(
                SELECT JSON_QUERY((SELECT r.Id,r.RowVersion
                    FROM dbo.AReservation a JOIN dbo.StockRight r ON r.TenantId=a.TenantId AND r.Id=a.StockRightId
                    WHERE a.TenantId=@Tenant AND a.AOrderLineId=@LineId ORDER BY r.Id FOR JSON PATH)) AS Rights,
                    JSON_QUERY((SELECT BindingId,Quantity FROM dbo.App05ASuccessor
                    WHERE TenantId=@Tenant AND OrderLineId=@LineId ORDER BY BindingId FOR JSON PATH)) AS Bindings,
                    JSON_QUERY((SELECT SuccessorRightId,NeverLoaded,Returned FROM dbo.App05AHeldTransfer
                    WHERE TenantId=@Tenant AND OrderLineId=@LineId ORDER BY SuccessorRightId FOR JSON PATH)) AS Transfers
                FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)),2);
            """, new { Tenant = tenant, LineId = lineId }, tx, ct)) ?? throw new InvalidOperationException("The closure source fingerprint is missing.");
        string revision = Convert.ToHexString(Digest(new { sourceFingerprint, source.Line.ConfirmSourceId, source.FollowUpId,
            source.CurrentRemaining, Held = held, OnVehicle = onVehicle, Pending = pending }));
        return new(source.Line.ConfirmSourceId, source.FollowUpId, source.Line.SkuId, source.Step, source.CurrentRemaining,
            held, onVehicle, pending, successorActive, revision);
    }

    private static async Task<IResult> ProposeClosure(SqlConnection db, SqlTransaction tx, string tenant, string actor,
        Guid tripId, Guid lineId, ClosureProposalInput input, CancellationToken ct)
    {
        var identity = new { Tenant = tenant, Actor = actor, input.CommandId };
        byte[] hash = Digest(new { Kind = "APP05_A_CLOSURE_PROPOSAL", TripId = tripId, LineId = lineId,
            Input = input with { Quantity = 0 }, QuantityBase = input.Quantity.ToString("F3", CultureInfo.InvariantCulture) });
        var previous = await db.QuerySingleOrDefaultAsync<SuccessorReceipt>(App02a3Sql.Cmd("""
            SELECT PayloadHash,Receipt,Applied FROM dbo.App05ASuccessorCommand
            WHERE TenantId=@Tenant AND ActorId=@Actor AND CommandId=@CommandId;
            """, identity, tx, ct));
        if (previous is not null)
            return CryptographicOperations.FixedTimeEquals(previous.PayloadHash, hash)
                ? Results.Content(previous.Receipt, "application/json", statusCode: previous.Applied ? 200 : 409) : Results.Conflict(new { Code = "COMMAND_PAYLOAD_CONFLICT" });
        if (await db.ExecuteScalarAsync<bool>(App02a3Sql.Cmd("""
            SELECT CONVERT(bit,CASE WHEN EXISTS(SELECT 1 FROM dbo.App05FollowUpAssignment
                    WHERE TenantId=@Tenant AND ActorId=@Actor AND CommandId=@CommandId)
                OR EXISTS(SELECT 1 FROM dbo.App05TripClosure
                    WHERE TenantId=@Tenant AND ActorId=@Actor AND CommandId=@CommandId)
                OR EXISTS(SELECT 1 FROM dbo.App05TripCloseRejection
                    WHERE TenantId=@Tenant AND ActorId=@Actor AND CommandId=@CommandId) THEN 1 ELSE 0 END);
            """, identity, tx, ct)))
            return Results.Conflict(new { Code = "COMMAND_PAYLOAD_CONFLICT" });

        var scope = await ReadClosureProposalScope(db, tx, tenant, tripId, lineId, ct);
        if (scope?.ConfirmSourceId != input.ConfirmSourceId || scope?.FollowUpId != input.FollowUpId)
            return await Record(new { Code = "A_CLOSURE_SOURCE_INVALID" }, false);
        if (scope.Revision != input.ExpectedRevision)
            return await Record(new { Code = "A_CLOSURE_SCOPE_CHANGED" }, false);
        if (!scope.CanPropose || input.Quantity > scope.Remaining || input.Quantity % scope.QuantityStep != 0)
            return await Record(new { Code = "A_CLOSURE_REMAINDER_NOT_ELIGIBLE" }, false);

        Guid proposalId = Guid.NewGuid();
        await db.ExecuteAsync(App02a3Sql.Cmd($"""
            INSERT dbo.App05ARemainderClosureProposal(TenantId,ProposalId,TripId,OrderLineId,ConfirmSourceId,SkuId,
                FollowUpId,CommandId,Maker,Quantity,Reason,SourceRevision,RecordedAt)
            VALUES(@Tenant,@ProposalId,@TripId,@LineId,@ConfirmSourceId,@SkuId,
                @FollowUpId,@CommandId,@Actor,@Quantity,@Reason,@Revision,TODATETIMEOFFSET({App02c6Clock.SqlUtcNow},'+00:00'));
            """, new { identity.Tenant, ProposalId = proposalId, TripId = tripId, LineId = lineId, input.ConfirmSourceId,
                scope.SkuId, input.FollowUpId, input.CommandId, identity.Actor, input.Quantity, input.Reason,
                Revision = Convert.FromHexString(scope.Revision) }, tx, ct));
        return await Record(new { ProposalId = proposalId, Quantity = input.Quantity.ToString("F3", CultureInfo.InvariantCulture),
            Status = "PENDING" }, true);

        async Task<IResult> Record<T>(T value, bool applied)
        {
            string receipt = JsonSerializer.Serialize(value, ClosureJson);
            await db.ExecuteAsync(App02a3Sql.Cmd($"""
                INSERT dbo.App05ASuccessorCommand(TenantId,ActorId,CommandId,PayloadHash,Receipt,Applied,RecordedAt)
                VALUES(@Tenant,@Actor,@CommandId,@PayloadHash,@Receipt,@Applied,TODATETIMEOFFSET({App02c6Clock.SqlUtcNow},'+00:00'));
                """, new { identity.Tenant, identity.Actor, input.CommandId, PayloadHash = hash, Receipt = receipt, Applied = applied }, tx, ct));
            return Results.Content(receipt, "application/json", statusCode: applied ? 200 : 409);
        }
    }
}
