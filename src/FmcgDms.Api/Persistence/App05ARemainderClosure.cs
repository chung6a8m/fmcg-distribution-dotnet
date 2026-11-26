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

    private sealed record ClosureProposal(Guid ProposalId, Guid CommandId, decimal Quantity,
        string Reason, string Maker, DateTimeOffset RecordedAt);

    private sealed record ClosureProposalScope(Guid ConfirmSourceId, Guid FollowUpId, Guid SkuId,
        decimal QuantityStep, decimal Remaining, decimal Held, decimal OnVehicle, bool PendingDelivery, string Revision)
    {
        public bool CanPropose => Remaining > 0 && OnVehicle == 0 && !PendingDelivery;
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
                var scope = await ReadClosureProposalScope(db, tx, tenant, actor, tripId, lineId, ct);
                if (scope is null) return Results.NotFound();
                var proposals = await db.QueryAsync<ClosureProposal>(App02a3Sql.Cmd("""
                    SELECT ProposalId,CommandId,Quantity,Reason,Maker,RecordedAt FROM dbo.App05ARemainderClosureProposal
                    WHERE TenantId=@Tenant AND TripId=@TripId AND OrderLineId=@LineId ORDER BY RecordedAt,ProposalId;
                    """, new { Tenant = tenant, TripId = tripId, LineId = lineId }, tx, ct));
                return Results.Ok(new { Scope = scope, CanPropose = ops && scope.CanPropose, Proposals = proposals });
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
    }

    private static async Task<ClosureProposalScope?> ReadClosureProposalScope(SqlConnection db, SqlTransaction tx,
        string tenant, string actor, Guid tripId, Guid lineId, CancellationToken ct)
    {
        var source = await ReadSuccessorSource(db, tx, tenant, tripId, lineId, ct);
        if (source is null) return null;
        var state = await ReadSuccessorState(db, tx, tenant, actor, tripId, source, ct);
        var preview = await Read(db, tx, tenant, tripId, true, ct, successor: true);
        var line = preview.ALines.SingleOrDefault(line => line.OrderLineId == lineId);
        if (line is null) return null;
        bool pending = await db.ExecuteScalarAsync<bool>(App02a3Sql.Cmd("""
            SELECT CONVERT(bit,CASE WHEN EXISTS(
                SELECT 1 FROM dbo.C4DeliveryEvidence e
                JOIN dbo.BusinessCommand c ON c.TenantId=e.TenantId AND c.Id=e.Id AND c.Status='RECEIVED'
                JOIN dbo.TripManifestLine m ON m.TenantId=e.TenantId AND m.Id=e.ManifestLineId
                JOIN dbo.AReservation a ON a.TenantId=m.TenantId AND a.StockRightId=m.StockRightId
                WHERE a.TenantId=@Tenant AND a.AOrderLineId=@LineId
            ) THEN 1 ELSE 0 END);
            """, new { Tenant = tenant, LineId = lineId }, tx, ct));
        string revision = Convert.ToHexString(Digest(new { state.Revision, Line = line, Pending = pending }));
        return new(line.ConfirmSourceId, source.FollowUpId, line.SkuId, source.Step, line.Remaining, line.Held,
            line.OnVehicle, pending, revision);
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

        var scope = await ReadClosureProposalScope(db, tx, tenant, actor, tripId, lineId, ct);
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
        return await Record(new { ProposalId = proposalId, input.Quantity, Status = "PENDING" }, true);

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
