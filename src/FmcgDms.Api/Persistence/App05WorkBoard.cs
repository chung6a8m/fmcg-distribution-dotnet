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
    internal sealed record ReassignWorkInput(Guid CommandId, string? ExpectedAssignmentRevision,
        string? Assignee, string? DueAt, string? Reason);

    private sealed record WorkTrip(Guid TripId, string Code, DateOnly BusinessDate);
    private sealed record WorkBoardRow(Guid TripId, string TripCode, DateOnly BusinessDate, Guid ClosureId,
        string SourceKind, Guid SourceId, Guid? RelatedId, Guid? CaseId, string Label, string Status, bool Overdue,
        Assignment ClosingAssignment, Assignment Assignment, string AssignmentRevision, Assignment[] AssignmentHistory,
        WorkTrip[] SuccessorTrips, ALine? ALine, Guid? ReconciliationReportId, Guid[] CompletionSourceIds, string? CompletionReason,
        bool CanReassign, string[] Assignees);

    private sealed record WorkCompletion(string SourceKind, Guid SourceId, Guid CompletionSourceId, string? Reason);
    private sealed record WorkProof(ALine? ALine, Guid? ReportId, Guid[] SourceIds, string? Reason, bool IsCompleted);

    private static void MapWorkBoard(RouteGroupBuilder routes)
    {
        routes.MapGet("/work", async (string? assignee, string? status, string? dueBefore, HttpContext context,
            TenantConnections connections, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            DateTimeOffset cutoff = default;
            if ((assignee is not null && !TextValid(assignee, 128)) ||
                status is not (null or "OPEN" or "COMPLETED") ||
                (dueBefore is not null && !TryDueAt(dueBefore, out cutoff)))
                return Results.BadRequest(new { Code = "WORK_FILTER_INVALID" });

            return Results.Ok(await Run(connections, context.User, null, false, null, async (db, tx, tenant, actor, ops) =>
            {
                var asOf = await db.ExecuteScalarAsync<DateTimeOffset>(App02a3Sql.Cmd(
                    $"SELECT TODATETIMEOFFSET({App02c6Clock.SqlUtcNow},'+00:00');", null, tx, ct));
                var closures = await db.QueryAsync<ClosureRow>(App02a3Sql.Cmd(ClosureSql + """
                     AND OpenWorkCount>0 AND EXISTS(SELECT 1 FROM dbo.App05TripOperator o
                         WHERE o.TenantId=@Tenant AND o.TripId=App05TripClosure.TripId AND o.ActorId=@Actor AND o.IsActive=1)
                     ORDER BY ClosedAt DESC,TripId;
                    """, new { Tenant = tenant, Actor = actor }, tx, ct));
                var rows = new List<WorkBoardRow>();
                foreach (var closure in closures)
                    rows.AddRange(await ReadWorkBoardTrip(db, tx, tenant, actor, ops, closure,
                        connections.UsesApp05Successor, assignee?.Trim(), status, dueBefore is null ? null : cutoff, ct));
                return new
                {
                    AsOf = asOf, Work = rows.OrderBy(row => row.Assignment.DueAt).ThenBy(row => row.TripId)
                        .ThenBy(row => row.SourceKind, StringComparer.Ordinal).ThenBy(row => row.SourceId)
                };
            }, ct));
        });

        routes.MapPost("/{tripId:guid}/work/{followUpId:guid}/reassign", async (Guid tripId, Guid followUpId,
            ReassignWorkInput input, HttpContext context, TenantConnections connections, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (followUpId == Guid.Empty || input.CommandId == Guid.Empty ||
                input.ExpectedAssignmentRevision is not { Length: 64 } revision || !revision.All(char.IsAsciiHexDigit) ||
                !TextValid(input.Assignee, 128) || !TextValid(input.Reason, 240) || !TryDueAt(input.DueAt, out var dueAt))
                return Results.BadRequest(new { Code = "WORK_REASSIGN_INPUT_INVALID" });
            var normalized = input with
            {
                Assignee = input.Assignee!.Trim(), Reason = input.Reason!.Trim(), ExpectedAssignmentRevision = revision.ToUpperInvariant()
            };
            return await Run(connections, context.User, tripId, true, input.CommandId, (db, tx, tenant, actor, _) =>
                ReassignWork(db, tx, tenant, actor, tripId, followUpId, normalized, dueAt, connections.UsesApp05Successor, ct), ct);
        });
    }

    private static Work[] ClosingWork(ClosureRow closure) =>
        JsonSerializer.Deserialize<Preview>(closure.Snapshot, ClosureJson)!.Work;

    private static async Task<WorkBoardRow[]> ReadWorkBoardTrip(SqlConnection db, SqlTransaction tx, string tenant,
        string actor, bool ops, ClosureRow closure, bool successor, string? assignee, string? status,
        DateTimeOffset? dueBefore, CancellationToken ct)
    {
        var scope = new { Tenant = tenant, Trip = closure.TripId };
        var history = (await db.QueryAsync<Assignment>(App02a3Sql.Cmd("""
            SELECT FollowUpId,SourceKind,SourceId,Assignee,DueAt,NextAction,Reason,CommandId,ActorId,AssignedAt
            FROM dbo.App05FollowUpAssignment WHERE TenantId=@Tenant AND TripId=@Trip ORDER BY EventId;
            """, scope, tx, ct))).ToLookup(row => row.FollowUpId);
        var candidates = ClosingWork(closure)
            .Select(row => (Source: row, History: history[row.FollowUp!.FollowUpId].ToArray()))
            .Where(row => row.History.Length > 0)
            .Where(row => ops || string.Equals(row.History[^1].Assignee, actor, StringComparison.Ordinal))
            .Where(row => assignee is null || string.Equals(row.History[^1].Assignee, assignee, StringComparison.Ordinal))
            .Where(row => dueBefore is null || row.History[^1].DueAt < dueBefore)
            .ToArray();
        if (candidates.Length == 0) return [];

        var preview = await Read(db, tx, tenant, closure.TripId, ops, ct, successor: successor);
        var completions = await ReadWorkCompletions(db, tx, tenant, actor, closure.TripId, successor, ct);
        var rows = new List<WorkBoardRow>(candidates.Length);
        foreach (var (source, events) in candidates)
        {
            var proof = ProveWork(preview, source, completions);
            string workStatus = proof.IsCompleted ? "COMPLETED" : "OPEN";
            if (status is not null && status != workStatus) continue;

            WorkTrip[] trips = [];
            if (successor && source.SourceKind == A)
            {
                var bindings = await ReadSuccessorBindings(db, tx, tenant, actor, source.SourceId, ct, warehouse: false);
                trips = bindings.Where(binding => binding.DestinationVisible).DistinctBy(binding => binding.DestinationTripId)
                    .Select(binding => new WorkTrip(binding.DestinationTripId, binding.Code, DateOnly.FromDateTime(binding.BusinessDate)))
                    .ToArray();
            }
            var current = preview.Work.SingleOrDefault(row => row.SourceKind == source.SourceKind && row.SourceId == source.SourceId);
            var assignment = events[^1];
            rows.Add(new(closure.TripId, preview.Code, preview.BusinessDate, closure.ClosureId, source.SourceKind,
                source.SourceId, source.RelatedId, current?.CaseId, current?.Label ?? source.Label, workStatus,
                !proof.IsCompleted && assignment.DueAt < preview.AsOf, source.FollowUp!, assignment, Convert.ToHexString(Digest(assignment)), events, trips,
                proof.ALine, proof.ReportId, proof.SourceIds, proof.Reason, ops && !proof.IsCompleted, preview.Assignees));
        }
        return rows.ToArray();
    }

    private static WorkProof ProveWork(Preview preview, Work source,
        ILookup<(string SourceKind, Guid SourceId), WorkCompletion> completions)
    {
        var evidence = completions[(source.SourceKind, source.SourceId)].ToArray();
        var line = source.SourceKind == A ? preview.ALines.SingleOrDefault(row => row.OrderLineId == source.SourceId) : null;
        var reportId = source.SourceKind == D4 ? preview.ReconciledReports.GetValueOrDefault(source.SourceId) : Guid.Empty;
        // A missing projection can indicate source drift. A recorded follow-up stays open until its own evidence proves completion.
        bool complete = source.SourceKind switch
        {
            A => line is { Remaining: 0 },
            D4 => reportId != Guid.Empty,
            _ => evidence.Length > 0
        };
        return new(line, reportId == Guid.Empty ? null : reportId,
            complete ? evidence.Select(row => row.CompletionSourceId).ToArray() : [], evidence.FirstOrDefault()?.Reason, complete);
    }

    private static async Task<ILookup<(string SourceKind, Guid SourceId), WorkCompletion>> ReadWorkCompletions(SqlConnection db,
        SqlTransaction tx, string tenant, string actor, Guid trip, bool successor, CancellationToken ct)
    {
        var completions = await db.QueryAsync<WorkCompletion>(App02a3Sql.Cmd("""
            SELECT x.SourceKind,x.SourceId,x.CompletionSourceId,x.Reason
            FROM dbo.SaleDelivery d CROSS APPLY (
                SELECT 'F05_RETURN' AS SourceKind,r.Id AS SourceId,s.SourceId AS CompletionSourceId,
                    CAST(NULL AS varchar(40)) AS Reason
                FROM dbo.F05ReturnCase r JOIN dbo.F05ReturnStage s
                    ON s.TenantId=r.TenantId AND s.CaseId=r.Id AND s.Stage='RELEASE'
                WHERE r.TenantId=d.TenantId AND r.DeliveryId=d.Id
                UNION ALL
                SELECT 'F06_COST',p.Id,a.Id,IIF(a.ProposalId=p.Id,NULL,'F06_ADJUSTED_BY_ANOTHER_PROPOSAL')
                FROM dbo.F06CostProposal p JOIN dbo.F06CostAdjustment a
                    ON a.TenantId=p.TenantId AND a.DeliveryId=p.DeliveryId
                WHERE p.TenantId=d.TenantId AND p.DeliveryId=d.Id
                UNION ALL
                SELECT 'F08_REFUND',r.Id,p.Id,NULL
                FROM dbo.F08ReturnCredit c JOIN dbo.F08Refund r ON r.TenantId=c.TenantId AND r.CreditId=c.Id
                JOIN dbo.F08Disbursement p ON p.TenantId=r.TenantId AND p.RefundId=r.Id
                WHERE c.TenantId=d.TenantId AND c.DeliveryId=d.Id
                UNION ALL
                SELECT 'F08_REFUND',r.Id,paid.Id,'F08_CREDIT_SPENT_BY_OTHER_REFUNDS'
                FROM dbo.F08ReturnCredit c JOIN dbo.F08Refund r ON r.TenantId=c.TenantId AND r.CreditId=c.Id
                JOIN dbo.F08Refund otherRefund ON otherRefund.TenantId=c.TenantId AND otherRefund.CreditId=c.Id AND otherRefund.Id<>r.Id
                JOIN dbo.F08Disbursement paid ON paid.TenantId=otherRefund.TenantId AND paid.RefundId=otherRefund.Id
                WHERE c.TenantId=d.TenantId AND c.DeliveryId=d.Id
                    AND NOT EXISTS(SELECT 1 FROM dbo.F08Disbursement own WHERE own.TenantId=r.TenantId AND own.RefundId=r.Id)
                    AND r.Amount>c.GrossAmount-(SELECT COALESCE(SUM(p.Amount),0)
                        FROM dbo.F08Refund q JOIN dbo.F08Disbursement p ON p.TenantId=q.TenantId AND p.RefundId=q.Id
                        WHERE q.TenantId=c.TenantId AND q.CreditId=c.Id)
            ) x WHERE d.TenantId=@Tenant AND d.TripId=@Trip
            """ + (successor ? """

            UNION ALL
            SELECT 'A_REMAINDER',b.OrderLineId,d.BusinessSourceId,NULL
            FROM dbo.App05ASuccessor b JOIN dbo.SaleDelivery d
                ON d.TenantId=b.TenantId AND d.App05BindingId=b.BindingId AND d.TripId=b.DestinationTripId
            WHERE b.TenantId=@Tenant AND b.OriginTripId=@Trip AND EXISTS(SELECT 1 FROM dbo.App05TripOperator o
                WHERE o.TenantId=b.TenantId AND o.TripId=b.DestinationTripId AND o.ActorId=@Actor AND o.IsActive=1)
            UNION ALL
            SELECT 'A_REMAINDER',p.OrderLineId,a.SourceId,NULL
            FROM dbo.App05ARemainderClosureProposal p JOIN dbo.App05ARemainderApproval a
                ON a.TenantId=p.TenantId AND a.ProposalId=p.ProposalId
            WHERE p.TenantId=@Tenant AND p.TripId=@Trip
            """ : ""), new { Tenant = tenant, Trip = trip, Actor = actor }, tx, ct));
        return completions.OrderBy(row => row.CompletionSourceId)
            .ToLookup(row => (row.SourceKind, row.SourceId));
    }

    private static async Task<IResult> ReassignWork(SqlConnection db, SqlTransaction tx, string tenant, string actor,
        Guid trip, Guid followUpId, ReassignWorkInput request, DateTimeOffset dueAt, bool successor, CancellationToken ct)
    {
        var scope = new { Tenant = tenant, Actor = actor, Trip = trip, request.CommandId };
        byte[] hash = Digest(new { Kind = "REASSIGN_WORK", Trip = trip, FollowUpId = followUpId,
            request.ExpectedAssignmentRevision, request.Assignee, DueAt = dueAt.ToUniversalTime(), request.Reason });
        var replay = await db.QuerySingleOrDefaultAsync<AssignmentReceipt>(App02a3Sql.Cmd("""
            SELECT FollowUpId,PayloadHash FROM dbo.App05FollowUpAssignment
            WHERE TenantId=@Tenant AND ActorId=@Actor AND CommandId=@CommandId;
            """, scope, tx, ct));
        if (replay is not null)
            return CryptographicOperations.FixedTimeEquals(replay.PayloadHash, hash)
                ? Results.Ok(new { replay.FollowUpId }) : Results.Conflict(new { Code = "COMMAND_PAYLOAD_CONFLICT" });
        if (await db.ExecuteScalarAsync<bool>(App02a3Sql.Cmd("""
                SELECT CONVERT(bit,CASE WHEN EXISTS(SELECT 1 FROM dbo.App05TripClosure
                        WHERE TenantId=@Tenant AND ActorId=@Actor AND CommandId=@CommandId)
                    OR EXISTS(SELECT 1 FROM dbo.App05TripCloseRejection
                        WHERE TenantId=@Tenant AND ActorId=@Actor AND CommandId=@CommandId) THEN 1 ELSE 0 END);
                """, scope, tx, ct)) || (successor && await SuccessorCommandUsed(db, tx, tenant, actor, request.CommandId, ct)))
            return Results.Conflict(new { Code = "COMMAND_PAYLOAD_CONFLICT" });

        var closure = await db.QuerySingleOrDefaultAsync<ClosureRow>(App02a3Sql.Cmd(
            ClosureSql + " AND TripId=@Trip AND OpenWorkCount>0;", scope, tx, ct));
        var source = closure is null ? null : ClosingWork(closure).SingleOrDefault(row => row.FollowUp?.FollowUpId == followUpId);
        if (source is null) return Results.NotFound();

        var current = (await db.QueryAsync<Assignment>(App02a3Sql.Cmd(AssignmentsSql, scope, tx, ct)))
            .Single(row => row.FollowUpId == followUpId);
        if (Convert.ToHexString(Digest(current)) != request.ExpectedAssignmentRevision)
            return Results.Conflict(new { Code = "WORK_ASSIGNMENT_CHANGED" });

        if (source.SourceKind == A)
            await LockAOrderLine(db, tx, tenant, source.SourceId, ct);
        var preview = await Read(db, tx, tenant, trip, true, ct, successor: successor);
        var completions = await ReadWorkCompletions(db, tx, tenant, actor, trip, successor, ct);
        if (ProveWork(preview, source, completions).IsCompleted)
            return Results.Conflict(new { Code = "WORK_ALREADY_COMPLETED" });
        if (!preview.Assignees.Contains(request.Assignee!, StringComparer.Ordinal))
            return Results.BadRequest(new { Code = "ASSIGNEE_NOT_IN_SCOPE" });
        if (dueAt <= preview.AsOf)
            return Results.BadRequest(new { Code = "DUE_AT_MUST_FOLLOW_AS_OF" });
        if (string.Equals(current.Assignee, request.Assignee, StringComparison.Ordinal) && current.DueAt == dueAt)
            return Results.BadRequest(new { Code = "WORK_ASSIGNMENT_UNCHANGED" });

        await db.ExecuteAsync(App02a3Sql.Cmd("""
            INSERT dbo.App05FollowUpAssignment(TenantId,TripId,SourceKind,SourceId,FollowUpId,ActorId,CommandId,
                PayloadHash,ExpectedRevision,Assignee,DueAt,NextAction,Reason,AssignedAt)
            VALUES(@Tenant,@Trip,@SourceKind,@SourceId,@FollowUpId,@Actor,@CommandId,@PayloadHash,
                @ExpectedRevision,@Assignee,@DueAt,@NextAction,@Reason,@AssignedAt);
            """, new { scope.Tenant, scope.Trip, scope.Actor, scope.CommandId, source.SourceKind, source.SourceId,
                FollowUpId = followUpId, PayloadHash = hash, ExpectedRevision = request.ExpectedAssignmentRevision,
                request.Assignee, DueAt = dueAt, current.NextAction, request.Reason, AssignedAt = preview.AsOf }, tx, ct));
        return Results.Ok(new { FollowUpId = followUpId });
    }
}
