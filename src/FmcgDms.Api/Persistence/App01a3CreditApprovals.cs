using FluentMigrator;

namespace FmcgDms.Api.Persistence;

/// <summary>
/// Synthetic-only policy evidence, not a tenant credit configuration UI.
/// No approval row (or no independently checked APPROVED row) grants no authority.
/// </summary>
[Migration(202611160003, "APP-01A.3 synthetic policy approvals and credit grant lineage")]
public sealed class App01a3CreditApprovals : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE dbo.CreditPolicyApproval (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_CreditPolicyApproval PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                TripId uniqueidentifier NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                PoolCode nvarchar(48) NOT NULL,
                CurrencyUnit nvarchar(32) NOT NULL,
                CaseNumber nvarchar(80) NOT NULL,
                PolicyVersion nvarchar(48) NOT NULL,
                ProposedBy nvarchar(128) NOT NULL,
                CheckedBy nvarchar(128) NULL,
                Decision nvarchar(16) NOT NULL,
                Reason nvarchar(256) NOT NULL,
                EvidenceRef nvarchar(128) NULL,
                ApprovedLimit decimal(19,4) NOT NULL,
                EffectiveFromUtc datetime2(7) NOT NULL,
                EffectiveUntilUtc datetime2(7) NOT NULL,
                ReviewedAtUtc datetime2(7) NULL,
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_CreditPolicyApproval_TenantId_Id UNIQUE (TenantId, Id),
                CONSTRAINT UQ_CreditPolicyApproval_Tenant_Scope UNIQUE
                    (TenantId, Id, TripId, CustomerId, PoolCode, CurrencyUnit),
                CONSTRAINT UQ_CreditPolicyApproval_Tenant_Case UNIQUE (TenantId, CaseNumber),
                CONSTRAINT FK_CreditPolicyApproval_Pool FOREIGN KEY
                    (TenantId, TripId, CustomerId, PoolCode, CurrencyUnit)
                    REFERENCES dbo.BCreditPool (TenantId, TripId, CustomerId, PoolCode, CurrencyUnit),
                CONSTRAINT CK_CreditPolicyApproval_Decision CHECK
                    (Decision IN (N'PENDING', N'APPROVED', N'REJECTED')),
                CONSTRAINT CK_CreditPolicyApproval_Scope CHECK (
                    PoolCode = N'B' AND CurrencyUnit = N'SYNTHETIC_UNITLESS'
                    AND ApprovedLimit >= 0 AND EffectiveUntilUtc > EffectiveFromUtc
                    AND LEN(LTRIM(RTRIM(CaseNumber))) > 0
                    AND LEN(LTRIM(RTRIM(PolicyVersion))) > 0
                    AND LEN(LTRIM(RTRIM(ProposedBy))) > 0),
                CONSTRAINT CK_CreditPolicyApproval_Checker CHECK
                    (Decision <> N'APPROVED' OR (
                        CheckedBy IS NOT NULL AND CheckedBy <> ProposedBy
                        AND EvidenceRef IS NOT NULL
                        AND LEN(LTRIM(RTRIM(EvidenceRef))) > 0
                        AND ReviewedAtUtc IS NOT NULL
                        AND LEN(LTRIM(RTRIM(Reason))) > 0))
            );
            CREATE INDEX IX_CreditPolicyApproval_Scope
                ON dbo.CreditPolicyApproval
                (TenantId,TripId,CustomerId,PoolCode,CurrencyUnit,Decision,EffectiveFromUtc);

            -- Nullable for upgrade compatibility. APP-01A.3 command code always writes
            -- a checked approval ID; legacy synthetic grants are audit exceptions.
            ALTER TABLE dbo.BCreditGrant ADD ApprovalId uniqueidentifier NULL;
            ALTER TABLE dbo.BCreditGrant ADD CONSTRAINT FK_BCreditGrant_Approval
                FOREIGN KEY (TenantId, ApprovalId, TripId, CustomerId, PoolCode, CurrencyUnit)
                REFERENCES dbo.CreditPolicyApproval
                    (TenantId, Id, TripId, CustomerId, PoolCode, CurrencyUnit);
            """);
    }

    public override void Down()
    {
        Delete.ForeignKey("FK_BCreditGrant_Approval").OnTable("BCreditGrant").InSchema("dbo");
        Delete.Column("ApprovalId").FromTable("BCreditGrant").InSchema("dbo");
        Delete.Table("CreditPolicyApproval").InSchema("dbo");
    }
}
