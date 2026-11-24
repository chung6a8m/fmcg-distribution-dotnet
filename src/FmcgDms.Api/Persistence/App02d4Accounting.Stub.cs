using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

internal static partial class App02d4Accounting
{
    private const string ReceiptColumns = "Id,DeliveryId,SourceLineId,PayloadHash,Period,Quantity,Gross,Ar,Cost,ReceivedAt";
    private const string PostingColumns = """
        Id,ReceiptId,SourceLineId,Kind,OriginalId,CorrectionId,ReportId,ApprovalId,Period,
        Quantity,Gross,Ar,Cost,Mapping,Scenario,PostedAt
        """;

    private static async Task<IResult> StoreTransfer(bool post, StubTransfer message, HttpContext http,
        App02d4Runtime runtime, CancellationToken ct)
    {
        await using var db = await runtime.OpenStub(http, Partner, ct);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var receipt = await db.QuerySingleOrDefaultAsync<Receipt>(new CommandDefinition($"""
            SELECT {ReceiptColumns} FROM d4accounting.Receipt WITH (UPDLOCK,HOLDLOCK)
            WHERE SourceLineId=@SourceLineId
            """, message, tx, cancellationToken: ct));

        if (receipt is not null && (!string.Equals(receipt.PayloadHash, message.PayloadHash, StringComparison.Ordinal) ||
            receipt.DeliveryId != message.DeliveryId || receipt.Period != message.Period ||
            receipt.Quantity != message.Quantity || receipt.Gross != message.Gross || receipt.Ar != message.Ar ||
            receipt.Cost != message.Cost))
        {
            await Audit(db, tx, post ? "POST" : "RECEIVE", message.SourceLineId, message.TransportId, "CONFLICT",
                runtime.UtcNow, ct);
            await tx.CommitAsync(ct);
            return Results.Conflict(new Error("PAYLOAD_CONFLICT"));
        }

        if (post && receipt is null)
        {
            await Audit(db, tx, "POST", message.SourceLineId, message.TransportId, "RECEIPT_MISSING", runtime.UtcNow, ct);
            await tx.CommitAsync(ct);
            return Results.Conflict(new Error("RECEIPT_MISSING"));
        }

        DateTimeOffset now = runtime.UtcNow;
        string operation = post ? "POST" : "RECEIVE";
        Guid id;
        bool replay;
        if (!post)
        {
            id = receipt?.Id ?? Guid.NewGuid();
            replay = receipt is not null;
            if (!replay)
                await db.ExecuteAsync(new CommandDefinition("""
                    INSERT d4accounting.Receipt
                        (Id,DeliveryId,SourceLineId,PayloadHash,Period,Quantity,Gross,Ar,Cost,ReceivedAt,TransportId)
                    VALUES (@Id,@DeliveryId,@SourceLineId,@PayloadHash,@Period,@Quantity,@Gross,@Ar,@Cost,@Now,@TransportId)
                    """, new
                {
                    Id = id, message.DeliveryId, message.SourceLineId, message.PayloadHash, message.Period,
                    message.Quantity, message.Gross, message.Ar, message.Cost, Now = now, message.TransportId
                }, tx, cancellationToken: ct));
        }
        else
        {
            string scenario = message.Scenario ?? "normal";
            var posting = await db.QuerySingleOrDefaultAsync<Posting>(new CommandDefinition($"""
                SELECT {PostingColumns} FROM d4accounting.Posting WITH (UPDLOCK,HOLDLOCK)
                WHERE SourceLineId=@SourceLineId AND Kind='BASE'
                """, message, tx, cancellationToken: ct));
            if (posting is not null && posting.Scenario != scenario)
            {
                await Audit(db, tx, operation, message.SourceLineId, message.TransportId, "CONFLICT", now, ct);
                await tx.CommitAsync(ct);
                return Results.Conflict(new Error("POST_PROFILE_CONFLICT"));
            }
            id = posting?.Id ?? Guid.NewGuid();
            replay = posting is not null;
            if (!replay)
                await db.ExecuteAsync(new CommandDefinition("""
                    INSERT d4accounting.Posting
                        (Id,ReceiptId,SourceLineId,Kind,Period,Quantity,Gross,Ar,Cost,Mapping,Scenario,PostedAt)
                    VALUES (@Id,@ReceiptId,@SourceLineId,'BASE',@Period,@Quantity,@Gross,@Ar,@Cost,@Mapping,@Scenario,@Now)
                    """, new
                {
                    Id = id, ReceiptId = receipt!.Id, message.SourceLineId, message.Period,
                    Quantity = scenario == "missing-line" ? (decimal?)null : message.Quantity,
                    Gross = scenario == "missing-line" ? (decimal?)null
                        : message.Gross + (scenario == "wrong-amount" ? 10m : 0m),
                    Ar = scenario == "missing-line" ? (decimal?)null
                        : message.Ar + (scenario == "wrong-amount" ? 10m : 0m),
                    Cost = scenario == "missing-line" ? (decimal?)null : message.Cost,
                    Mapping = scenario == "wrong-mapping" ? "OTHER" : Mapping, Scenario = scenario, Now = now
                }, tx, cancellationToken: ct));
        }

        if (message.Fault == "before-commit")
        {
            await tx.RollbackAsync(ct);
            await Audit(db, null, operation, message.SourceLineId, message.TransportId, "ROLLED_BACK", now, ct);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        string outcome = message.Fault == "after-commit" ? "COMMITTED_NO_ACK" : replay ? "REPLAY" : "COMMITTED";
        await Audit(db, tx, operation, message.SourceLineId, message.TransportId, outcome, now, ct);
        await tx.CommitAsync(ct);
        return message.Fault == "after-commit"
            ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
            : Results.Ok(new Ack(post ? Posted : Received, id, message.SourceLineId));
    }

    private static async Task<IResult> Snapshot(SnapshotRequest request, HttpContext http,
        App02d4Runtime runtime, CancellationToken ct)
    {
        await using var db = await runtime.OpenStub(http, Partner, ct);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        using var rows = await db.QueryMultipleAsync(new CommandDefinition($"""
            SELECT {ReceiptColumns} FROM d4accounting.Receipt WHERE ReceivedAt<=@AsOf;
            SELECT {PostingColumns} FROM d4accounting.Posting WHERE PostedAt<=@AsOf;
            """, request, tx, cancellationToken: ct));
        var receipts = (await rows.ReadAsync<Receipt>()).AsList();
        var postings = (await rows.ReadAsync<Posting>()).AsList();
        await tx.CommitAsync(ct);
        return Results.Ok(new StubSnapshot(receipts, postings));
    }

    private sealed record CorrectionMessage(Guid Id, Guid OriginalId, Guid ReceiptId, Guid ReportId, Guid SourceLineId,
        string Period, decimal Quantity, decimal Gross, decimal Ar, decimal Cost, Guid ApprovalId, string? Fault);

    private static async Task<IResult> StoreCorrection(CorrectionMessage message, HttpContext http,
        App02d4Runtime runtime, CancellationToken ct)
    {
        await using var db = await runtime.OpenStub(http, Partner, ct);
        if (message.Fault is not (null or "after-commit") || (!runtime.UsesFinance && message.Fault is not null))
            return Results.BadRequest(new Error("INVALID_CORRECTION_REQUEST"));
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var original = await db.QuerySingleOrDefaultAsync<Posting>(new CommandDefinition($"""
            SELECT {PostingColumns} FROM d4accounting.Posting WITH (UPDLOCK,HOLDLOCK)
            WHERE Id=@OriginalId AND ReceiptId=@ReceiptId AND SourceLineId=@SourceLineId AND Kind='BASE'
            """, message, tx, cancellationToken: ct));
        if (original is null || original.Period != message.Period)
            return Results.Conflict(new Error("ORIGINAL_POSTING_SCOPE"));
        var posting = await db.QuerySingleOrDefaultAsync<Posting>(new CommandDefinition($"""
            SELECT {PostingColumns} FROM d4accounting.Posting WITH (UPDLOCK,HOLDLOCK)
            WHERE CorrectionId=@Id
            """, message, tx, cancellationToken: ct));
        if (posting is not null && (posting.OriginalId != message.OriginalId || posting.ReportId != message.ReportId
            || posting.ApprovalId != message.ApprovalId || posting.Quantity != message.Quantity ||
            posting.Gross != message.Gross || posting.Ar != message.Ar || posting.Cost != message.Cost))
            return Results.Conflict(new Error("CORRECTION_PAYLOAD_CONFLICT"));
        var id = posting?.Id ?? Guid.NewGuid();
        var now = runtime.UtcNow;
        if (posting is null && runtime.UsesFinance && message.Period != Period(now))
            return Results.Conflict(new Error("PERIOD_LOCKED"));
        if (posting is null)
            await db.ExecuteAsync(new CommandDefinition("""
                INSERT d4accounting.Posting
                    (Id,ReceiptId,SourceLineId,Kind,OriginalId,CorrectionId,ReportId,ApprovalId,
                     Period,Quantity,Gross,Ar,Cost,Mapping,Scenario,PostedAt)
                VALUES (@PostingId,@ReceiptId,@SourceLineId,'CORRECTION',@OriginalId,@Id,@ReportId,@ApprovalId,
                    @Period,@Quantity,@Gross,@Ar,@Cost,@Mapping,N'correction',@Now)
                """, new
            {
                PostingId = id, message.ReceiptId, message.SourceLineId, message.OriginalId,
                message.Id, message.ReportId, message.ApprovalId, message.Period, message.Quantity,
                message.Gross, message.Ar, message.Cost, Mapping, Now = now
            }, tx, cancellationToken: ct));
        await Audit(db, tx, "CORRECTION", message.SourceLineId, message.Id.ToString("D"),
            message.Fault == "after-commit" ? "COMMITTED_NO_ACK" : posting is null ? "COMMITTED" : "REPLAY", now, ct);
        await tx.CommitAsync(ct);
        return message.Fault == "after-commit" ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
            : Results.Ok(new Ack("CORRECTION_POSTED", id, message.SourceLineId));
    }

    private static Task<int> Audit(SqlConnection db, IDbTransaction? tx, string operation, Guid sourceLineId,
        string transportId, string outcome, DateTimeOffset now, CancellationToken ct) =>
        db.ExecuteAsync(new CommandDefinition("""
            INSERT d4accounting.Journal (Operation,SourceLineId,TransportId,Outcome,RecordedAt)
            VALUES (@Operation,@SourceLineId,@TransportId,@Outcome,@Now)
            """, new { Operation = operation, SourceLineId = sourceLineId, TransportId = transportId,
            Outcome = outcome, Now = now }, tx, cancellationToken: ct));
}
