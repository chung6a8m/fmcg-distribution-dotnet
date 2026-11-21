using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(202611210002, "Synthetic B pending bank and customer credit lineage")]
public sealed class App01c2LocalPaymentSchema : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE dbo.BLocalPendingBankEvidence (
                TenantId nvarchar(64) NOT NULL,
                EvidenceRef nvarchar(128) NOT NULL,
                TripId uniqueidentifier NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                ShipToSnapshotId uniqueidentifier NOT NULL,
                ReportedAmount decimal(19,4) NOT NULL CHECK (ReportedAmount > 0),
                PRIMARY KEY (TenantId,EvidenceRef),
                FOREIGN KEY (TenantId,TripId) REFERENCES dbo.Trip(TenantId,Id),
                FOREIGN KEY (TenantId,CustomerId,ShipToSnapshotId) REFERENCES dbo.ShipToSnapshot(TenantId,CustomerId,Id)
            );
            CREATE TABLE dbo.BLocalCreditReservation (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                CreditSaleId uniqueidentifier NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                TripId uniqueidentifier NOT NULL,
                ShipToSnapshotId uniqueidentifier NOT NULL,
                Amount decimal(19,4) NOT NULL CHECK (Amount > 0),
                UNIQUE (TenantId,Id),
                FOREIGN KEY (TenantId,TripId) REFERENCES dbo.Trip(TenantId,Id),
                FOREIGN KEY (TenantId,CustomerId,ShipToSnapshotId) REFERENCES dbo.ShipToSnapshot(TenantId,CustomerId,Id)
            );
            CREATE INDEX IX_BLocalCreditReservation_Origin ON dbo.BLocalCreditReservation(TenantId,CreditSaleId);
            CREATE TABLE dbo.BLocalSalePayment (
                TenantId nvarchar(64) NOT NULL,
                SaleDeliveryId uniqueidentifier NOT NULL,
                PendingBankEvidenceRef nvarchar(128) NULL,
                CreditReservationId uniqueidentifier NULL,
                CreditApplied decimal(19,4) NOT NULL,
                CreditIssued decimal(19,4) NOT NULL,
                PRIMARY KEY (TenantId,SaleDeliveryId),
                FOREIGN KEY (TenantId,SaleDeliveryId) REFERENCES dbo.SaleDelivery(TenantId,Id),
                FOREIGN KEY (TenantId,PendingBankEvidenceRef) REFERENCES dbo.BLocalPendingBankEvidence(TenantId,EvidenceRef),
                FOREIGN KEY (TenantId,CreditReservationId) REFERENCES dbo.BLocalCreditReservation(TenantId,Id),
                CHECK (CreditIssued >= 0
                    AND ((CreditReservationId IS NULL AND CreditApplied=0)
                         OR (CreditReservationId IS NOT NULL AND CreditApplied>0))
                    AND (CreditIssued=0 OR CreditApplied=0)
                    AND (PendingBankEvidenceRef IS NULL OR (CreditApplied=0 AND CreditIssued=0))
                    AND (PendingBankEvidenceRef IS NOT NULL OR CreditApplied>0 OR CreditIssued>0))
            );
            ALTER TABLE dbo.BLocalCreditReservation ADD CONSTRAINT FK_BLocalCreditReservation_Origin
                FOREIGN KEY (TenantId,CreditSaleId) REFERENCES dbo.BLocalSalePayment(TenantId,SaleDeliveryId);
            CREATE UNIQUE INDEX UX_BLocalSalePayment_Pending ON dbo.BLocalSalePayment(TenantId,PendingBankEvidenceRef)
                WHERE PendingBankEvidenceRef IS NOT NULL;
            CREATE UNIQUE INDEX UX_BLocalSalePayment_Reservation ON dbo.BLocalSalePayment(TenantId,CreditReservationId)
                WHERE CreditReservationId IS NOT NULL;
            """);
        foreach (string table in new[] { "BLocalPendingBankEvidence", "BLocalCreditReservation", "BLocalSalePayment" })
            Execute.Sql($"""
                CREATE TRIGGER dbo.TR_{table}_Immutable ON dbo.{table} AFTER UPDATE, DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM deleted) THROW 51096, 'Synthetic B payment evidence is immutable', 1;
                END;
                """);
    }

    public override void Down() => Execute.Sql("""
        IF EXISTS (SELECT 1 FROM dbo.BLocalSalePayment)
            THROW 51097, 'Cannot downgrade with recorded B payments', 1;
        ALTER TABLE dbo.BLocalCreditReservation DROP CONSTRAINT FK_BLocalCreditReservation_Origin;
        DROP TABLE dbo.BLocalSalePayment;
        DROP TABLE dbo.BLocalCreditReservation;
        DROP TABLE dbo.BLocalPendingBankEvidence;
        """);
}
