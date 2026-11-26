using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using FmcgDms.Api.Tenancy;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

internal static partial class App05CloseReadiness
{
    private sealed record CommandHistoryRow(Guid CommandId, Guid TripId, string TripCode, string Kind,
        string Status, string? SourceKind, Guid? SourceId, Guid? OrderId, Guid? ResultId, string? Code, DateTimeOffset? RecordedAt);

    private static void MapCommandHistory(RouteGroupBuilder routes)
    {
        routes.MapGet("/commands", async (Guid? commandId, Guid? tripId, int? page, HttpContext context, TenantConnections connections, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (commandId == Guid.Empty || tripId == Guid.Empty || page is < 0 or > 1_000_000) return Results.BadRequest();
            var user = context.User;
            string[] allowedRoles = connections.UsesApp05Successor
                ? [OpsRole, AssigneeRole, "warehouse", "sales"] : [OpsRole, AssigneeRole];
            var roles = allowedRoles.Where(role => user.HasClaim("synthetic_role", role)).ToArray();
            if (roles.Length == 0)
                throw new UnauthorizedAccessException("A current scoped role is required for command history.");

            string tenant = App02a3Sql.Tenant(user);
            string actor = App02a3Sql.Actor(user);
            await using var db = await connections.OpenApp02c4Async(user, ct);
            await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var active = await db.QueryAsync<string>(App02a3Sql.Cmd("""
                SELECT RoleCode FROM dbo.App02ActorRoleGrant
                WHERE TenantId=@Tenant AND ActorId=@Actor AND IsActive=1 AND RoleCode IN @Roles;
                """, new { Tenant = tenant, Actor = actor, Roles = roles }, tx, ct));
            var grants = active.ToHashSet(StringComparer.Ordinal);
            if (grants.Count == 0)
                throw new UnauthorizedAccessException("A current scoped role is required for command history.");

            var result = await db.QueryAsync<CommandHistoryRow>(App02a3Sql.Cmd(CommandHistorySql(connections.UsesApp05Successor),
                new
                {
                    Tenant = tenant, Actor = actor, CommandId = commandId, TripId = tripId, Offset = (page ?? 0) * 50,
                    Operations = grants.Contains(OpsRole) || grants.Contains(AssigneeRole),
                    Warehouse = grants.Contains("warehouse"), Sales = grants.Contains("sales")
                }, tx, ct));
            await tx.CommitAsync(ct);
            return Results.Ok(result);
        });
    }

    // New receipts retain the execution scope even for rejected outcomes. Legacy applied
    // receipts can recover their scope from the immutable domain records they created.
    private static JsonObject StoreCommandScope<T>(T value, string kind, Guid trip, Guid line, Guid? destination = null)
    {
        var receipt = JsonSerializer.SerializeToNode(value, ClosureJson)!.AsObject();
        receipt["_scope"] = JsonSerializer.SerializeToNode(new { Kind = kind, Trip = trip, Line = line, Destination = destination }, ClosureJson);
        return receipt;
    }

    private static JsonObject ReadCommandBody(string stored)
    {
        var receipt = JsonNode.Parse(stored)!.AsObject();
        receipt.Remove("_scope");
        return receipt;
    }

