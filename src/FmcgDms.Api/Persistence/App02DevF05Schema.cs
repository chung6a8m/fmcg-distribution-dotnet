using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(202611230006, "DEV-F05 sourced physical returns and independent release into stock")]
[FluentMigrator.Tags(Profile)]
public sealed class App02DevF05Schema : Migration
{
    internal const string Profile = "DevF05";

    public override void Up()
    {
        Execute.Sql("ALTER TABLE dbo.C2CommandScope ADD IsF05Return bit NOT NULL CONSTRAINT DF_C2Scope_F05 DEFAULT (0);");
        Execute.Sql("""
            ALTER TABLE dbo.C2CommandScope DROP CONSTRAINT CK_C2CommandScope_D1GrantRole;
            ALTER TABLE dbo.C2CommandScope ADD CONSTRAINT CK_C2CommandScope_D1GrantRole CHECK (
                (IsF05Return=0 AND (
                    (D1GrantTripId IS NULL AND RoleCode IN (N'sales',N'warehouse'))
                    OR (D1GrantTripId IS NOT NULL AND RoleCode IN (N'warehouse',N'finance')
                        AND LoadTripId IS NULL AND DeliveryTripId IS NULL)))
                OR (IsF05Return=1 AND D1GrantTripId IS NULL AND LoadTripId IS NULL AND DeliveryTripId IS NULL
                    AND IsD1Load=0 AND RoleCode IN (N'sales',N'warehouse',N'finance',N'case_checker')));

            CREATE TABLE dbo.F05ReturnCase (
                TenantId nvarchar(64) NOT NULL,
                Id uniqueidentifier NOT NULL,
                DeliveryId uniqueidentifier NOT NULL,
                LotId uniqueidentifier NOT NULL,
                Quantity decimal(18,3) NOT NULL CHECK (Quantity>0),
                RequestedBy nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Reason nvarchar(200) NOT NULL CHECK (LEN(Reason)>0),
                CONSTRAINT PK_F05ReturnCase PRIMARY KEY (TenantId,Id),
                CONSTRAINT FK_F05ReturnCase_Issue FOREIGN KEY (TenantId,DeliveryId,LotId)
                    REFERENCES dbo.F03LotIssue(TenantId,DeliveryId,LotId),
                CONSTRAINT FK_F05ReturnCase_Source FOREIGN KEY (TenantId,Id) REFERENCES dbo.BusinessSource(TenantId,Id)
            );
            CREATE INDEX IX_F05ReturnCase_Delivery ON dbo.F05ReturnCase(TenantId,DeliveryId,LotId);
            CREATE TABLE dbo.F05ReturnStage (
                TenantId nvarchar(64) NOT NULL,
                CaseId uniqueidentifier NOT NULL,
                Stage varchar(12) NOT NULL CHECK (Stage IN ('RECEIVE','APPROVE','RELEASE')),
                SourceId uniqueidentifier NOT NULL,
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Evidence nvarchar(120) NOT NULL CHECK (LEN(Evidence)>0),
                Reason nvarchar(200) NOT NULL CHECK (LEN(Reason)>0),
                AcceptedCost decimal(19,4) NULL CHECK (AcceptedCost>=0),
                DestinationId uniqueidentifier NULL,
                CONSTRAINT PK_F05ReturnStage PRIMARY KEY (TenantId,CaseId,Stage),
                CONSTRAINT UQ_F05ReturnStage_Source UNIQUE (TenantId,SourceId),
                CONSTRAINT UQ_F05ReturnStage_Evidence UNIQUE (TenantId,Stage,Evidence),
                CONSTRAINT FK_F05ReturnStage_Case FOREIGN KEY (TenantId,CaseId) REFERENCES dbo.F05ReturnCase(TenantId,Id),
                CONSTRAINT FK_F05ReturnStage_Source FOREIGN KEY (TenantId,SourceId)
                    REFERENCES dbo.BusinessSource(TenantId,Id),
                CONSTRAINT FK_F05ReturnStage_Destination FOREIGN KEY (TenantId,DestinationId)
                    REFERENCES dbo.StockLocation(TenantId,Id),
                CONSTRAINT CK_F05ReturnStage_State CHECK (
                    (Stage='RECEIVE' AND AcceptedCost IS NULL AND DestinationId IS NOT NULL)
                    OR (Stage='APPROVE' AND AcceptedCost IS NULL AND DestinationId IS NULL)
                    OR (Stage='RELEASE' AND AcceptedCost IS NOT NULL AND DestinationId IS NOT NULL))
            );
            """);
        foreach (var table in new[] { "F05ReturnCase", "F05ReturnStage" })
            Execute.Sql($"""
                CREATE TRIGGER dbo.TR_{table}_Immutable ON dbo.{table} INSTEAD OF UPDATE, DELETE AS
                    THROW 51031, 'Return source evidence is immutable.', 1;
                """);
    }

    public override void Down() => throw new NotSupportedException("Use the nonce-owned scratch reset.");
}
