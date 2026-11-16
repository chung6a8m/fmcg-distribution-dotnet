using FluentMigrator;

namespace FmcgDms.Api.Persistence;

/// <summary>
/// APP-01C.3 synthetic approval witness for A credit terms.
/// Only nonce-owned LocalDB fixture code may seed these records. There is no
/// public terms-approval or real credit decision endpoint.
/// </summary>
[Migration(202611160007, "APP-01C.3 independently approved A terms witness")]
public sealed class App01c3ATermsApproval : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE dbo.AOrderTermsApproval (
                TenantId nvarchar(64) NOT NULL,
                AOrderId uniqueidentifier NOT NULL,
                TripId uniqueidentifier NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                TermsDays int NOT NULL,
                Decision nvarchar(16) NOT NULL,
                PolicyVersion nvarchar(64) NOT NULL,
                ProposedBy nvarchar(128) NOT NULL,
                CheckedBy nvarchar(128) NULL,
                EvidenceRef nvarchar(128) NULL,
                ApprovedAtUtc datetime2(7) NULL,
                EffectiveFromUtc datetime2(7) NOT NULL,
                EffectiveUntilUtc datetime2(7) NOT NULL,
                RowVersion rowversion NOT NULL,
                CONSTRAINT PK_AOrderTermsApproval PRIMARY KEY (TenantId, AOrderId),
                CONSTRAINT FK_AOrderTermsApproval_ExactOrder FOREIGN KEY
                    (TenantId, AOrderId, TripId, CustomerId)
                    REFERENCES dbo.AOrder (TenantId, Id, TripId, CustomerId),
                CONSTRAINT CK_AOrderTermsApproval_Validity CHECK (
                    TermsDays BETWEEN 1 AND 365
                    AND LEN(LTRIM(RTRIM(PolicyVersion))) > 0
                    AND LEN(LTRIM(RTRIM(ProposedBy))) > 0
                    AND EffectiveFromUtc < EffectiveUntilUtc
                    AND ((Decision=N'PENDING' AND CheckedBy IS NULL
                        AND EvidenceRef IS NULL AND ApprovedAtUtc IS NULL)
                    OR (Decision=N'APPROVED'
                        AND CheckedBy IS NOT NULL AND LEN(LTRIM(RTRIM(CheckedBy)))>0
                        AND ProposedBy<>CheckedBy
                        AND EvidenceRef IS NOT NULL AND LEN(LTRIM(RTRIM(EvidenceRef)))>0
                        AND ApprovedAtUtc IS NOT NULL)))
            );
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_AOrderTermsApproval_ImmutableApproved
            ON dbo.AOrderTermsApproval AFTER UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM deleted WHERE Decision=N'APPROVED')
                    THROW 51072, 'APP01C3 approved A terms evidence is immutable', 1;
            END;
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DROP TRIGGER IF EXISTS dbo.TR_AOrderTermsApproval_ImmutableApproved;
            DROP TABLE dbo.AOrderTermsApproval;
            """);
    }
}
