using FluentMigrator;

namespace FmcgDms.Api.Persistence;

/// <summary>
/// APP-02A.3 nonposting correction annotation. An actual cash/ledger reversal
/// is NOT authorized or inferred by an event; it needs a separate business
/// policy contract. This records immutable maker/checker evidence only.
/// </summary>
[Migration(202611170003, "APP-02A.3 immutable nonposting correction proposal and approval")]
public sealed class App02a3CorrectionSchema : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE dbo.App02CorrectionProposal (
                ProposalId uniqueidentifier NOT NULL
                    CONSTRAINT PK_App02CorrectionProposal PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                OriginalAcceptanceId uniqueidentifier NOT NULL,
                MakerActorId nvarchar(128) NOT NULL,
                ReasonCode nvarchar(64) NOT NULL,
                EvidenceRef nvarchar(256) NOT NULL,
                Note nvarchar(512) NOT NULL,
                ProposedAtUtc datetime2(7) NOT NULL
                    CONSTRAINT DF_App02CorrectionProposal_Proposed DEFAULT (SYSUTCDATETIME()),
                CONSTRAINT UQ_App02CorrectionProposal_Tenant UNIQUE (TenantId,ProposalId),
                CONSTRAINT FK_App02CorrectionProposal_Original FOREIGN KEY
                    (TenantId,OriginalAcceptanceId)
                    REFERENCES dbo.App02AcceptanceApplication (TenantId,AcceptanceId),
                CONSTRAINT CK_App02CorrectionProposal_Required CHECK (
                    LEN(LTRIM(RTRIM(MakerActorId)))>0
                    AND LEN(LTRIM(RTRIM(ReasonCode)))>0
                    AND LEN(LTRIM(RTRIM(EvidenceRef)))>0
                    AND LEN(LTRIM(RTRIM(Note)))>0)
            );

            CREATE TABLE dbo.App02CorrectionApproval (
                TenantId nvarchar(64) NOT NULL,
                ProposalId uniqueidentifier NOT NULL,
                CheckerActorId nvarchar(128) NOT NULL,
                CorrectionSourceId uniqueidentifier NOT NULL,
                ApprovedAtUtc datetime2(7) NOT NULL,
                CONSTRAINT PK_App02CorrectionApproval PRIMARY KEY (TenantId,ProposalId),
                CONSTRAINT UQ_App02CorrectionApproval_Source UNIQUE
                    (TenantId,CorrectionSourceId),
                CONSTRAINT FK_App02CorrectionApproval_Proposal FOREIGN KEY
                    (TenantId,ProposalId)
                    REFERENCES dbo.App02CorrectionProposal(TenantId,ProposalId),
                CONSTRAINT FK_App02CorrectionApproval_Source FOREIGN KEY
                    (TenantId,CorrectionSourceId)
                    REFERENCES dbo.App02CorrectionLink(TenantId,CorrectionSourceId),
                CONSTRAINT CK_App02CorrectionApproval_Checker CHECK (
                    LEN(LTRIM(RTRIM(CheckerActorId)))>0)
            );
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_APP02A3_CorrectionProposalGuard
            ON dbo.App02CorrectionProposal AFTER INSERT, UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM deleted)
                    THROW 51240, 'APP02 correction proposal immutable', 1;
                IF EXISTS (
                    SELECT 1 FROM inserted i
                    JOIN dbo.App02AcceptanceApplication a ON a.TenantId=i.TenantId
                        AND a.AcceptanceId=i.OriginalAcceptanceId
                    JOIN dbo.App02EventReceipt r ON r.TenantId=a.TenantId
                        AND r.ReceiptId=a.CReceiptId
                    JOIN dbo.App02EventProgress p ON p.TenantId=r.TenantId
                        AND p.ReceiptId=r.ReceiptId
                    WHERE p.Status<>N'APPLIED' OR r.ActorId=i.MakerActorId)
                    THROW 51241, 'APP02 correction requires APPLIED and independent maker', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_APP02A3_CorrectionApprovalImmutable
            ON dbo.App02CorrectionApproval AFTER UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM deleted)
                    THROW 51242, 'APP02 correction approval immutable', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_APP02A3_CorrectionAnnotationOnly
            ON dbo.LedgerEffect AFTER INSERT, UPDATE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (
                    SELECT 1 FROM inserted i
                    JOIN dbo.BusinessSourceLine sl ON sl.TenantId=i.TenantId
                        AND sl.Id=i.BusinessSourceLineId
                    JOIN dbo.BusinessSource s ON s.TenantId=sl.TenantId
                        AND s.Id=sl.BusinessSourceId
                    WHERE s.SourceKind=N'CASH_HANDOVER_CORRECTION')
                    THROW 51243, 'APP02 correction annotation must not post economic effects', 1;
            END;
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DROP TRIGGER IF EXISTS dbo.TR_APP02A3_CorrectionAnnotationOnly;
            DROP TRIGGER IF EXISTS dbo.TR_APP02A3_CorrectionApprovalImmutable;
            DROP TRIGGER IF EXISTS dbo.TR_APP02A3_CorrectionProposalGuard;
            DROP TABLE dbo.App02CorrectionApproval;
            DROP TABLE dbo.App02CorrectionProposal;
            """);
    }
}
