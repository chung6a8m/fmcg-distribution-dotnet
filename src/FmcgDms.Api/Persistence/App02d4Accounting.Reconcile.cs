using System.Data;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

internal static partial class App02d4Accounting
{
    private const string FinanceCasesLock = "APP-03.7:accounting-cases";

    private sealed record ReconcileRequest(DateTimeOffset From, DateTimeOffset Cutoff, DateTimeOffset AsOf, bool UnknownScope, Guid? RequestId);
    private sealed record Line(Guid ReportId, Guid DeliveryId, Guid SourceLineId, string SourceHash, DateTimeOffset OccurredAt,
        string Period, Guid? ReceiptId, Guid? PostingId, string Status, string? Reason,
        decimal ExpectedQuantity, decimal ExpectedGross, decimal ExpectedAr, decimal ExpectedCost,
        decimal? ActualQuantity, decimal? ActualGross, decimal? ActualAr, decimal? ActualCost,
        decimal? DiffQuantity, decimal? DiffGross, decimal? DiffAr, decimal? DiffCost);
    private sealed record Report(Guid Id, DateTimeOffset FromUtc, DateTimeOffset Cutoff, DateTimeOffset AsOf,
        int? ExpectedCount, int ReceivedCount, int PostedCount, int OkCount, int DiffCount, int PendingCount,
        int OutsideCount, string Status, string? Reason, DateTimeOffset CreatedAt);

    private sealed record StoredReport(DateTimeOffset FromUtc, DateTimeOffset Cutoff, DateTimeOffset AsOf,
        bool UnknownScope, string? RequestActor)
    {
        public bool Matches(ReconcileRequest request, string? actor) =>
            FromUtc == request.From && Cutoff == request.Cutoff && AsOf == request.AsOf &&
            UnknownScope == request.UnknownScope && string.Equals(RequestActor, actor, StringComparison.Ordinal);
    }

