using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(202611240003, "APP-03.7 Finance queue")]
[FluentMigrator.Tags(App03A1Profile.FinanceName)]
public sealed class App03FinanceSchema : Migration
{
    internal const long Version = 202611240003;

    public override void Up()
    {
        Execute.Sql("""
            ALTER TABLE d4app.AccountingReport ADD FinanceSequence bigint IDENTITY NOT NULL,
                RequestActor nvarchar(128) COLLATE Latin1_General_100_BIN2 NULL;
            """);
        Execute.Sql("""
            CREATE VIEW d4app.FinanceQueue AS
                SELECT s.DeliveryId,s.SourceLineId,s.Seller,s.Channel,s.OccurredAt,s.Quantity,s.Gross,s.Ar,s.Cost,
                    customer.Name AS Customer,sku.Code AS Sku,t.Code AS Trip,
                    r.Id AS ReceiptId,p.Id AS PostingId,latest.ReportId,latest.Status AS ReconciliationStatus
                FROM d4app.CommittedSource s
                JOIN dbo.SaleDelivery d ON d.Id=s.DeliveryId
                JOIN dbo.Trip t ON t.TenantId=d.TenantId AND t.Id=d.TripId
                JOIN dbo.Sku sku ON sku.TenantId=d.TenantId AND sku.Id=d.SkuId
                JOIN dbo.Customer customer ON customer.TenantId=d.TenantId AND customer.Id=d.CustomerId
                LEFT JOIN d4accounting.Receipt r ON r.SourceLineId=s.SourceLineId
                LEFT JOIN d4accounting.Posting p ON p.SourceLineId=s.SourceLineId AND p.Kind='BASE'
                OUTER APPLY (
                    SELECT TOP(1) l.ReportId,l.Status
                    FROM d4app.AccountingReportLine l
                    JOIN d4app.AccountingReport h ON h.Id=l.ReportId
                    WHERE l.SourceLineId=s.SourceLineId
                    ORDER BY h.AsOf DESC,h.FinanceSequence DESC
                ) latest;
            """);
        Execute.Sql("""
            CREATE VIEW d4app.FinanceTransferOutcome AS
                SELECT s.DeliveryId,s.SourceLineId,j.Operation,j.TransportId,j.Outcome,j.RecordedAt,
                    CASE j.Operation WHEN 'RECEIVE' THEN s.ReceiptId ELSE s.PostingId END AS Id
                FROM d4app.FinanceQueue s JOIN d4accounting.Journal j ON j.SourceLineId=s.SourceLineId
                WHERE j.Operation IN ('RECEIVE','POST') AND j.Outcome IN ('COMMITTED','COMMITTED_NO_ACK','REPLAY');
            """);
        Execute.Sql("DENY INSERT,UPDATE,DELETE ON d4app.FinanceQueue TO d4app;");
        Execute.Sql("DENY INSERT,UPDATE,DELETE ON d4app.FinanceTransferOutcome TO d4app;");
    }

    public override void Down() => throw new NotSupportedException("Scratch schemas are removed with the owned database.");
}
