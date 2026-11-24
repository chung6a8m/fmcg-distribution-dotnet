using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(202611240004, "APP-03.7 Finance reconciliation provenance and recovery")]
[FluentMigrator.Tags(App03A1Profile.FinanceName)]
public sealed class App03FinanceReviewSchema : Migration
{
    internal const long Version = 202611240004;

    public override void Up()
    {
        Execute.Sql("""
            CREATE VIEW d4app.FinanceQueueProvenance AS
                SELECT q.*,h.FromUtc AS ReportFromUtc,h.Cutoff AS ReportCutoff,h.AsOf AS ReportAsOf,
                    CONVERT(bit,CASE WHEN h.Id IS NOT NULL AND (
                        ISNULL(l.ReceiptId,'00000000-0000-0000-0000-000000000000')<>
                            ISNULL(q.ReceiptId,'00000000-0000-0000-0000-000000000000')
                        OR ISNULL(l.PostingId,'00000000-0000-0000-0000-000000000000')<>
                            ISNULL(q.PostingId,'00000000-0000-0000-0000-000000000000')
                        OR EXISTS (SELECT 1 FROM d4accounting.Posting p WHERE p.OriginalId=q.PostingId
                            AND (p.PostedAt>h.AsOf OR (p.PostedAt=h.AsOf AND l.Reason='AMOUNT_MISMATCH')))
                    ) THEN 1 ELSE 0 END) AS ReconciliationStale
                FROM d4app.FinanceQueue q
                LEFT JOIN d4app.AccountingReport h ON h.Id=q.ReportId
                LEFT JOIN d4app.AccountingReportLine l ON l.ReportId=q.ReportId AND l.SourceLineId=q.SourceLineId;
            """);
        Execute.Sql("""
            CREATE VIEW d4app.FinanceCorrectionPosting AS
                SELECT c.Id AS CorrectionId,p.Id AS PostingId,p.PostedAt
                FROM d4app.AccountingCorrection c
                JOIN d4accounting.Posting p ON p.CorrectionId=c.Id AND p.Kind='CORRECTION'
                    AND p.OriginalId=c.OriginalId AND p.ReceiptId=c.ReceiptId AND p.ReportId=c.ReportId
                    AND p.ApprovalId=c.ApprovalId AND p.SourceLineId=c.SourceLineId AND p.Period=c.Period
                    AND p.Quantity=c.Quantity AND p.Gross=c.Gross AND p.Ar=c.Ar AND p.Cost=c.Cost
                    AND p.Mapping='DELIVERY' AND p.PostedAt>=c.ApprovedAt;
            """);
        Execute.Sql("""
            CREATE VIEW d4app.FinanceCases AS
                WITH Ranked AS (
                    SELECT c.Id,c.ReportId,c.SourceLineId,c.ClosedReportId,c.ClosedBy,
                        l.DeliveryId,l.PostingId,l.SourceHash,l.Reason,l.OccurredAt,
                        ROW_NUMBER() OVER (PARTITION BY l.PostingId,l.SourceLineId,l.SourceHash,l.Reason
                            ORDER BY CASE WHEN fix.Id IS NOT NULL THEN 0 ELSE 1 END,h.FinanceSequence) AS Position
                    FROM d4app.AccountingCase c
                    JOIN d4app.AccountingReportLine l ON l.ReportId=c.ReportId AND l.SourceLineId=c.SourceLineId
                    JOIN d4app.AccountingReport h ON h.Id=c.ReportId
                    LEFT JOIN d4app.AccountingCorrection fix ON fix.CaseId=c.Id
                )
                SELECT Id,ReportId,SourceLineId,ClosedReportId,ClosedBy,DeliveryId,PostingId,SourceHash,Reason,OccurredAt
                FROM Ranked WHERE Position=1;
            """);
        Execute.Sql("DENY INSERT,UPDATE,DELETE ON d4app.FinanceCases TO d4app;");
        Execute.Sql("DENY INSERT,UPDATE,DELETE ON d4app.FinanceQueueProvenance TO d4app;");
        Execute.Sql("DENY INSERT,UPDATE,DELETE ON d4app.FinanceCorrectionPosting TO d4app;");
    }

    public override void Down() => throw new NotSupportedException("Scratch schemas are removed with the owned database.");
}
