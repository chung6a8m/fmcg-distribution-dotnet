using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(202611220001, "APP-02C.1 Customer Outlet and versioned ShipTo")]
public sealed class App02c1Schema : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE dbo.C1Command (
                TenantId nvarchar(64) NOT NULL REFERENCES dbo.AppTenantBinding(TenantId),
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CommandId uniqueidentifier NOT NULL,
                Kind varchar(32) NOT NULL,
                HashVersion tinyint NOT NULL CHECK (HashVersion = 1),
                RequestHash binary(32) NOT NULL,
                HttpStatus smallint NOT NULL CHECK (HttpStatus IN (200,400,404,409)),
                Code varchar(48) NOT NULL,
                ResultId uniqueidentifier NULL,
                Revision int NULL,
                CompletedAtUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
                CONSTRAINT PK_C1Command PRIMARY KEY (TenantId,ActorId,CommandId)
            );

            CREATE TABLE dbo.C1Outlet (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                Name nvarchar(256) NOT NULL CHECK (LEN(Name)>0),
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_C1Outlet_Scope UNIQUE (TenantId,CustomerId,Id),
                CONSTRAINT FK_C1Outlet_Customer FOREIGN KEY (TenantId,CustomerId)
                    REFERENCES dbo.Customer(TenantId,Id)
            );

            CREATE TABLE dbo.C1RegisteredShipTo (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                CONSTRAINT UQ_C1RegisteredShipTo_Scope UNIQUE (TenantId,CustomerId,Id),
                CONSTRAINT FK_C1RegisteredShipTo_Customer FOREIGN KEY (TenantId,CustomerId)
                    REFERENCES dbo.Customer(TenantId,Id)
            );

            CREATE TABLE dbo.C1RegisteredShipToRevision (
                TenantId nvarchar(64) NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                RegisteredId uniqueidentifier NOT NULL,
                Revision int NOT NULL CHECK (Revision>0),
                OutletId uniqueidentifier NULL,
                Payload nvarchar(3000) NOT NULL CHECK (ISJSON(Payload)=1),
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CommandId uniqueidentifier NOT NULL,
                RecordedAtUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
                CONSTRAINT PK_C1RegisteredShipToRevision PRIMARY KEY (TenantId,CustomerId,RegisteredId,Revision),
                CONSTRAINT FK_C1Revision_Registered FOREIGN KEY (TenantId,CustomerId,RegisteredId)
                    REFERENCES dbo.C1RegisteredShipTo(TenantId,CustomerId,Id),
                CONSTRAINT FK_C1Revision_Outlet FOREIGN KEY (TenantId,CustomerId,OutletId)
                    REFERENCES dbo.C1Outlet(TenantId,CustomerId,Id),
                CONSTRAINT FK_C1Revision_Command FOREIGN KEY (TenantId,ActorId,CommandId)
                    REFERENCES dbo.C1Command(TenantId,ActorId,CommandId)
            );

            CREATE TABLE dbo.C1ShipToSelection (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                OutletId uniqueidentifier NULL,
                RegisteredId uniqueidentifier NULL,
                RegisteredRevision int NULL,
                Origin varchar(12) NOT NULL,
                ValidatorVersion tinyint NOT NULL CHECK (ValidatorVersion=1),
                Payload nvarchar(3000) NOT NULL CHECK (ISJSON(Payload)=1),
                PayloadHash binary(32) NOT NULL,
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CommandId uniqueidentifier NOT NULL,
                CapturedAtUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
                CONSTRAINT UQ_C1Selection_Scope UNIQUE (TenantId,CustomerId,Id),
                CONSTRAINT UQ_C1Selection_Command UNIQUE (TenantId,ActorId,CommandId),
                CONSTRAINT FK_C1Selection_Customer FOREIGN KEY (TenantId,CustomerId)
                    REFERENCES dbo.Customer(TenantId,Id),
                CONSTRAINT FK_C1Selection_Outlet FOREIGN KEY (TenantId,CustomerId,OutletId)
                    REFERENCES dbo.C1Outlet(TenantId,CustomerId,Id),
                CONSTRAINT FK_C1Selection_Revision FOREIGN KEY (TenantId,CustomerId,RegisteredId,RegisteredRevision)
                    REFERENCES dbo.C1RegisteredShipToRevision(TenantId,CustomerId,RegisteredId,Revision),
                CONSTRAINT FK_C1Selection_Command FOREIGN KEY (TenantId,ActorId,CommandId)
                    REFERENCES dbo.C1Command(TenantId,ActorId,CommandId),
                CONSTRAINT CK_C1Selection_Origin CHECK (
                    (Origin='DIRECT' AND RegisteredId IS NULL AND RegisteredRevision IS NULL)
                    OR (Origin='REGISTERED' AND RegisteredId IS NOT NULL AND RegisteredRevision IS NOT NULL))
            );
            CREATE INDEX IX_C1Selection_Customer ON dbo.C1ShipToSelection (TenantId,CustomerId,CapturedAtUtc);
            """);

        // Existing APP-01 references keep their IDs and columns. C.1 selections
        // live separately so legacy posting cannot use a C.1 selection as an order snapshot.
        foreach (string table in new[] { "ShipToSnapshot", "C1Command", "C1RegisteredShipToRevision", "C1ShipToSelection" })
            Execute.Sql($"""
                CREATE TRIGGER dbo.TR_{table}_Immutable ON dbo.{table}
                INSTEAD OF UPDATE, DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51021, 'ShipTo or command history is immutable.', 1;
                END;
                """);
    }

    public override void Down() => throw new NotSupportedException("Forward-only application migration.");
}
