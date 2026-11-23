using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(202611230008, "DEV-F07 source-bound gifts, physical returns and waivers")]
[FluentMigrator.Tags(Profile)]
public sealed class App02DevF07Schema : Migration
{
    internal const string Profile = "DevF07";

    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE dbo.F07Program (
                TenantId nvarchar(64) NOT NULL PRIMARY KEY,
                Id uniqueidentifier NOT NULL,
                PurchaseSkuId uniqueidentifier NOT NULL,
                GiftSkuId uniqueidentifier NOT NULL,
                GiftLocationId uniqueidentifier NOT NULL,
                QuarantineLocationId uniqueidentifier NOT NULL,
                PurchaseThreshold decimal(18,3) NOT NULL CHECK (PurchaseThreshold>0),
                GiftsPerThreshold int NOT NULL CHECK (GiftsPerThreshold>0),
                UNIQUE (TenantId,Id),
                FOREIGN KEY (TenantId,PurchaseSkuId) REFERENCES dbo.Sku(TenantId,Id),
                FOREIGN KEY (TenantId,GiftSkuId) REFERENCES dbo.Sku(TenantId,Id),
                FOREIGN KEY (TenantId,GiftLocationId) REFERENCES dbo.StockLocation(TenantId,Id),
                FOREIGN KEY (TenantId,QuarantineLocationId) REFERENCES dbo.StockLocation(TenantId,Id),
                CHECK (PurchaseSkuId<>GiftSkuId AND GiftLocationId<>QuarantineLocationId)
            );
            CREATE TABLE dbo.F07ProgramGrant (
                TenantId nvarchar(64) NOT NULL,
                ProgramId uniqueidentifier NOT NULL,
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                RoleCode varchar(12) NOT NULL CHECK (RoleCode IN ('sales','finance')),
                PRIMARY KEY (TenantId,ProgramId,ActorId,RoleCode),
                FOREIGN KEY (TenantId,ProgramId) REFERENCES dbo.F07Program(TenantId,Id)
            );
            CREATE TABLE dbo.F07Issue (
                TenantId nvarchar(64) NOT NULL,
                Id uniqueidentifier NOT NULL,
                ProgramId uniqueidentifier NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                Quantity int NOT NULL CHECK (Quantity>0),
                CostAmount bigint NOT NULL CHECK (CostAmount>=0),
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Evidence nvarchar(120) NOT NULL CHECK (LEN(Evidence)>0),
                Reason nvarchar(200) NOT NULL CHECK (LEN(Reason)>0),
                PRIMARY KEY (TenantId,Id),
                UNIQUE (TenantId,Evidence),
                FOREIGN KEY (TenantId,Id) REFERENCES dbo.BusinessSource(TenantId,Id),
                FOREIGN KEY (TenantId,ProgramId) REFERENCES dbo.F07Program(TenantId,Id),
                FOREIGN KEY (TenantId,CustomerId) REFERENCES dbo.Customer(TenantId,Id)
            );
            CREATE INDEX IX_F07Issue_Customer ON dbo.F07Issue(TenantId,ProgramId,CustomerId);
            CREATE TABLE dbo.F07GiftReturn (
                TenantId nvarchar(64) NOT NULL,
                Id uniqueidentifier NOT NULL,
                IssueId uniqueidentifier NOT NULL,
                Quantity int NOT NULL CHECK (Quantity>0),
                CostAmount bigint NOT NULL CHECK (CostAmount>=0),
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Evidence nvarchar(120) NOT NULL CHECK (LEN(Evidence)>0),
                Reason nvarchar(200) NOT NULL CHECK (LEN(Reason)>0),
                PRIMARY KEY (TenantId,Id),
                UNIQUE (TenantId,Evidence),
                FOREIGN KEY (TenantId,Id) REFERENCES dbo.BusinessSource(TenantId,Id),
                FOREIGN KEY (TenantId,IssueId) REFERENCES dbo.F07Issue(TenantId,Id)
            );
            CREATE INDEX IX_F07GiftReturn_Issue ON dbo.F07GiftReturn(TenantId,IssueId);
            CREATE TABLE dbo.F07Waiver (
                TenantId nvarchar(64) NOT NULL,
                Id uniqueidentifier NOT NULL,
                IssueId uniqueidentifier NOT NULL,
                Quantity int NOT NULL CHECK (Quantity>0),
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Evidence nvarchar(120) NOT NULL CHECK (LEN(Evidence)>0),
                Reason nvarchar(200) NOT NULL CHECK (LEN(Reason)>0),
                PRIMARY KEY (TenantId,Id),
                UNIQUE (TenantId,Evidence),
                FOREIGN KEY (TenantId,Id) REFERENCES dbo.BusinessSource(TenantId,Id),
                FOREIGN KEY (TenantId,IssueId) REFERENCES dbo.F07Issue(TenantId,Id)
            );
            CREATE INDEX IX_F07Waiver_Issue ON dbo.F07Waiver(TenantId,IssueId);
            CREATE TABLE dbo.F07WaiverDecision (
                TenantId nvarchar(64) NOT NULL,
                Id uniqueidentifier NOT NULL,
                WaiverId uniqueidentifier NOT NULL,
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Evidence nvarchar(120) NOT NULL CHECK (LEN(Evidence)>0),
                Reason nvarchar(200) NOT NULL CHECK (LEN(Reason)>0),
                PRIMARY KEY (TenantId,Id),
                UNIQUE (TenantId,WaiverId),
                UNIQUE (TenantId,Evidence),
                FOREIGN KEY (TenantId,Id) REFERENCES dbo.BusinessSource(TenantId,Id),
                FOREIGN KEY (TenantId,WaiverId) REFERENCES dbo.F07Waiver(TenantId,Id)
            );
            """);
        foreach (string table in new[] { "F07Program", "F07Issue", "F07GiftReturn", "F07Waiver", "F07WaiverDecision" })
            Execute.Sql($"""
                CREATE TRIGGER dbo.TR_{table}_Immutable ON dbo.{table} INSTEAD OF UPDATE, DELETE AS
                    THROW 51033, 'Gift records are immutable.', 1;
                """);
    }

    public override void Down() => throw new NotSupportedException("Use the nonce-owned scratch reset.");
}
