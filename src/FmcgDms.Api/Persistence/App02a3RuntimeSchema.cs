using FluentMigrator;

namespace FmcgDms.Api.Persistence;

/// <summary>
/// APP-02A.3: test-only current actor grants and separately made CASE proposals.
/// These are NOT a production identity/authorization administration service.
/// Grants are seeded explicitly by the synthetic test/operator fixture, never
/// inferred from an incoming event or granted by an event endpoint.
/// </summary>
[Migration(202611170002, "APP-02A.3 synthetic current actor grants and case proposals")]
public sealed class App02a3RuntimeSchema : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE dbo.App02ActorRoleGrant (
                TenantId nvarchar(64) NOT NULL,
                ActorId nvarchar(128) NOT NULL,
                RoleCode nvarchar(32) NOT NULL,
                IsActive bit NOT NULL,
                ChangedBy nvarchar(128) NOT NULL,
                ChangedAtUtc datetime2(7) NOT NULL
                    CONSTRAINT DF_App02Grant_Changed DEFAULT(SYSUTCDATETIME()),
                RowVersion rowversion NOT NULL,
                CONSTRAINT PK_App02ActorRoleGrant PRIMARY KEY
                    (TenantId,ActorId,RoleCode),
                CONSTRAINT FK_App02Grant_Tenant FOREIGN KEY (TenantId)
                    REFERENCES dbo.AppTenantBinding(TenantId),
                CONSTRAINT CK_App02Grant_Role CHECK (RoleCode IN
                    (N'sales',N'cashier',N'cashier_supervisor',N'case_checker')),
                CONSTRAINT CK_App02Grant_Actor CHECK (
                    LEN(LTRIM(RTRIM(ActorId)))>0 AND
                    LEN(LTRIM(RTRIM(ChangedBy)))>0)
            );

            CREATE TABLE dbo.App02ActorRoleHistory (
                HistoryId uniqueidentifier NOT NULL
                    CONSTRAINT PK_App02ActorRoleHistory PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                ActorId nvarchar(128) NOT NULL,
                RoleCode nvarchar(32) NOT NULL,
                PreviousActive bit NULL,
                CurrentActive bit NOT NULL,
                ChangedBy nvarchar(128) NOT NULL,
                LoggedAtUtc datetime2(7) NOT NULL,
                CONSTRAINT FK_App02GrantHistory_Tenant FOREIGN KEY(TenantId)
                    REFERENCES dbo.AppTenantBinding(TenantId)
            );

            CREATE TABLE dbo.App02CaseProposal (
                ProposalId uniqueidentifier NOT NULL
                    CONSTRAINT PK_App02CaseProposal PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                CaseId uniqueidentifier NOT NULL,
                MakerActorId nvarchar(128) NOT NULL,
                Decision nvarchar(24) NOT NULL,
                ReasonCode nvarchar(64) NOT NULL,
                EvidenceRef nvarchar(256) NOT NULL,
                ProposedResolution nvarchar(512) NOT NULL,
                ProposedAtUtc datetime2(7) NOT NULL,
                CONSTRAINT UQ_App02CaseProposal_Tenant UNIQUE(TenantId,ProposalId),
                CONSTRAINT FK_App02CaseProposal_Case FOREIGN KEY(TenantId,CaseId)
                    REFERENCES dbo.App02ExceptionCase(TenantId,CaseId),
                CONSTRAINT CK_App02CaseProposal_Decision CHECK (
                    Decision IN(N'HOLD',N'CLOSE',N'APPROVED_RESUME')),
                CONSTRAINT CK_App02CaseProposal_Required CHECK (
                    LEN(LTRIM(RTRIM(MakerActorId)))>0
                    AND LEN(LTRIM(RTRIM(ReasonCode)))>0
                    AND LEN(LTRIM(RTRIM(EvidenceRef)))>0
                    AND LEN(LTRIM(RTRIM(ProposedResolution)))>0)
            );

            CREATE TABLE dbo.App02CaseApprovalLink (
                TenantId nvarchar(64) NOT NULL,
                ProposalId uniqueidentifier NOT NULL,
                DispositionId uniqueidentifier NOT NULL,
                ApprovedAtUtc datetime2(7) NOT NULL,
                CONSTRAINT PK_App02CaseApprovalLink PRIMARY KEY (TenantId,ProposalId),
                CONSTRAINT UQ_App02CaseApprovalLink_Disposition UNIQUE
                    (TenantId,DispositionId),
                CONSTRAINT FK_App02CaseApprovalLink_Proposal FOREIGN KEY(TenantId,ProposalId)
                    REFERENCES dbo.App02CaseProposal(TenantId,ProposalId),
                CONSTRAINT FK_App02CaseApprovalLink_Disposition FOREIGN KEY(TenantId,DispositionId)
                    REFERENCES dbo.App02CaseDisposition(TenantId,DispositionId)
            );
            """);

        Execute.Sql("""
            CREATE TRIGGER dbo.TR_APP02A3_GrantAudit ON dbo.App02ActorRoleGrant
            AFTER INSERT, UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM deleted d WHERE NOT EXISTS
                    (SELECT 1 FROM inserted i WHERE i.TenantId=d.TenantId
                    AND i.ActorId=d.ActorId AND i.RoleCode=d.RoleCode))
                    THROW 51200, 'APP02 grant cannot be removed/rekeyed', 1;
                INSERT dbo.App02ActorRoleHistory(
                    HistoryId,TenantId,ActorId,RoleCode,PreviousActive,
                    CurrentActive,ChangedBy,LoggedAtUtc)
                SELECT NEWID(),i.TenantId,i.ActorId,i.RoleCode,d.IsActive,
                    i.IsActive,i.ChangedBy,SYSUTCDATETIME()
                FROM inserted i LEFT JOIN deleted d ON
                    d.TenantId=i.TenantId AND d.ActorId=i.ActorId AND d.RoleCode=i.RoleCode;
            END;
            """);

        Execute.Sql("""
            CREATE TRIGGER dbo.TR_APP02A3_GrantHistoryImmutable
            ON dbo.App02ActorRoleHistory AFTER UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM deleted)
                    THROW 51201, 'APP02 grant audit is immutable', 1;
            END;
            """);

        Execute.Sql("""
            CREATE TRIGGER dbo.TR_APP02A3_ProposalGuard
            ON dbo.App02CaseProposal AFTER INSERT, UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM deleted)
                    THROW 51202, 'APP02 maker proposal is immutable', 1;
                IF EXISTS (
                    SELECT 1 FROM inserted i
                    JOIN dbo.App02ExceptionCase c ON c.TenantId=i.TenantId
                        AND c.CaseId=i.CaseId
                    JOIN dbo.App02EventProgress p ON p.TenantId=c.TenantId
                        AND p.ReceiptId=c.ReceiptId
                    JOIN dbo.App02EventReceipt r ON r.TenantId=c.TenantId
                        AND r.ReceiptId=c.ReceiptId
                    WHERE p.Status<>N'CASE' OR p.ActiveCaseId<>i.CaseId
                        OR r.ActorId=i.MakerActorId)
                    THROW 51203, 'APP02 maker requires active CASE and independent actor', 1;
            END;
            """);

        Execute.Sql("""
            CREATE TRIGGER dbo.TR_APP02A3_ApprovalLinkImmutable
            ON dbo.App02CaseApprovalLink AFTER UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS(SELECT 1 FROM deleted)
                    THROW 51204, 'APP02 approval link is immutable', 1;
            END;
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DROP TRIGGER IF EXISTS dbo.TR_APP02A3_ApprovalLinkImmutable;
            DROP TRIGGER IF EXISTS dbo.TR_APP02A3_ProposalGuard;
            DROP TRIGGER IF EXISTS dbo.TR_APP02A3_GrantHistoryImmutable;
            DROP TRIGGER IF EXISTS dbo.TR_APP02A3_GrantAudit;
            DROP TABLE dbo.App02CaseApprovalLink;
            DROP TABLE dbo.App02CaseProposal;
            DROP TABLE dbo.App02ActorRoleHistory;
            DROP TABLE dbo.App02ActorRoleGrant;
            """);
    }
}
