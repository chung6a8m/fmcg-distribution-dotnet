using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using FmcgDms.Api.Tenancy;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

internal static partial class App05CloseReadiness
{
    private static readonly JsonSerializerOptions ClosureJson = new(JsonSerializerDefaults.Web);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record CloseInput(Guid CommandId, string? ExpectedRevision);

    private sealed record ClosureRow(Guid ClosureId, Guid TripId, Guid CommandId, string Status,
        DateTimeOffset ClosedAt, string ClosedBy, int OpenWorkCount, Guid HandoffId, string Snapshot, byte[] PayloadHash)
    {
        internal object Receipt() => new
        {
            ClosureId, TripId, CommandId, Status, ClosedAt, ClosedBy, OpenWorkCount, HandoffId,
            Snapshot = JsonSerializer.Deserialize<JsonElement>(Snapshot, ClosureJson)
        };
    }
    private sealed record CloseRejection(string Code, byte[] PayloadHash);

    private const string ClosureSql = """
        SELECT ClosureId,TripId,CommandId,Status,ClosedAt,ActorId AS ClosedBy,OpenWorkCount,HandoffId,Snapshot,PayloadHash
        FROM dbo.App05TripClosure WHERE TenantId=@Tenant
        """;

    private static void MapClosures(RouteGroupBuilder routes)
    {
        routes.MapPost("/{tripId:guid}/close", async (Guid tripId, CloseInput input, HttpContext context,
            TenantConnections connections, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (input.CommandId == Guid.Empty || input.ExpectedRevision is not { Length: 64 } revision ||
                !revision.All(char.IsAsciiHexDigit))
                return Results.BadRequest(new { Code = "TRIP_CLOSE_INPUT_INVALID" });

            return await Run(connections, context.User, tripId, true, input.CommandId, (db, tx, tenant, actor, _) =>
                Close(db, tx, tenant, actor, tripId, input.CommandId, revision.ToUpperInvariant(), connections.UsesApp05Successor, ct), ct);
        });
        routes.MapGet("/{tripId:guid}/closure", async (Guid tripId, HttpContext context, TenantConnections connections,
            CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await Run(connections, context.User, tripId, false, null, async (db, tx, tenant, actor, ops) =>
            {
                var row = await db.QuerySingleOrDefaultAsync<ClosureRow>(App02a3Sql.Cmd(
                    ClosureSql + " AND TripId=@Trip;", new { Tenant = tenant, Trip = tripId }, tx, ct));
                var preview = await Read(db, tx, tenant, tripId, ops, ct, successor: connections.UsesApp05Successor);
                if (row is null)
                    return new { Closure = row?.Receipt(), CurrentOpenWorkCount = preview.Work.Length };
                if (row.OpenWorkCount == 0)
                    return new { Closure = (object?)row.Receipt(), CurrentOpenWorkCount = 0 };
                var completions = await ReadWorkCompletions(db, tx, tenant, actor, tripId, connections.UsesApp05Successor, ct);
                return new { Closure = (object?)row.Receipt(),
                    CurrentOpenWorkCount = ClosingWork(row).Count(work => !ProveWork(preview, work, completions).IsCompleted) };
            }, ct));
        });
    }

    internal static string? CloseTarget(string status, string state, Coverage coverage, Guid? handoffId)
    {
        if (status != "RECONCILING" || handoffId is null || coverage.Expected == 0 || coverage.Known != coverage.Expected)
            return null;
        return state switch
        {
            "READY" => "CLOSED",
            "READY_WITH_WORK" => "CLOSED_WITH_OPEN_CASES",
            _ => null
        };
    }

    private static async Task<IResult> Close(SqlConnection db, SqlTransaction tx, string tenant, string actor, Guid trip,
        Guid commandId, string expectedRevision, bool successor, CancellationToken ct)
    {
        var payloadHash = Digest(new { Trip = trip, ExpectedRevision = expectedRevision });
        var scope = new { Tenant = tenant, Actor = actor, CommandId = commandId, Trip = trip };
        var applied = await db.QuerySingleOrDefaultAsync<ClosureRow>(App02a3Sql.Cmd(
            ClosureSql + " AND ActorId=@Actor AND CommandId=@CommandId;", scope, tx, ct));
        if (applied is not null)
            return CryptographicOperations.FixedTimeEquals(applied.PayloadHash, payloadHash)
                ? Results.Ok(applied.Receipt()) : Results.Conflict(new { Code = "COMMAND_PAYLOAD_CONFLICT" });
        var rejected = await db.QuerySingleOrDefaultAsync<CloseRejection>(App02a3Sql.Cmd("""
            SELECT Code,PayloadHash FROM dbo.App05TripCloseRejection
            WHERE TenantId=@Tenant AND ActorId=@Actor AND CommandId=@CommandId;
            """, scope, tx, ct));
        if (rejected is not null)
            return Results.Conflict(new
            {
                Code = CryptographicOperations.FixedTimeEquals(rejected.PayloadHash, payloadHash)
                    ? rejected.Code : "COMMAND_PAYLOAD_CONFLICT"
            });
        if (await db.ExecuteScalarAsync<bool>(App02a3Sql.Cmd("""
            SELECT CONVERT(bit,CASE WHEN EXISTS(SELECT 1 FROM dbo.App05FollowUpAssignment
                WHERE TenantId=@Tenant AND ActorId=@Actor AND CommandId=@CommandId) THEN 1 ELSE 0 END);
            """, scope, tx, ct)))
            return Results.Conflict(new { Code = "COMMAND_PAYLOAD_CONFLICT" });

        if (successor && await SuccessorCommandUsed(db, tx, tenant, actor, commandId, ct))
            return Results.Conflict(new { Code = "COMMAND_PAYLOAD_CONFLICT" });

        var preview = await Read(db, tx, tenant, trip, true, ct, closing: true, successor: successor);
        string? target = CloseTarget(preview.Status, preview.State, preview.Coverage, preview.HandoffId);
        string? code = preview.Status is "CLOSED" or "CLOSED_WITH_OPEN_CASES" ? "TRIP_ALREADY_CLOSED"
            : preview.Revision != expectedRevision ? "READINESS_CHANGED"
            : target is null ? "TRIP_CLOSE_NOT_READY" : null;
        if (code is not null)
        {
            await db.ExecuteAsync(App02a3Sql.Cmd("""
                INSERT dbo.App05TripCloseRejection(TenantId,TripId,ActorId,CommandId,PayloadHash,Code)
                VALUES(@Tenant,@Trip,@Actor,@CommandId,@PayloadHash,@Code);
                """, new { scope.Tenant, scope.Trip, scope.Actor, scope.CommandId, PayloadHash = payloadHash, Code = code }, tx, ct));
            return Results.Conflict(new { Code = code });
        }

        var facts = new
        {
            preview.TripId, preview.Code, preview.BusinessDate, preview.AsOf, preview.Revision, preview.Coverage,
            preview.HandoffId, preview.ALines, preview.Receivables, preview.Work
        };
        var row = new ClosureRow(Guid.NewGuid(), trip, commandId, target!, preview.AsOf, actor, preview.Work.Length,
            preview.HandoffId!.Value, JsonSerializer.Serialize(facts, ClosureJson), payloadHash);
        await db.ExecuteAsync(App02a3Sql.Cmd("""
            UPDATE dbo.Trip SET Status=@Status WHERE TenantId=@Tenant AND Id=@TripId AND Status='RECONCILING';
            IF @@ROWCOUNT<>1 THROW 51057, 'APP-05.2 trip status changed during closure.', 1;

            INSERT dbo.App05TripClosure(TenantId,TripId,ClosureId,ActorId,CommandId,ExpectedRevision,PayloadHash,
                Status,ClosedAt,OpenWorkCount,HandoffId,Snapshot)
            VALUES(@Tenant,@TripId,@ClosureId,@ClosedBy,@CommandId,@ExpectedRevision,@PayloadHash,
                @Status,@ClosedAt,@OpenWorkCount,@HandoffId,@Snapshot);
            """, new
        {
            scope.Tenant, row.TripId, row.ClosureId, row.ClosedBy, row.CommandId, ExpectedRevision = expectedRevision,
            row.PayloadHash, row.Status, row.ClosedAt, row.OpenWorkCount, row.HandoffId, row.Snapshot
        }, tx, ct));
        return Results.Ok(row.Receipt());
    }
}
