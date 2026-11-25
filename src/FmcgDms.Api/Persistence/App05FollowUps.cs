using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

internal static partial class App05CloseReadiness
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record AssignInput(Guid CommandId, string? SourceKind, Guid SourceId, string? Assignee,
        string? DueAt, string? NextAction, string? Reason, string? ExpectedRevision);
    internal sealed record Assignment(Guid FollowUpId, string SourceKind, Guid SourceId, string Assignee,
        DateTimeOffset DueAt, string NextAction, string Reason, Guid CommandId, string ActorId, DateTimeOffset AssignedAt);
    private sealed record AssignmentReceipt(Guid FollowUpId, byte[] PayloadHash);

    private static void MapFollowUps(RouteGroupBuilder routes) =>
        routes.MapPost("/{tripId:guid}/follow-ups", async (Guid tripId, AssignInput input, HttpContext context,
            Tenancy.TenantConnections connections, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (input.CommandId == Guid.Empty || input.SourceId == Guid.Empty ||
                input.SourceKind is not (A or D4 or "F05_RETURN" or "F06_COST" or "F08_REFUND") ||
                !TextValid(input.Assignee, 128) || !TextValid(input.NextAction, 240) || !TextValid(input.Reason, 240) ||
                input.ExpectedRevision is not { Length: 64 } revision || !revision.All(char.IsAsciiHexDigit) ||
                !TryDueAt(input.DueAt, out var dueAt))
                return Results.BadRequest(new { Code = "FOLLOW_UP_INPUT_INVALID" });

            var request = input with
            {
                Assignee = input.Assignee!.Trim(),
                NextAction = input.NextAction!.Trim(),
                Reason = input.Reason!.Trim(),
                ExpectedRevision = revision.ToUpperInvariant()
            };
            return await Run(connections, context.User, tripId, true, request.CommandId, (db, tx, tenant, actor, _) =>
                Assign(db, tx, tenant, actor, tripId, request, dueAt, connections.UsesApp05TripClosure, connections.UsesApp05Successor, ct), ct);
        });

    private static bool TextValid(string? value, int maximum) =>
        value?.Trim() is { Length: > 0 } trimmed && trimmed.Length <= maximum && !trimmed.Any(char.IsControl);

    internal static bool TryDueAt(string? value, out DateTimeOffset dueAt)
    {
        dueAt = default;
        if (value is null || value.Length is < 17 or > 33)
            return false;
        if (value.EndsWith('Z'))
            value = value[..^1] + "+00:00";
        string[] formats = ["yyyy-MM-dd'T'HH:mmzzz", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];
        return DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out dueAt);
    }

    private static async Task<IResult> Assign(SqlConnection db, SqlTransaction tx, string tenant, string actor, Guid trip,
        AssignInput request, DateTimeOffset dueAt, bool closing, bool successor, CancellationToken ct)
    {
        var scope = new { Tenant = tenant, Actor = actor, request.CommandId };
        var payloadHash = Digest(new
        {
            Trip = trip, request.SourceKind, request.SourceId, request.Assignee, DueAt = dueAt, request.NextAction,
            request.Reason, request.ExpectedRevision
        });
        var replay = await db.QuerySingleOrDefaultAsync<AssignmentReceipt>(App02a3Sql.Cmd("""
            SELECT FollowUpId,PayloadHash FROM dbo.App05FollowUpAssignment
            WHERE TenantId=@Tenant AND ActorId=@Actor AND CommandId=@CommandId;
            """, scope, tx, ct));
        if (replay is not null)
            return CryptographicOperations.FixedTimeEquals(replay.PayloadHash, payloadHash)
                ? Results.Ok(new { replay.FollowUpId }) : Results.Conflict(new { Code = "COMMAND_PAYLOAD_CONFLICT" });
        if (closing && await db.ExecuteScalarAsync<bool>(App02a3Sql.Cmd("""
            SELECT CONVERT(bit,CASE WHEN EXISTS(SELECT 1 FROM dbo.App05TripClosure
                    WHERE TenantId=@Tenant AND ActorId=@Actor AND CommandId=@CommandId)
                OR EXISTS(SELECT 1 FROM dbo.App05TripCloseRejection
                    WHERE TenantId=@Tenant AND ActorId=@Actor AND CommandId=@CommandId) THEN 1 ELSE 0 END);
            """, scope, tx, ct)))
            return Results.Conflict(new { Code = "COMMAND_PAYLOAD_CONFLICT" });

        if (successor && await SuccessorCommandUsed(db, tx, tenant, actor, request.CommandId, ct))
            return Results.Conflict(new { Code = "COMMAND_PAYLOAD_CONFLICT" });

        var preview = await Read(db, tx, tenant, trip, true, ct, closing, successor);
        if (!preview.CanAssign || preview.Revision != request.ExpectedRevision)
            return Results.Conflict(new { Code = preview.CanAssign ? "READINESS_CHANGED" : "TRIP_ASSIGNMENT_NOT_OPEN" });
        if (!preview.Work.Any(row => row.SourceKind == request.SourceKind && row.SourceId == request.SourceId))
            return Results.Conflict(new { Code = "FOLLOW_UP_SOURCE_NOT_OPEN" });
        if (!preview.Assignees.Contains(request.Assignee!, StringComparer.Ordinal))
            return Results.BadRequest(new { Code = "ASSIGNEE_NOT_IN_SCOPE" });
        if (dueAt <= preview.AsOf)
            return Results.BadRequest(new { Code = "DUE_AT_MUST_FOLLOW_AS_OF" });

        var id = await db.ExecuteScalarAsync<Guid>(App02a3Sql.Cmd("""
            DECLARE @FollowUpId uniqueidentifier = (SELECT FollowUpId FROM dbo.App05FollowUp WITH (UPDLOCK,HOLDLOCK)
                WHERE TenantId=@Tenant AND TripId=@Trip AND SourceKind=@SourceKind AND SourceId=@SourceId);
            IF @FollowUpId IS NULL
            BEGIN
                SET @FollowUpId=NEWID();
                INSERT dbo.App05FollowUp(TenantId,TripId,SourceKind,SourceId,FollowUpId)
                VALUES(@Tenant,@Trip,@SourceKind,@SourceId,@FollowUpId);
            END;
            INSERT dbo.App05FollowUpAssignment(TenantId,TripId,SourceKind,SourceId,FollowUpId,ActorId,CommandId,
                PayloadHash,ExpectedRevision,Assignee,DueAt,NextAction,Reason,AssignedAt)
            VALUES(@Tenant,@Trip,@SourceKind,@SourceId,@FollowUpId,@Actor,@CommandId,@PayloadHash,
                @ExpectedRevision,@Assignee,@DueAt,@NextAction,@Reason,@AssignedAt);
            SELECT @FollowUpId;
            """, new
        {
            scope.Tenant, scope.Actor, scope.CommandId, Trip = trip, request.SourceKind, request.SourceId,
            PayloadHash = payloadHash, request.ExpectedRevision, request.Assignee, DueAt = dueAt, request.NextAction,
            request.Reason, AssignedAt = preview.AsOf
        }, tx, ct));
        return Results.Ok(new { FollowUpId = id });
    }

    private const string AssignmentsSql = """
        SELECT f.FollowUpId,f.SourceKind,f.SourceId,a.Assignee,a.DueAt,a.NextAction,a.Reason,a.CommandId,a.ActorId,a.AssignedAt
        FROM dbo.App05FollowUp f CROSS APPLY(
            SELECT TOP(1) Assignee,DueAt,NextAction,Reason,CommandId,ActorId,AssignedAt
            FROM dbo.App05FollowUpAssignment a WHERE a.TenantId=f.TenantId AND a.FollowUpId=f.FollowUpId
            ORDER BY a.EventId DESC
        ) a WHERE f.TenantId=@Tenant AND f.TripId=@Trip;
        """;
}
