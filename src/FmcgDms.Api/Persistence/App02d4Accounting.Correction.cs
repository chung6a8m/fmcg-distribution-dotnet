using System.Data;
using System.Net.Http.Json;
using Dapper;

namespace FmcgDms.Api.Persistence;

internal static partial class App02d4Accounting
{
    private sealed record ProposeRequest(Guid DeliveryId, string? Period);
    private sealed record CloseRequest(Guid ReportId);
    private sealed record CorrectionCandidate(Guid CaseId, Guid OriginalId, Guid ReceiptId, Guid ReportId,
        Guid SourceLineId, string Period, decimal DiffQuantity, decimal DiffGross, decimal DiffAr, decimal DiffCost);
    private sealed record Correction(Guid Id, Guid CaseId, Guid OriginalId, Guid ReceiptId, Guid ReportId, Guid SourceLineId,
        string Period, decimal Quantity, decimal Gross, decimal Ar, decimal Cost, string Maker, string? Checker,
        Guid? ApprovalId, DateTimeOffset? ApprovedAt, Guid? PostingId);
    private sealed record Case(Guid Id, Guid ReportId, Guid SourceLineId, Guid? ClosedReportId);
    private const string CorrectionColumns = """
        Id,CaseId,OriginalId,ReceiptId,ReportId,SourceLineId,Period,Quantity,Gross,Ar,Cost,Maker,
        Checker,ApprovalId,ApprovedAt,PostingId
        """;

    private static async Task<IResult> Propose(Guid reportId, ProposeRequest request, HttpContext http,
        App02d4Runtime runtime, CancellationToken ct)
    {
        await using var db = await runtime.Open(http.User, "maker", ct);
        string? actor = Actor(http.User);
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 128) return Results.Forbid();
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var candidate = await db.QuerySingleOrDefaultAsync<CorrectionCandidate>(new CommandDefinition("""
            SELECT c.Id AS CaseId,l.PostingId AS OriginalId,l.ReceiptId,l.ReportId,l.SourceLineId,l.Period,
                l.DiffQuantity,l.DiffGross,l.DiffAr,l.DiffCost
            FROM d4app.AccountingReportLine l
            JOIN d4app.AccountingCase c ON c.ReportId=l.ReportId AND c.SourceLineId=l.SourceLineId
            WHERE l.ReportId=@ReportId AND l.DeliveryId=@DeliveryId AND l.Reason='AMOUNT_MISMATCH'
                AND l.PostingId IS NOT NULL AND l.ReceiptId IS NOT NULL AND c.ClosedReportId IS NULL
            """, new { ReportId = reportId, request.DeliveryId }, tx, cancellationToken: ct));
        if (candidate is null) return Results.Conflict(new Error("CORRECTION_SCOPE"));
        if (candidate.Period != Period(runtime.UtcNow) || (request.Period is not null && request.Period != candidate.Period))
            return Results.Conflict(new Error("PERIOD_LOCKED"));
        var prior = await db.QuerySingleOrDefaultAsync<Correction>(new CommandDefinition($"""
            SELECT {CorrectionColumns} FROM d4app.AccountingCorrection WITH (UPDLOCK,HOLDLOCK) WHERE OriginalId=@OriginalId
            """, candidate, tx, cancellationToken: ct));
        if (prior is not null && (prior.Maker != actor || prior.CaseId != candidate.CaseId))
            return Results.Conflict(new Error("CORRECTION_ALREADY_EXISTS"));

