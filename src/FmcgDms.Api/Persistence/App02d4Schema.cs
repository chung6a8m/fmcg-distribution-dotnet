using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(202611230001, "APP-02D.4 isolated synthetic invoice and accounting rehearsal")]
public sealed class App02d4Schema : Migration
{
    public override void Up()
    {
        foreach (string schema in new[] { "d4app", "d4invoice", "d4accounting" })
            Execute.Sql($"CREATE SCHEMA {schema} AUTHORIZATION dbo;");
        Execute.Sql("""
            CREATE TABLE d4app.ActorRole (
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                RoleCode nvarchar(32) NOT NULL CHECK (RoleCode IN
                    ('invoice','invoice-observer','accountant','reconciler','maker','checker')),
                IsActive bit NOT NULL,
                PRIMARY KEY (ActorId, RoleCode)
            );
            CREATE TABLE d4app.TripProfile (
                TripId uniqueidentifier NOT NULL PRIMARY KEY REFERENCES dbo.Trip(Id)
            );
            """);
        Execute.Sql("""
            CREATE VIEW d4app.CommittedSource AS
                SELECT d.Id AS DeliveryId, d.BusinessSourceLineId AS SourceLineId,
                    CASE d.RightKind WHEN 'A' THEN 'D4-A' ELSE 'D4-B' END AS Seller,
                    'MOCK-V1' AS Channel, TODATETIMEOFFSET(bs.OccurredAtUtc,'+00:00') AS OccurredAt,
                    TODATETIMEOFFSET(bs.RecordedAtUtc,'+00:00') AS RecordedAt,
                    d.BaseQuantity AS Quantity,d.PayableAmount AS Gross,
                    CAST(d.PayableAmount - COALESCE(cr.ReceivedAmount,0) AS decimal(19,4)) AS Ar,
                    d.CostAmount AS Cost,
                    CONVERT(varchar(64), HASHBYTES('SHA2_256', CONCAT(
                        'D4-v0.1|',d.TenantId,'|',d.Id,'|',d.BusinessSourceLineId,'|',d.ShipToSnapshotId,
                        '|',d.C4EvidenceId,'|',d.D2SelectionId,'|',d.RightKind,'|',d.SkuId,'|',
                        CONVERT(varchar(33),bs.OccurredAtUtc,126),'|',d.BaseQuantity,'|',
                        d.PayableAmount,'|',COALESCE(cr.ReceivedAmount,0),'|',d.CostAmount)),2) AS Hash
                FROM dbo.SaleDelivery d
                JOIN d4app.TripProfile p ON p.TripId=d.TripId
                JOIN dbo.BusinessSource bs ON bs.TenantId=d.TenantId AND bs.Id=d.BusinessSourceId
                JOIN dbo.DeliveryStockIssue si ON si.TenantId=d.TenantId AND si.SaleDeliveryId=d.Id
                LEFT JOIN dbo.CashReceipt cr ON cr.TenantId=d.TenantId AND cr.SaleDeliveryId=d.Id
                WHERE d.InvoiceEligible=0 AND (d.C4EvidenceId IS NOT NULL OR d.D2SelectionId IS NOT NULL);
            """);
        Execute.Sql(App02d4Invoice.Schema);
        Execute.Sql(App02d4Accounting.Schema);
        Execute.Sql("""
            CREATE USER d4app WITHOUT LOGIN;
            CREATE USER d4invoice WITHOUT LOGIN;
            CREATE USER d4accounting WITHOUT LOGIN;
            CREATE USER d4oracle WITHOUT LOGIN;
            GRANT SELECT,INSERT ON SCHEMA::d4app TO d4app;
            GRANT UPDATE ON d4app.InvoiceIntent(Status,FactId,FactCommittedAt) TO d4app;
            GRANT UPDATE ON d4app.InvoiceAttempt(Result,FinishedAt,ObservedStatus,ObservedIntentId,ObservedHash,ObservedChannel) TO d4app;
            GRANT UPDATE ON d4app.AccountingCase(ClosedReportId,ClosedBy,ClosedAt,NextAction) TO d4app;
            GRANT UPDATE ON d4app.AccountingCorrection(Checker,ApprovalId,ApprovedAt,PostingId) TO d4app;
            DENY INSERT,UPDATE,DELETE ON d4app.ActorRole TO d4app;
            DENY INSERT,UPDATE,DELETE ON d4app.InvoiceProfile TO d4app;
            DENY UPDATE,DELETE ON d4app.AccountingReport TO d4app;
            DENY UPDATE,DELETE ON d4app.AccountingReportLine TO d4app;
            DENY INSERT,UPDATE,DELETE ON d4app.TripProfile TO d4app;
            DENY INSERT,UPDATE,DELETE ON d4app.CommittedSource TO d4app;
            GRANT SELECT,INSERT ON SCHEMA::d4invoice TO d4invoice;
            GRANT SELECT,INSERT ON SCHEMA::d4accounting TO d4accounting;
            GRANT SELECT ON SCHEMA::dbo TO d4oracle;
            GRANT SELECT ON SCHEMA::d4app TO d4oracle;
            GRANT SELECT ON SCHEMA::d4invoice TO d4oracle;
            GRANT SELECT ON SCHEMA::d4accounting TO d4oracle;
            """);
    }

    public override void Down() => throw new NotSupportedException("D.4 uses only disposable nonce-owned LocalDB.");
}