    private static string CommandHistorySql(bool successor) => """
        WITH receipts AS (
            SELECT CommandId,TripId,CAST(NULL AS uniqueidentifier) Destination,'FOLLOW_UP_ASSIGNMENT' Kind,
                'APPLIED' Status,SourceKind,SourceId,FollowUpId ResultId,CAST(NULL AS nvarchar(80)) Code,AssignedAt RecordedAt
            FROM dbo.App05FollowUpAssignment WHERE TenantId=@Tenant AND ActorId=@Actor
            UNION ALL
            SELECT CommandId,TripId,NULL,'TRIP_CLOSE','APPLIED',NULL,NULL,ClosureId,NULL,ClosedAt
            FROM dbo.App05TripClosure WHERE TenantId=@Tenant AND ActorId=@Actor
            UNION ALL
            SELECT CommandId,TripId,NULL,'TRIP_CLOSE','REJECTED',NULL,NULL,NULL,Code,NULL
            FROM dbo.App05TripCloseRejection WHERE TenantId=@Tenant AND ActorId=@Actor
        """ + (successor ? """
            UNION ALL
            SELECT c.CommandId,COALESCE(s.TripId,b.OriginTripId,p.TripId),COALESCE(s.Destination,b.DestinationTripId),
                COALESCE(s.Kind,CASE WHEN a.ProposalId IS NOT NULL THEN 'A_CLOSURE_APPROVAL'
                    WHEN p.ProposalId IS NOT NULL THEN 'A_CLOSURE_PROPOSAL'
                    WHEN r.RightId IS NOT NULL THEN 'A_RESERVE_UNHELD'
                    WHEN h.SuccessorRightId IS NOT NULL THEN 'A_TRANSFER_HELD'
                    ELSE 'A_ASSIGN_SUCCESSOR' END),
                CASE WHEN c.Applied=1 THEN 'APPLIED' ELSE 'REJECTED' END,
                'A_REMAINDER',COALESCE(s.LineId,b.OrderLineId,p.OrderLineId),
                COALESCE(a.SourceId,r.RightId,h.SuccessorRightId,p.ProposalId,b.BindingId),
                JSON_VALUE(c.Receipt,'$.code'),c.RecordedAt
            FROM dbo.App05ASuccessorCommand c
            OUTER APPLY OPENJSON(c.Receipt,'$._scope') WITH (
                Kind varchar(32) '$.kind',TripId uniqueidentifier '$.trip',
                Destination uniqueidentifier '$.destination',LineId uniqueidentifier '$.line'
            ) s
            LEFT JOIN dbo.App05ASuccessor b ON b.TenantId=c.TenantId
                AND b.BindingId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(c.Receipt,'$.bindingId'))
            LEFT JOIN dbo.App05AHeldTransfer h ON h.TenantId=c.TenantId
                AND h.SuccessorRightId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(c.Receipt,'$.successorRightId'))
            LEFT JOIN dbo.App05ASuccessorReserve r ON r.TenantId=c.TenantId
                AND r.RightId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(c.Receipt,'$.rightId'))
            LEFT JOIN dbo.App05ARemainderClosureProposal p ON p.TenantId=c.TenantId
                AND p.ProposalId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(c.Receipt,'$.proposalId'))
            LEFT JOIN dbo.App05ARemainderApproval a ON a.TenantId=c.TenantId AND a.ProposalId=p.ProposalId
                AND a.Checker=c.ActorId AND a.CommandId=c.CommandId
            WHERE c.TenantId=@Tenant AND c.ActorId=@Actor AND (s.TripId IS NOT NULL OR c.Applied=1)
            UNION ALL
            SELECT TRY_CONVERT(uniqueidentifier,STUFF(STUFF(STUFF(STUFF(c.CommandId,21,0,'-'),17,0,'-'),13,0,'-'),9,0,'-')),
                scope.LoadTripId,NULL,'A_SUCCESSOR_LOAD',c.Status,NULL,src.Id,src.Id,c.ReasonCode,
                TODATETIMEOFFSET(COALESCE(c.CompletedAtUtc,c.SubmittedAtUtc),'+00:00')
            FROM dbo.BusinessCommand c
            JOIN dbo.C2CommandScope scope ON scope.TenantId=c.TenantId AND scope.CommandRowId=c.Id
            JOIN dbo.BusinessSource src ON src.TenantId=c.TenantId AND src.BusinessCommandId=c.Id AND src.TripId=scope.LoadTripId
            WHERE @Warehouse=1 AND c.TenantId=@Tenant AND c.ActorId COLLATE Latin1_General_100_BIN2=@Actor
                AND scope.RoleCode='warehouse' AND LEN(c.CommandId)=32 AND c.Status='APPLIED' AND EXISTS (
                    SELECT 1 FROM dbo.TripManifestLine m
                    JOIN dbo.App05AExecutionRight r ON r.TenantId=m.TenantId AND r.RightId=m.StockRightId AND r.DestinationTripId=m.TripId
                    WHERE m.TenantId=src.TenantId AND m.LoadBusinessSourceId=src.Id AND m.TripId=scope.LoadTripId)
            UNION ALL
            SELECT e.ClientCommandId,e.TripId,NULL,'A_SUCCESSOR_DELIVERY',c.Status,'A_REMAINDER',r.OrderLineId,src.Id,c.ReasonCode,
                TODATETIMEOFFSET(COALESCE(c.CompletedAtUtc,c.SubmittedAtUtc),'+00:00')
            FROM dbo.C4DeliveryEvidence e
            JOIN dbo.BusinessCommand c ON c.TenantId=e.TenantId AND c.Id=e.Id
            JOIN dbo.C2CommandScope scope ON scope.TenantId=c.TenantId AND scope.CommandRowId=c.Id
                AND scope.DeliveryTripId=e.TripId AND scope.RoleCode='sales'
            JOIN dbo.TripManifestLine m ON m.TenantId=e.TenantId AND m.Id=e.ManifestLineId AND m.TripId=e.TripId
            JOIN dbo.App05AExecutionRight r ON r.TenantId=m.TenantId AND r.RightId=m.StockRightId AND r.DestinationTripId=m.TripId
            LEFT JOIN dbo.BusinessSource src ON src.TenantId=c.TenantId AND src.BusinessCommandId=c.Id AND src.TripId=e.TripId
                AND src.SourceKind='A_DELIVERY'
            WHERE @Sales=1 AND e.TenantId=@Tenant AND e.ActorId=@Actor AND c.ActorId COLLATE Latin1_General_100_BIN2=@Actor
        """ : "") + """
        )
        SELECT r.CommandId,r.TripId,t.Code TripCode,r.Kind,r.Status,r.SourceKind,r.SourceId,a.AOrderId AS OrderId,r.ResultId,r.Code,r.RecordedAt
        FROM receipts r JOIN dbo.Trip t ON t.TenantId=@Tenant AND t.Id=r.TripId
        LEFT JOIN dbo.AConfirmedLineSnapshot a ON a.TenantId=t.TenantId AND a.AOrderLineId=r.SourceId
            AND a.TripId=r.TripId AND r.SourceKind='A_REMAINDER'
        WHERE r.CommandId IS NOT NULL AND (@CommandId IS NULL OR r.CommandId=@CommandId)
            AND (@TripId IS NULL OR r.TripId=@TripId OR r.Destination=@TripId)
            AND ((@Warehouse=1 AND r.Kind='A_RESERVE_UNHELD' AND NOT EXISTS(
                SELECT 1 FROM (VALUES(r.TripId),(r.Destination)) required(TripId)
                WHERE NOT EXISTS(SELECT 1 FROM dbo.C3TripWriter w
                    WHERE w.TenantId=@Tenant AND w.ActorId=@Actor AND w.TripId=required.TripId)))
            OR (@Warehouse=1 AND r.Kind='A_SUCCESSOR_LOAD' AND EXISTS(SELECT 1 FROM dbo.C3TripWriter w
                WHERE w.TenantId=@Tenant AND w.ActorId=@Actor AND w.TripId=r.TripId))
            OR (@Sales=1 AND r.Kind='A_SUCCESSOR_DELIVERY' AND EXISTS(SELECT 1 FROM dbo.C4DeliveryWriter w
                WHERE w.TenantId=@Tenant AND w.ActorId=@Actor AND w.TripId=r.TripId))
            OR (@Operations=1 AND r.Kind NOT IN ('A_RESERVE_UNHELD','A_SUCCESSOR_LOAD','A_SUCCESSOR_DELIVERY') AND NOT EXISTS(
                SELECT 1 FROM (VALUES(r.TripId),(r.Destination)) required(TripId)
                WHERE required.TripId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM dbo.App05TripOperator o
                    WHERE o.TenantId=@Tenant AND o.ActorId=@Actor AND o.TripId=required.TripId AND o.IsActive=1))))
        ORDER BY r.RecordedAt DESC,r.CommandId DESC,r.Kind
        OFFSET @Offset ROWS FETCH NEXT 50 ROWS ONLY;
        """;
}
