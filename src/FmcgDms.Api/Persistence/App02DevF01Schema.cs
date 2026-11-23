using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(202611230002, "DEV-F01 source-bound no-order visits")]
[FluentMigrator.Tags(Profile)]
public sealed class App02DevF01Schema : Migration
{
    internal const string Profile = "DevF01";

    public override void Up()
    {
        Execute.Sql("""
        CREATE TABLE dbo.F01CustomerScope (
            TenantId nvarchar(64) NOT NULL,
            CustomerId uniqueidentifier NOT NULL,
            ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            CONSTRAINT PK_F01CustomerScope PRIMARY KEY (TenantId,ActorId,CustomerId),
            CONSTRAINT FK_F01CustomerScope_Customer FOREIGN KEY (TenantId,CustomerId)
                REFERENCES dbo.Customer(TenantId,Id)
        );
        CREATE TABLE dbo.F01Visit (
            Id uniqueidentifier NOT NULL PRIMARY KEY,
            TenantId nvarchar(64) NOT NULL,
            ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
            CommandId uniqueidentifier NOT NULL,
            CustomerId uniqueidentifier NOT NULL,
            OutletId uniqueidentifier NULL,
            Outcome varchar(16) NOT NULL CHECK (Outcome IN ('NO_ORDER','NOT_MET')),
            Reason nvarchar(160) NOT NULL CHECK (LEN(Reason)>0),
            Observation nvarchar(500) NULL CHECK (Observation IS NULL OR LEN(Observation)>0),
            CompletedAtUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
            CONSTRAINT UQ_F01Visit_Command UNIQUE (TenantId,ActorId,CommandId),
            CONSTRAINT FK_F01Visit_Command FOREIGN KEY (TenantId,ActorId,CommandId)
                REFERENCES dbo.C1Command(TenantId,ActorId,CommandId),
            CONSTRAINT FK_F01Visit_Customer FOREIGN KEY (TenantId,CustomerId)
                REFERENCES dbo.Customer(TenantId,Id),
            CONSTRAINT FK_F01Visit_Outlet FOREIGN KEY (TenantId,CustomerId,OutletId)
                REFERENCES dbo.C1Outlet(TenantId,CustomerId,Id)
        );
        CREATE INDEX IX_F01Visit_Customer ON dbo.F01Visit(TenantId,CustomerId,CompletedAtUtc DESC,Id);
        """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_F01Visit_Immutable ON dbo.F01Visit INSTEAD OF UPDATE, DELETE AS
                THROW 51026, 'Completed visits are immutable.', 1;
            """);
    }

    public override void Down() => throw new NotSupportedException("Use the nonce-owned scratch reset.");
}