    private static async Task<IResult> Reconcile(ReconcileRequest request, HttpContext http, App02d4Runtime runtime,
        CancellationToken ct)
    {
        await using var db = await runtime.Open(http.User, "reconciler", ct);
        DateTimeOffset now = runtime.UtcNow;
        if (request.From >= request.Cutoff || request.AsOf < request.Cutoff || request.AsOf > now)
            return Results.BadRequest(new Error("INVALID_RECONCILIATION_WINDOW"));
        if (request.RequestId == Guid.Empty || (!runtime.UsesFinance && request.RequestId is not null))
            return Results.BadRequest(new Error("INVALID_REPORT_REQUEST_ID"));
        var requestId = request.RequestId;
        var requestActor = Actor(http.User);
        if (requestId is Guid id)
        {
            var saved = await FindStoredReport(db, id, null, ct);
            if (saved is not null)
                return saved.Matches(request, requestActor) ? await ReadReport(db, id, runtime.UsesFinance, ct)
                    : Results.Conflict(new Error("REPORT_REQUEST_CONFLICT"));
        }
        using var client = runtime.Client(http, Partner);
        StubSnapshot? snapshot;
        try
        {
            using var response = await client.PostAsJsonAsync("snapshot", new SnapshotRequest(request.AsOf), ct);
            if (!response.IsSuccessStatusCode) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            snapshot = await response.Content.ReadFromJsonAsync<StubSnapshot>(ct);
        }
        catch (HttpRequestException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        if (snapshot is null) return Results.StatusCode(StatusCodes.Status502BadGateway);

        var sources = await runtime.ReadSources(db, ct, asOf: request.AsOf);
        var scope = sources.Where(source => source.OccurredAt >= request.From && source.OccurredAt < request.Cutoff)
            .ToArray();
        var ids = scope.Select(source => source.SourceLineId).ToHashSet();
        var receipts = snapshot.Receipts.ToDictionary(row => row.SourceLineId);
        var postings = snapshot.Postings.ToLookup(row => row.SourceLineId);
        var reportId = requestId ?? Guid.NewGuid();
        var lines = scope.Select(source => Compare(reportId, source, receipts.GetValueOrDefault(source.SourceLineId),
            postings[source.SourceLineId])).ToArray();
        int received = request.UnknownScope ? snapshot.Receipts.Count : lines.Count(row => row.ReceiptId is not null);
        int posted = request.UnknownScope ? snapshot.Postings.Count(row => row.Kind == "BASE") :
            lines.Count(row => row.PostingId is not null);
        int ok = lines.Count(row => row.Status == Ok);
        int diff = lines.Count(row => row.Status == Diff);
        int pending = lines.Count(row => row.Status == Pending);
        int outside = snapshot.Receipts.Select(row => row.SourceLineId).Concat(snapshot.Postings.Select(row => row.SourceLineId))
            .Distinct().Count(id => !ids.Contains(id));
        bool scopeKnown = !request.UnknownScope;
        bool unexpected = snapshot.Receipts.Any(row => !sources.Any(source => source.SourceLineId == row.SourceLineId));
        string status = !scopeKnown ? Unknown : diff > 0 || unexpected ? Diff : pending > 0 ? Pending : Ok;
        var report = new Report(reportId, request.From.ToUniversalTime(), request.Cutoff.ToUniversalTime(),
            request.AsOf.ToUniversalTime(), scopeKnown ? scope.Length : null, received, posted,
            scopeKnown ? ok : 0, scopeKnown ? diff : 0, scopeKnown ? pending : 0, outside, status,
            !scopeKnown ? "SOURCE_SCOPE_UNAVAILABLE" : unexpected ? "UNEXPECTED_SOURCE" : null, now);
        if (!scopeKnown) lines = [];

        await using var tx = await db.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (runtime.UsesFinance && lines.Any(line => line.Status == Diff && line.PostingId is not null))
            await App02a3Sql.Lock(db, tx, FinanceCasesLock, ct);
        if (requestId is not null)
        {
            var saved = await FindStoredReport(db, reportId, tx, ct);
            if (saved is not null)
            {
                await tx.CommitAsync(ct);
                return saved.Matches(request, requestActor) ? await ReadReport(db, reportId, runtime.UsesFinance, ct)
                    : Results.Conflict(new Error("REPORT_REQUEST_CONFLICT"));
            }
        }
        string requestColumn = requestId is null ? "" : ",RequestActor";
        string requestValue = requestId is null ? "" : ",@RequestActor";
        var parameters = new DynamicParameters(report);
        if (requestId is not null) parameters.Add("RequestActor", requestActor);
        await db.ExecuteAsync(new CommandDefinition($"""
            INSERT d4app.AccountingReport
                (Id,FromUtc,Cutoff,AsOf,ExpectedCount,ReceivedCount,PostedCount,OkCount,DiffCount,PendingCount,
                 OutsideCount,Status,Reason,CreatedAt{requestColumn})
            VALUES (@Id,@FromUtc,@Cutoff,@AsOf,@ExpectedCount,@ReceivedCount,@PostedCount,@OkCount,@DiffCount,
                @PendingCount,@OutsideCount,@Status,@Reason,@CreatedAt{requestValue})
            """, parameters, tx, cancellationToken: ct));
        if (lines.Length > 0)
        {
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT d4app.AccountingReportLine
                    (ReportId,DeliveryId,SourceLineId,SourceHash,OccurredAt,Period,ReceiptId,PostingId,Status,Reason,
                     ExpectedQuantity,ExpectedGross,ExpectedAr,ExpectedCost,ActualQuantity,ActualGross,ActualAr,ActualCost,
                     DiffQuantity,DiffGross,DiffAr,DiffCost)
                VALUES (@ReportId,@DeliveryId,@SourceLineId,@SourceHash,@OccurredAt,@Period,@ReceiptId,@PostingId,@Status,@Reason,
                        @ExpectedQuantity,@ExpectedGross,@ExpectedAr,@ExpectedCost,@ActualQuantity,@ActualGross,@ActualAr,
                        @ActualCost,@DiffQuantity,@DiffGross,@DiffAr,@DiffCost)
                """, lines, tx, cancellationToken: ct));

            var cases = lines.Where(line => line.Status == Diff && line.PostingId is not null)
                .Select(line => new { Id = Guid.NewGuid(), line.ReportId, line.SourceLineId }).ToArray();
            if (cases.Length > 0)
                await db.ExecuteAsync(new CommandDefinition(runtime.UsesFinance ? """
                    INSERT d4app.AccountingCase (Id,ReportId,SourceLineId,OwnerRole,NextAction)
                    SELECT @Id,@ReportId,@SourceLineId,'reconciler','REVIEW_POSTING_DIFFERENCE'
                    FROM d4app.AccountingReportLine currentLine
                    WHERE currentLine.ReportId=@ReportId AND currentLine.SourceLineId=@SourceLineId
                      AND NOT EXISTS (
                        SELECT 1 FROM d4app.AccountingCase existing
                        JOIN d4app.AccountingReportLine earlier ON earlier.ReportId=existing.ReportId
                            AND earlier.SourceLineId=existing.SourceLineId
                        WHERE earlier.PostingId=currentLine.PostingId AND earlier.SourceLineId=currentLine.SourceLineId
                            AND earlier.SourceHash=currentLine.SourceHash AND earlier.Reason=currentLine.Reason)
                    """ : """
                    INSERT d4app.AccountingCase (Id,ReportId,SourceLineId,OwnerRole,NextAction)
                    VALUES (@Id,@ReportId,@SourceLineId,'reconciler','REVIEW_POSTING_DIFFERENCE')
                    """, cases, tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
        return await ReadReport(db, report.Id, runtime.UsesFinance, ct);
    }

    private static Task<StoredReport?> FindStoredReport(SqlConnection db, Guid id, System.Data.Common.DbTransaction? tx,
        CancellationToken ct) => db.QuerySingleOrDefaultAsync<StoredReport>(new CommandDefinition($"""
            SELECT FromUtc,Cutoff,AsOf,CONVERT(bit,IIF(ExpectedCount IS NULL,1,0)) AS UnknownScope,RequestActor
            FROM d4app.AccountingReport {(tx is null ? "" : "WITH (UPDLOCK,HOLDLOCK)")}
            WHERE Id=@id
            """, new { id }, tx, cancellationToken: ct));

    private static Line Compare(Guid reportId, D4Source source, Receipt? receipt, IEnumerable<Posting> entries)
    {
        var posting = entries.SingleOrDefault(row => row.Kind == "BASE");
        string period = Period(source.OccurredAt);
        string status = Pending;
        string? reason = receipt is null ? "RECEIPT_MISSING" : "POSTING_MISSING";
        decimal? quantity = null, gross = null, ar = null, cost = null;
        if (posting is not null)
        {
            status = Diff;
            bool linkageMatches = receipt is not null && posting.ReceiptId == receipt.Id &&
                receipt.DeliveryId == source.DeliveryId && receipt.PayloadHash == PayloadHash(source) &&
                posting.Period == period && receipt.Period == period && posting.Mapping == Mapping;
            if (!linkageMatches)
            {
                reason = "MAPPING_MISMATCH";
            }
            else if (posting.Quantity is null || posting.Gross is null || posting.Ar is null || posting.Cost is null)
            {
                reason = "POSTING_LINE_MISSING";
            }
            else
            {
                var adjustments = entries.Where(row => row.Kind == "CORRECTION" && row.OriginalId == posting.Id).ToArray();
                quantity = posting.Quantity + adjustments.Sum(row => row.Quantity);
                gross = posting.Gross + adjustments.Sum(row => row.Gross);
                ar = posting.Ar + adjustments.Sum(row => row.Ar);
                cost = posting.Cost + adjustments.Sum(row => row.Cost);
                if (quantity == source.Quantity && gross == source.Gross && ar == source.Ar && cost == source.Cost)
                {
                    status = Ok;
                    reason = null;
                }
                else
                {
                    reason = "AMOUNT_MISMATCH";
                }
            }
        }
        return new Line(reportId, source.DeliveryId, source.SourceLineId, source.Hash, source.OccurredAt,
            period, receipt?.Id, posting?.Id, status, reason, source.Quantity, source.Gross, source.Ar, source.Cost,
            quantity, gross, ar, cost, quantity - source.Quantity, gross - source.Gross, ar - source.Ar, cost - source.Cost);
    }

    private static async Task<IResult> GetReport(Guid reportId, HttpContext http, App02d4Runtime runtime,
        CancellationToken ct)
    {
        await using var db = await runtime.Open(http.User, "reconciler", ct);
        return await ReadReport(db, reportId, runtime.UsesFinance, ct);
    }

    private static async Task<IResult> ReadReport(SqlConnection db, Guid reportId, bool finance, CancellationToken ct)
    {
        string canonicalJoin = finance ? """
            LEFT JOIN d4app.FinanceCases canonical ON canonical.PostingId=l.PostingId AND canonical.SourceLineId=l.SourceLineId
                AND canonical.SourceHash=l.SourceHash AND canonical.Reason=l.Reason
            """ : "";
        string caseId = finance ? "COALESCE(c.Id,canonical.Id)" : "c.Id";
        using var reader = await db.QueryMultipleAsync(new CommandDefinition($"""
            SELECT Id,FromUtc,Cutoff,AsOf,ExpectedCount,ReceivedCount,PostedCount,OkCount,DiffCount,PendingCount,
                OutsideCount,Status,Reason,CreatedAt
            FROM d4app.AccountingReport WHERE Id=@ReportId;
            SELECT l.*,{caseId} AS CaseId FROM d4app.AccountingReportLine l
            LEFT JOIN d4app.AccountingCase c ON c.ReportId=l.ReportId AND c.SourceLineId=l.SourceLineId
            {canonicalJoin}
            WHERE l.ReportId=@ReportId ORDER BY l.OccurredAt,l.SourceLineId;
            """, new { ReportId = reportId }, cancellationToken: ct));
        var report = await reader.ReadSingleOrDefaultAsync<Report>();
        if (report is null) return Results.NotFound(new Error("REPORT_NOT_FOUND"));
        var rows = await reader.ReadAsync();
        var lines = rows.Select(row =>
        {
            var values = (IDictionary<string, object>)row;
            return values.ToDictionary(pair => char.ToLowerInvariant(pair.Key[0]) + pair.Key[1..], pair => pair.Value);
        }).ToList();
        return Results.Ok(new { Report = report, Lines = lines });
    }
}
