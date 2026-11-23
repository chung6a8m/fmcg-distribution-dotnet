using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(202611230007, "DEV-F06 forward-period source cost corrections")]
[FluentMigrator.Tags(Profile)]
public sealed class App02DevF06Schema : Migration
{
    internal const string Profile = "DevF06";

    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE dbo.F06Period (
                TenantId nvarchar(64) NOT NULL REFERENCES dbo.AppTenantBinding(TenantId),
                MonthStart date NOT NULL CHECK (DAY(MonthStart)=1),
                State varchar(8) NOT NULL CHECK (State IN ('OPEN','LOCKED')),
                CloseReference nvarchar(120) NULL,
                ClosedAtUtc datetime2(7) NULL,
                CONSTRAINT PK_F06Period PRIMARY KEY (TenantId,MonthStart),
                CONSTRAINT CK_F06Period_Close CHECK (
                    (State='OPEN' AND CloseReference IS NULL AND ClosedAtUtc IS NULL)
                    OR (State='LOCKED' AND CloseReference IS NOT NULL AND LEN(CloseReference)>0 AND ClosedAtUtc IS NOT NULL))
            );
            CREATE TABLE dbo.F06CostProposal (
                TenantId nvarchar(64) NOT NULL,
                Id uniqueidentifier NOT NULL,
                DeliveryId uniqueidentifier NOT NULL,
                NewCost bigint NOT NULL CHECK (NewCost>0),
                Reason nvarchar(200) NOT NULL CHECK (LEN(Reason)>0),
                Evidence nvarchar(120) NOT NULL CHECK (LEN(Evidence)>0),
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                PeriodStart date NOT NULL,
                CONSTRAINT PK_F06CostProposal PRIMARY KEY (TenantId,Id),
                CONSTRAINT UQ_F06CostProposal_Evidence UNIQUE (TenantId,Evidence),
                CONSTRAINT FK_F06CostProposal_Source FOREIGN KEY (TenantId,Id) REFERENCES dbo.BusinessSource(TenantId,Id),
                CONSTRAINT FK_F06CostProposal_Delivery FOREIGN KEY (TenantId,DeliveryId) REFERENCES dbo.SaleDelivery(TenantId,Id),
                CONSTRAINT FK_F06CostProposal_Period FOREIGN KEY (TenantId,PeriodStart) REFERENCES dbo.F06Period(TenantId,MonthStart)
            );
            CREATE TABLE dbo.F06CostAdjustment (
                TenantId nvarchar(64) NOT NULL,
                Id uniqueidentifier NOT NULL,
                ProposalId uniqueidentifier NOT NULL,
                DeliveryId uniqueidentifier NOT NULL,
                OriginalCost bigint NOT NULL CHECK (OriginalCost>0),
                AdjustedCost bigint NOT NULL CHECK (AdjustedCost>0),
                PriorReturnedQuantity decimal(18,3) NOT NULL CHECK (PriorReturnedQuantity>=0),
                PriorRefund bigint NOT NULL CHECK (PriorRefund>=0),
                PriorAdjustment bigint NOT NULL,
                PeriodStart date NOT NULL,
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Reason nvarchar(200) NOT NULL CHECK (LEN(Reason)>0),
                Evidence nvarchar(120) NOT NULL CHECK (LEN(Evidence)>0),
                WarehouseId uniqueidentifier NOT NULL,
                CONSTRAINT PK_F06CostAdjustment PRIMARY KEY (TenantId,Id),
                CONSTRAINT UQ_F06CostAdjustment_Delivery UNIQUE (TenantId,DeliveryId),
                CONSTRAINT UQ_F06CostAdjustment_Proposal UNIQUE (TenantId,ProposalId),
                CONSTRAINT UQ_F06CostAdjustment_Evidence UNIQUE (TenantId,Evidence),
                CONSTRAINT FK_F06CostAdjustment_Source FOREIGN KEY (TenantId,Id) REFERENCES dbo.BusinessSource(TenantId,Id),
                CONSTRAINT FK_F06CostAdjustment_Proposal FOREIGN KEY (TenantId,ProposalId) REFERENCES dbo.F06CostProposal(TenantId,Id),
                CONSTRAINT FK_F06CostAdjustment_Delivery FOREIGN KEY (TenantId,DeliveryId) REFERENCES dbo.SaleDelivery(TenantId,Id),
                CONSTRAINT FK_F06CostAdjustment_Warehouse FOREIGN KEY (TenantId,WarehouseId) REFERENCES dbo.StockLocation(TenantId,Id),
                CONSTRAINT FK_F06CostAdjustment_Period FOREIGN KEY (TenantId,PeriodStart) REFERENCES dbo.F06Period(TenantId,MonthStart)
            );
            CREATE TABLE dbo.F06ReturnWarehouseWitness (
                TenantId nvarchar(64) NOT NULL,
                ReturnSourceId uniqueidentifier NOT NULL,
                WarehouseVersion binary(8) NOT NULL,
                CONSTRAINT PK_F06ReturnWitness PRIMARY KEY (TenantId,ReturnSourceId),
                CONSTRAINT FK_F06ReturnWitness_Stage FOREIGN KEY (TenantId,ReturnSourceId)
                    REFERENCES dbo.F05ReturnStage(TenantId,SourceId)
            );
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_F06Period_Lock ON dbo.F06Period AFTER UPDATE, DELETE AS
            BEGIN
                IF EXISTS (SELECT 1 FROM deleted d LEFT JOIN inserted i
                    ON i.TenantId=d.TenantId AND i.MonthStart=d.MonthStart
                    WHERE i.MonthStart IS NULL OR d.State<>'OPEN' OR i.State<>'LOCKED')
                    THROW 51032, 'Only a forward period close is allowed.', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_F06ReturnWarehouseWitness ON dbo.F05ReturnStage AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                INSERT dbo.F06ReturnWarehouseWitness(TenantId,ReturnSourceId,WarehouseVersion)
                SELECT i.TenantId,i.SourceId,b.RowVersion FROM inserted i
                JOIN dbo.F05ReturnCase r ON r.TenantId=i.TenantId AND r.Id=i.CaseId
                JOIN dbo.SaleDelivery d ON d.TenantId=r.TenantId AND d.Id=r.DeliveryId
                JOIN dbo.PhysicalStockBalance b
                    ON b.TenantId=d.TenantId AND b.SkuId=d.SkuId AND b.StockLocationId=i.DestinationId
                WHERE i.Stage='RELEASE';
            END;
            """);
        foreach (var table in new[] { "F06CostProposal", "F06CostAdjustment", "F06ReturnWarehouseWitness" })
            Execute.Sql($"""
                CREATE TRIGGER dbo.TR_{table}_Immutable ON dbo.{table} INSTEAD OF UPDATE, DELETE AS
                    THROW 51033, 'Cost correction source evidence is immutable.', 1;
                """);
    }

    public override void Down() => throw new NotSupportedException("Use the nonce-owned scratch reset.");
}
