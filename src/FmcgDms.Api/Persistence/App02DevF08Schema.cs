using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(202611230009, "DEV-F08 return credits and evidenced synthetic refunds")]
[FluentMigrator.Tags(Profile)]
public sealed class App02DevF08Schema : Migration
{
    internal const string Profile = "DevF08";

    public override void Up()
    {
        Execute.Sql("""
            ALTER TABLE dbo.C2CommandScope DROP CONSTRAINT CK_C2CommandScope_D1GrantRole;
            ALTER TABLE dbo.C2CommandScope ADD CONSTRAINT CK_C2CommandScope_D1GrantRole CHECK (
                (IsF05Return=0 AND (
                    (D1GrantTripId IS NULL AND RoleCode IN (N'sales',N'warehouse'))
                    OR (D1GrantTripId IS NOT NULL AND RoleCode IN (N'warehouse',N'finance')
                        AND LoadTripId IS NULL AND DeliveryTripId IS NULL)))
                OR (IsF05Return=1 AND D1GrantTripId IS NULL AND LoadTripId IS NULL AND DeliveryTripId IS NULL
                    AND IsD1Load=0 AND RoleCode IN (N'sales',N'warehouse',N'finance',N'case_checker',N'cashier')));

            CREATE TABLE dbo.F08Scope (
                TenantId nvarchar(64) NOT NULL,
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                RoleCode nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL CHECK (RoleCode IN (N'finance',N'cashier',N'case_checker')),
                PRIMARY KEY (TenantId,ActorId,RoleCode),
                FOREIGN KEY (TenantId) REFERENCES dbo.AppTenantBinding(TenantId)
            );
            CREATE TABLE dbo.F08Treasury (
                TenantId nvarchar(64) NOT NULL PRIMARY KEY,
                OpeningAmount bigint NOT NULL CHECK (OpeningAmount>=0),
                AvailableAmount bigint NOT NULL,
                CHECK (AvailableAmount>=0 AND AvailableAmount<=OpeningAmount),
                FOREIGN KEY (TenantId) REFERENCES dbo.AppTenantBinding(TenantId)
            );
            CREATE TABLE dbo.F08PaymentWitness (
                TenantId nvarchar(64) NOT NULL,
                Id uniqueidentifier NOT NULL,
                DeliveryId uniqueidentifier NOT NULL,
                Amount bigint NOT NULL CHECK (Amount>0),
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Evidence nvarchar(120) NOT NULL CHECK (LEN(Evidence)>0),
                Reason nvarchar(200) NOT NULL CHECK (LEN(Reason)>0),
                PRIMARY KEY (TenantId,Id),
                UNIQUE (TenantId,DeliveryId),
                UNIQUE (TenantId,Evidence),
                UNIQUE (TenantId,Id,DeliveryId,Amount),
                FOREIGN KEY (TenantId,Id) REFERENCES dbo.BusinessSource(TenantId,Id),
                FOREIGN KEY (TenantId,DeliveryId) REFERENCES dbo.F04DeliveryPricing(TenantId,DeliveryId)
            );
            CREATE TABLE dbo.F08Settlement (
                TenantId nvarchar(64) NOT NULL,
                Id uniqueidentifier NOT NULL,
                WitnessId uniqueidentifier NOT NULL,
                DeliveryId uniqueidentifier NOT NULL,
                Amount bigint NOT NULL CHECK (Amount>0),
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Evidence nvarchar(120) NOT NULL CHECK (LEN(Evidence)>0),
                Reason nvarchar(200) NOT NULL CHECK (LEN(Reason)>0),
                PRIMARY KEY (TenantId,Id),
                UNIQUE (TenantId,DeliveryId),
                UNIQUE (TenantId,Evidence),
                FOREIGN KEY (TenantId,Id) REFERENCES dbo.BusinessSource(TenantId,Id),
                FOREIGN KEY (TenantId,WitnessId,DeliveryId,Amount)
                    REFERENCES dbo.F08PaymentWitness(TenantId,Id,DeliveryId,Amount)
            );
            ALTER TABLE dbo.F05ReturnCase ADD CONSTRAINT UQ_F05ReturnCase_SourceQty
                UNIQUE(TenantId,Id,DeliveryId,Quantity);
            CREATE TABLE dbo.F08ReturnCredit (
                TenantId nvarchar(64) NOT NULL,
                Id uniqueidentifier NOT NULL,
                CaseId uniqueidentifier NOT NULL,
                ReleaseStage varchar(12) NOT NULL DEFAULT 'RELEASE' CHECK (ReleaseStage='RELEASE'),
                DeliveryId uniqueidentifier NOT NULL,
                Quantity decimal(18,3) NOT NULL CHECK (Quantity>0),
                NetAmount bigint NOT NULL CHECK (NetAmount>=0),
                TaxAmount bigint NOT NULL CHECK (TaxAmount>=0),
                GrossAmount bigint NOT NULL CHECK (GrossAmount>=0),
                DiscountAmount bigint NOT NULL CHECK (DiscountAmount>=0),
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Evidence nvarchar(120) NOT NULL CHECK (LEN(Evidence)>0),
                Reason nvarchar(200) NOT NULL CHECK (LEN(Reason)>0),
                PRIMARY KEY (TenantId,Id),
                UNIQUE (TenantId,CaseId),
                UNIQUE (TenantId,Evidence),
                CHECK (GrossAmount=NetAmount+TaxAmount),
                FOREIGN KEY (TenantId,Id) REFERENCES dbo.BusinessSource(TenantId,Id),
                FOREIGN KEY (TenantId,CaseId,DeliveryId,Quantity) REFERENCES dbo.F05ReturnCase(TenantId,Id,DeliveryId,Quantity),
                FOREIGN KEY (TenantId,CaseId,ReleaseStage) REFERENCES dbo.F05ReturnStage(TenantId,CaseId,Stage),
                FOREIGN KEY (TenantId,DeliveryId) REFERENCES dbo.F04DeliveryPricing(TenantId,DeliveryId)
            );
            CREATE INDEX IX_F08ReturnCredit_Delivery ON dbo.F08ReturnCredit(TenantId,DeliveryId);
            CREATE TABLE dbo.F08Refund (
                TenantId nvarchar(64) NOT NULL,
                Id uniqueidentifier NOT NULL,
                CreditId uniqueidentifier NOT NULL,
                Amount bigint NOT NULL CHECK (Amount>0),
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Evidence nvarchar(120) NOT NULL CHECK (LEN(Evidence)>0),
                Reason nvarchar(200) NOT NULL CHECK (LEN(Reason)>0),
                PRIMARY KEY (TenantId,Id),
                UNIQUE (TenantId,Evidence),
                FOREIGN KEY (TenantId,Id) REFERENCES dbo.BusinessSource(TenantId,Id),
                FOREIGN KEY (TenantId,CreditId) REFERENCES dbo.F08ReturnCredit(TenantId,Id)
            );
            CREATE INDEX IX_F08Refund_Credit ON dbo.F08Refund(TenantId,CreditId);
            CREATE TABLE dbo.F08RefundApproval (
                TenantId nvarchar(64) NOT NULL,
                Id uniqueidentifier NOT NULL,
                RefundId uniqueidentifier NOT NULL,
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Evidence nvarchar(120) NOT NULL CHECK (LEN(Evidence)>0),
                Reason nvarchar(200) NOT NULL CHECK (LEN(Reason)>0),
                PRIMARY KEY (TenantId,Id),
                UNIQUE (TenantId,RefundId),
                UNIQUE (TenantId,Evidence),
                FOREIGN KEY (TenantId,Id) REFERENCES dbo.BusinessSource(TenantId,Id),
                FOREIGN KEY (TenantId,RefundId) REFERENCES dbo.F08Refund(TenantId,Id)
            );
            CREATE TABLE dbo.F08PayoutWitness (
                TenantId nvarchar(64) NOT NULL,
                Id uniqueidentifier NOT NULL,
                RefundId uniqueidentifier NOT NULL,
                Amount bigint NOT NULL CHECK (Amount>0),
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Evidence nvarchar(120) NOT NULL CHECK (LEN(Evidence)>0),
                Reason nvarchar(200) NOT NULL CHECK (LEN(Reason)>0),
                PRIMARY KEY (TenantId,Id),
                UNIQUE (TenantId,RefundId),
                UNIQUE (TenantId,Evidence),
                UNIQUE (TenantId,Id,RefundId,Amount),
                FOREIGN KEY (TenantId,Id) REFERENCES dbo.BusinessSource(TenantId,Id),
                FOREIGN KEY (TenantId,RefundId) REFERENCES dbo.F08RefundApproval(TenantId,RefundId)
            );
            CREATE TABLE dbo.F08Disbursement (
                TenantId nvarchar(64) NOT NULL,
                Id uniqueidentifier NOT NULL,
                WitnessId uniqueidentifier NOT NULL,
                RefundId uniqueidentifier NOT NULL,
                Amount bigint NOT NULL CHECK (Amount>0),
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Evidence nvarchar(120) NOT NULL CHECK (LEN(Evidence)>0),
                Reason nvarchar(200) NOT NULL CHECK (LEN(Reason)>0),
                PRIMARY KEY (TenantId,Id),
                UNIQUE (TenantId,RefundId),
                UNIQUE (TenantId,Evidence),
                FOREIGN KEY (TenantId,Id) REFERENCES dbo.BusinessSource(TenantId,Id),
                FOREIGN KEY (TenantId,WitnessId,RefundId,Amount)
                    REFERENCES dbo.F08PayoutWitness(TenantId,Id,RefundId,Amount)
            );
            """);
        foreach (string table in new[] { "F08PaymentWitness", "F08Settlement", "F08ReturnCredit", "F08Refund", "F08RefundApproval", "F08PayoutWitness", "F08Disbursement" })
            Execute.Sql($"""
                CREATE TRIGGER dbo.TR_{table}_Immutable ON dbo.{table} INSTEAD OF UPDATE, DELETE AS
                    THROW 51034, 'Credit and refund sources are immutable.', 1;
                """);
    }

    public override void Down() => throw new NotSupportedException("Use the nonce-owned scratch reset.");
}
