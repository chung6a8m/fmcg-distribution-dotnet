using FluentMigrator;

namespace FmcgDms.Api.Persistence;

/// <summary>
/// APP-01C.2 synthetic cash-witness boundary. The sale actor cannot create
/// VERIFIED evidence by claiming a boolean in the sale request. A separate
/// trusted (in tests, fixture-seeded) witness must already exist in the same
/// physical tenant database. No verifier/real-money production API is provided.
/// </summary>
[Migration(202611160006, "APP-01C.2 verified cash witness for atomic B posting")]
public sealed class App01c2VerifiedCashEvidence : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE dbo.VerifiedCashEvidence (
                TenantId nvarchar(64) NOT NULL,
                EvidenceRef nvarchar(128) NOT NULL,
                TripId uniqueidentifier NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                ShipToSnapshotId uniqueidentifier NOT NULL,
                PaymentMethod nvarchar(16) NOT NULL,
                VerificationStatus nvarchar(16) NOT NULL,
                VerifiedAmount decimal(19,4) NOT NULL,
                VerifiedBy nvarchar(128) NULL,
                VerifiedAtUtc datetime2(7) NULL,
                RowVersion rowversion NOT NULL,
                CONSTRAINT PK_VerifiedCashEvidence PRIMARY KEY (TenantId, EvidenceRef),
                CONSTRAINT UQ_VerifiedCashEvidence_ReceiptScope UNIQUE
                    (TenantId, EvidenceRef, TripId, CustomerId, VerifiedAmount,
                     PaymentMethod, VerificationStatus),
                CONSTRAINT FK_VerifiedCashEvidence_Trip FOREIGN KEY (TenantId, TripId)
                    REFERENCES dbo.Trip (TenantId, Id),
                CONSTRAINT FK_VerifiedCashEvidence_ShipTo FOREIGN KEY
                    (TenantId, CustomerId, ShipToSnapshotId)
                    REFERENCES dbo.ShipToSnapshot (TenantId, CustomerId, Id),
                CONSTRAINT CK_VerifiedCashEvidence_Validated CHECK (
                    LEN(LTRIM(RTRIM(EvidenceRef))) > 0 AND VerifiedAmount > 0
                    AND PaymentMethod = N'CASH'
                    AND ((VerificationStatus=N'PENDING'
                         AND VerifiedBy IS NULL AND VerifiedAtUtc IS NULL)
                      OR (VerificationStatus=N'VERIFIED'
                         AND VerifiedBy IS NOT NULL
                         AND LEN(LTRIM(RTRIM(VerifiedBy))) > 0
                         AND VerifiedAtUtc IS NOT NULL)))
            );
            -- The receipt cannot consume a forged, pending, different-amount
            -- or different-Trip/customer witness even through direct SQL.
            ALTER TABLE dbo.CashReceipt ADD CONSTRAINT UQ_CashReceipt_VerifiedEvidence
                UNIQUE (TenantId, VerificationEvidenceRef);
            ALTER TABLE dbo.CashReceipt ADD CONSTRAINT FK_CashReceipt_VerifiedWitness
                FOREIGN KEY (TenantId, VerificationEvidenceRef, TripId, CustomerId,
                             ReceivedAmount, PaymentMethod, VerificationStatus)
                REFERENCES dbo.VerifiedCashEvidence
                    (TenantId, EvidenceRef, TripId, CustomerId, VerifiedAmount,
                     PaymentMethod, VerificationStatus);
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_VerifiedCashEvidence_ImmutableVerified
            ON dbo.VerifiedCashEvidence AFTER UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM deleted WHERE VerificationStatus=N'VERIFIED')
                    THROW 51071, 'APP01C2 verified cash witness is immutable', 1;
            END;
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DROP TRIGGER IF EXISTS dbo.TR_VerifiedCashEvidence_ImmutableVerified;
            ALTER TABLE dbo.CashReceipt DROP CONSTRAINT FK_CashReceipt_VerifiedWitness;
            ALTER TABLE dbo.CashReceipt DROP CONSTRAINT UQ_CashReceipt_VerifiedEvidence;
            DROP TABLE dbo.VerifiedCashEvidence;
            """);
    }
}
