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
    private sealed record ClosureApprovalInput(Guid CommandId, string? ExpectedRevision);

    private sealed record ClosureApproval(Guid ProposalId, Guid SourceId, Guid CommandId, string Checker,
        [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] decimal Quantity,
        [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] decimal Released, DateTimeOffset ApprovedAt);

    private sealed record ClosureOrder(Guid OrderId, Guid CustomerId);

    private static string BaseQuantity(decimal quantity) => quantity.ToString("F3", CultureInfo.InvariantCulture);

    private static bool SameClosureActor(string actor, string maker) =>
        string.Equals(actor, maker, StringComparison.OrdinalIgnoreCase);

    private static void MapRemainderApproval(RouteGroupBuilder sources) =>
        sources.MapPost("/closure-proposals/{proposalId:guid}/approve", async (Guid tripId, Guid lineId, Guid proposalId,
            ClosureApprovalInput input, HttpContext context, TenantConnections connections, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (proposalId == Guid.Empty || input.CommandId == Guid.Empty
                || input.ExpectedRevision is not { Length: 64 } revision || !revision.All(char.IsAsciiHexDigit))
                return Results.BadRequest(new { Code = "A_CLOSURE_APPROVAL_INVALID" });
            return await Run(connections, context.User, tripId, true, input.CommandId, (db, tx, tenant, actor, _) =>
                ApproveClosure(db, tx, tenant, actor, tripId, lineId, proposalId,
                    input with { ExpectedRevision = revision.ToUpperInvariant() }, ct), ct);
        });

    private static async Task<IResult> ApproveClosure(SqlConnection db, SqlTransaction tx, string tenant, string actor,
        Guid tripId, Guid lineId, Guid proposalId, ClosureApprovalInput input, CancellationToken ct)
    {
        var key = new { Tenant = tenant, Actor = actor, input.CommandId };
        byte[] hash = Digest(new { Kind = "APP05_A_CLOSURE_APPROVAL", tripId, lineId, proposalId, input });
        var previous = await db.QuerySingleOrDefaultAsync<SuccessorReceipt>(App02a3Sql.Cmd("""
            SELECT PayloadHash,Receipt,Applied FROM dbo.App05ASuccessorCommand
            WHERE TenantId=@Tenant AND ActorId=@Actor AND CommandId=@CommandId;
            """, key, tx, ct));
        if (previous is not null)
            return CryptographicOperations.FixedTimeEquals(previous.PayloadHash, hash)
                ? Receipt(previous.Receipt, previous.Applied) : Results.Conflict(new { Code = "COMMAND_PAYLOAD_CONFLICT" });
        if (await db.ExecuteScalarAsync<bool>(App02a3Sql.Cmd("""
            SELECT CONVERT(bit,CASE WHEN EXISTS(SELECT 1 FROM dbo.App05FollowUpAssignment
                    WHERE TenantId=@Tenant AND ActorId=@Actor AND CommandId=@CommandId)
                OR EXISTS(SELECT 1 FROM dbo.App05TripClosure
                    WHERE TenantId=@Tenant AND ActorId=@Actor AND CommandId=@CommandId)
                OR EXISTS(SELECT 1 FROM dbo.App05TripCloseRejection
                    WHERE TenantId=@Tenant AND ActorId=@Actor AND CommandId=@CommandId) THEN 1 ELSE 0 END);
            """, key, tx, ct)))
            return Results.Conflict(new { Code = "COMMAND_PAYLOAD_CONFLICT" });

        await LockAOrderLine(db, tx, tenant, lineId, ct);
        var identity = new { key.Tenant, TripId = tripId, LineId = lineId, ProposalId = proposalId };
        var proposal = await db.QuerySingleOrDefaultAsync<ClosureProposal>(App02a3Sql.Cmd("""
            SELECT ProposalId,CommandId,Quantity,Reason,Maker,RecordedAt,SourceRevision
            FROM dbo.App05ARemainderClosureProposal
            WHERE TenantId=@Tenant AND ProposalId=@ProposalId AND TripId=@TripId AND OrderLineId=@LineId;
            """, identity, tx, ct));
        if (proposal is null) return Results.NotFound();
        if (SameClosureActor(actor, proposal.Maker)) return await Reject("A_CLOSURE_SELF_APPROVAL");
        if (await db.ExecuteScalarAsync<bool>(App02a3Sql.Cmd("""
            SELECT CONVERT(bit,CASE WHEN EXISTS(SELECT 1 FROM dbo.App05ARemainderApproval
                WHERE TenantId=@Tenant AND ProposalId=@ProposalId) THEN 1 ELSE 0 END);
            """, identity, tx, ct)))
            return await Reject("A_CLOSURE_ALREADY_APPROVED");

        await LockClosurePools(db, tx, tenant, tripId, lineId, ct);
        // C.4 locks the order before reading deliveries, including deliveries on sibling lines.
        var order = await db.QuerySingleOrDefaultAsync<ClosureOrder>(App02a3Sql.Cmd("""
            SELECT o.Id AS OrderId,o.CustomerId FROM dbo.AConfirmedLineSnapshot s
            JOIN dbo.AOrder o WITH (UPDLOCK,HOLDLOCK) ON o.TenantId=s.TenantId AND o.Id=s.AOrderId AND o.TripId=s.TripId
            WHERE s.TenantId=@Tenant AND s.TripId=@TripId AND s.AOrderLineId=@LineId
                AND o.Status IN ('CONFIRMED','PARTIALLY_DELIVERED');
            """, identity, tx, ct));
        if (order is null) return await Reject("A_CLOSURE_REMAINDER_NOT_ELIGIBLE");

        var scope = await ReadClosureProposalScope(db, tx, tenant, tripId, lineId, ct);
        if (scope is null || scope.Revision != input.ExpectedRevision || scope.Revision != Convert.ToHexString(proposal.SourceRevision))
            return await Reject("A_CLOSURE_SCOPE_CHANGED");
        if (!scope.CanPropose || proposal.Quantity > scope.Remaining || proposal.Quantity % scope.QuantityStep != 0)
            return await Reject("A_CLOSURE_REMAINDER_NOT_ELIGIBLE");
        if (scope.SuccessorActive)
            return await Reject("A_CLOSURE_SUCCESSOR_ACTIVE");

        var source = await ReadSuccessorSource(db, tx, tenant, tripId, lineId, ct)
            ?? throw new InvalidOperationException("The locked closure source disappeared.");

        // Close unheld obligations first. Any excess must be backed by proven MAIN rights.
        decimal released = Math.Max(0, proposal.Quantity - (scope.Remaining - scope.Held));
        var plan = await PlanClosureRelease(db, tx, tenant, tripId, source, released, ct);
        if (plan is null) return await Reject("A_CLOSURE_RELEASE_UNPROVEN");
        Guid sourceId = await App01a2CommandService.RecordClosureApprovalSource(db, tx, tenant, actor,
            proposalId, tripId, order.CustomerId, scope.SkuId, proposal.Quantity, hash, ct);
        await db.ExecuteAsync(App02a3Sql.Cmd($"""
            INSERT dbo.App05ARemainderApproval(TenantId,ProposalId,SourceId,OrderLineId,SkuId,Maker,Checker,CommandId,Quantity,Released,ApprovedAt)
            VALUES(@Tenant,@ProposalId,@SourceId,@LineId,@SkuId,@Maker,@Actor,@CommandId,@Quantity,@Released,
                TODATETIMEOFFSET({App02c6Clock.SqlUtcNow},'+00:00'));
            """, new { key.Tenant, identity.ProposalId, SourceId = sourceId, identity.LineId, scope.SkuId, proposal.Maker,
                key.Actor, key.CommandId, proposal.Quantity, Released = released }, tx, ct));
        await PostClosureRelease(db, tx, tenant, sourceId, tripId, source.Line, order.CustomerId, plan, ct);
        await db.ExecuteAsync(App02a3Sql.Cmd("""
            UPDATE o SET Status='REMAINDER_CLOSED' FROM dbo.AOrder o
            WHERE o.TenantId=@Tenant AND o.Id=@OrderId AND NOT EXISTS(
                SELECT 1 FROM dbo.AConfirmedLineSnapshot s
                WHERE s.TenantId=o.TenantId AND s.AOrderId=o.Id AND s.ConfirmedBaseQuantity>
                    (SELECT COALESCE(SUM(amount.Quantity),0) FROM (
                        SELECT d.BaseQuantity AS Quantity FROM dbo.SaleDelivery d
                        WHERE d.TenantId=s.TenantId AND d.AOrderLineId=s.AOrderLineId
                        UNION ALL
                        SELECT a.Quantity FROM dbo.App05ARemainderApproval a
                        WHERE a.TenantId=s.TenantId AND a.OrderLineId=s.AOrderLineId) amount));
            """, new { key.Tenant, order.OrderId }, tx, ct));

        return await Record(new { SourceId = sourceId, ProposalId = proposalId, input.CommandId, Status = "APPROVED",
            Quantity = BaseQuantity(proposal.Quantity), Released = BaseQuantity(released) }, true);

        Task<IResult> Reject(string code) => Record(new { Code = code }, false);

        async Task<IResult> Record<T>(T value, bool applied)
        {
            string receipt = JsonSerializer.Serialize(value, ClosureJson);
            await db.ExecuteAsync(App02a3Sql.Cmd($"""
                INSERT dbo.App05ASuccessorCommand(TenantId,ActorId,CommandId,PayloadHash,Receipt,Applied,RecordedAt)
                VALUES(@Tenant,@Actor,@CommandId,@PayloadHash,@Receipt,@Applied,TODATETIMEOFFSET({App02c6Clock.SqlUtcNow},'+00:00'));
                """, new { key.Tenant, key.Actor, key.CommandId, PayloadHash = hash, Receipt = receipt, Applied = applied }, tx, ct));
            return Receipt(receipt, applied);
        }
    }

    private static IResult Receipt(string json, bool applied) =>
        Results.Content(json, "application/json", statusCode: applied ? 200 : 409);
}