        Guid id = prior?.Id ?? Guid.NewGuid();
        if (prior is null)
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT d4app.AccountingCorrection
                    (Id,CaseId,OriginalId,ReceiptId,ReportId,SourceLineId,Period,Quantity,Gross,Ar,Cost,Maker)
                VALUES (@Id,@CaseId,@OriginalId,@ReceiptId,@ReportId,@SourceLineId,@Period,@Quantity,@Gross,@Ar,@Cost,@Maker)
                """, new
            {
                Id = id, candidate.CaseId, candidate.OriginalId, candidate.ReceiptId, candidate.ReportId,
                candidate.SourceLineId, candidate.Period, Quantity = -candidate.DiffQuantity, Gross = -candidate.DiffGross,
                Ar = -candidate.DiffAr, Cost = -candidate.DiffCost, Maker = actor
            }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return Results.Ok(new { Id = id, Status = "PROPOSED" });
    }

    private static async Task<IResult> Approve(Guid id, HttpContext http, App02d4Runtime runtime, CancellationToken ct)
    {
        await using var db = await runtime.Open(http.User, "checker", ct);
        string? actor = Actor(http.User);
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 128) return Results.Forbid();
        Correction correction;
        await using (var tx = await db.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            var stored = await db.QuerySingleOrDefaultAsync<Correction>(new CommandDefinition($"""
                SELECT {CorrectionColumns} FROM d4app.AccountingCorrection WITH (UPDLOCK,HOLDLOCK) WHERE Id=@Id
                """, new { Id = id }, tx, cancellationToken: ct));
            if (stored is null) return Results.NotFound(new Error("CORRECTION_NOT_FOUND"));
            if (stored.Maker == actor) return Results.Conflict(new Error("MAKER_CHECKER_MUST_DIFFER"));
            if (stored.Checker is not null && stored.Checker != actor)
                return Results.Conflict(new Error("APPROVAL_ACTOR_CONFLICT"));
            if (stored.PostingId is Guid postingId)
                return Results.Ok(new { Id = id, PostingId = postingId, Status = "CORRECTION_POSTED" });
            if (stored.Period != Period(runtime.UtcNow)) return Results.Conflict(new Error("PERIOD_LOCKED"));
            correction = stored.ApprovalId is not null ? stored : stored with
            {
                Checker = actor, ApprovalId = Guid.NewGuid(), ApprovedAt = runtime.UtcNow
            };
            if (stored.ApprovalId is null)
                await db.ExecuteAsync(new CommandDefinition("""
                    UPDATE d4app.AccountingCorrection SET Checker=@Checker,ApprovalId=@ApprovalId,ApprovedAt=@ApprovedAt
                    WHERE Id=@Id
                    """, correction, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }

        var message = new CorrectionMessage(correction.Id, correction.OriginalId, correction.ReceiptId, correction.ReportId,
            correction.SourceLineId, correction.Period, correction.Quantity, correction.Gross, correction.Ar, correction.Cost,
            correction.ApprovalId!.Value);
        using var client = runtime.Client(http, Partner);
        Ack? ack;
        try
        {
            using var response = await client.PostAsJsonAsync("correction", message, ct);
            if (!response.IsSuccessStatusCode)
                return Results.StatusCode((int)response.StatusCode);
            ack = await response.Content.ReadFromJsonAsync<Ack>(ct);
        }
        catch (HttpRequestException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        if (ack is null || ack.Status != "CORRECTION_POSTED" || ack.SourceLineId != correction.SourceLineId)
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        var updated = await db.ExecuteAsync(new CommandDefinition("""
            UPDATE d4app.AccountingCorrection SET PostingId=@PostingId
            WHERE Id=@Id AND (PostingId IS NULL OR PostingId=@PostingId)
            """, new { Id = id, PostingId = ack.Id }, cancellationToken: ct));
        return updated == 1 ? Results.Ok(new { Id = id, PostingId = ack.Id, ack.Status }) :
            Results.Conflict(new Error("CORRECTION_ACK_CONFLICT"));
    }

    private static async Task<IResult> CloseCase(Guid caseId, CloseRequest request, HttpContext http,
        App02d4Runtime runtime, CancellationToken ct)
    {
        await using var db = await runtime.Open(http.User, "reconciler", ct);
        string? actor = Actor(http.User);
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 128) return Results.Forbid();
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var @case = await db.QuerySingleOrDefaultAsync<Case>(new CommandDefinition("""
            SELECT Id,ReportId,SourceLineId,ClosedReportId
            FROM d4app.AccountingCase WITH (UPDLOCK,HOLDLOCK) WHERE Id=@Id
            """, new { Id = caseId }, tx, cancellationToken: ct));
        if (@case is null) return Results.NotFound(new Error("CASE_NOT_FOUND"));
        if (@case.ClosedReportId is not null)
            return @case.ClosedReportId == request.ReportId ? Results.Ok(new { Id = caseId, Status = "CLOSED" }) :
                Results.Conflict(new Error("CASE_ALREADY_CLOSED"));
        var closure = await db.ExecuteAsync(new CommandDefinition("""
            UPDATE c SET ClosedReportId=@ReportId,ClosedBy=@Actor,ClosedAt=@Now,NextAction='CLOSED_RECONCILED'
            FROM d4app.AccountingCase c
            JOIN d4app.AccountingCorrection fix ON fix.CaseId=c.Id AND fix.PostingId IS NOT NULL
            JOIN d4app.AccountingReport old ON old.Id=c.ReportId
            JOIN d4app.AccountingReport fresh ON fresh.Id=@ReportId AND fresh.FromUtc=old.FromUtc
                AND fresh.Cutoff=old.Cutoff AND fresh.AsOf>old.AsOf AND fresh.AsOf>=fix.ApprovedAt
                AND fresh.Status='RECONCILED_OK'
            JOIN d4app.AccountingReportLine evidence ON evidence.ReportId=fresh.Id AND evidence.SourceLineId=c.SourceLineId
                AND evidence.Status='RECONCILED_OK'
            WHERE c.Id=@Id AND c.ClosedReportId IS NULL
            """, new { Id = caseId, request.ReportId, Actor = actor, Now = runtime.UtcNow }, tx, cancellationToken: ct));
        if (closure != 1) return Results.Conflict(new Error("RECONCILIATION_REQUIRED"));
        await tx.CommitAsync(ct);
        return Results.Ok(new { Id = caseId, Status = "CLOSED" });
    }
}
