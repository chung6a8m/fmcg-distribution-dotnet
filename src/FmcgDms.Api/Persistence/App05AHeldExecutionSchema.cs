using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(Version, "APP-05.4b post transferred HELD on its successor")]
[FluentMigrator.Tags(App03A1Profile.SuccessorName)]
public sealed class App05AHeldExecutionSchema : Migration
{
    internal const long Version = 202611260002;

    public override void Up()
    {
        Execute.Sql("""
            ALTER TABLE dbo.App05AHeldTransfer ADD CONSTRAINT UQ_App05AHeldTransfer_Execution
                UNIQUE(TenantId,SuccessorRightId,BindingId,OrderLineId,OriginalRightId,OriginTripId,WarehouseId,SkuId);
            CREATE TABLE dbo.App05AHeldExecution (
                TenantId nvarchar(64) NOT NULL,
                RightId uniqueidentifier NOT NULL,
                BindingId uniqueidentifier NOT NULL,
                OrderLineId uniqueidentifier NOT NULL,
                OriginalRightId uniqueidentifier NOT NULL,
                OriginTripId uniqueidentifier NOT NULL,
                DestinationTripId uniqueidentifier NOT NULL,
                WarehouseId uniqueidentifier NOT NULL,
                SkuId uniqueidentifier NOT NULL,
                RightKind char(1) NOT NULL CHECK(RightKind='A'),
                PRIMARY KEY(TenantId,RightId),
                UNIQUE(TenantId,RightId,BindingId,DestinationTripId,OrderLineId,SkuId),
                FOREIGN KEY(TenantId,RightId,BindingId,OrderLineId,OriginalRightId,OriginTripId,WarehouseId,SkuId)
                    REFERENCES dbo.App05AHeldTransfer(TenantId,SuccessorRightId,BindingId,OrderLineId,OriginalRightId,OriginTripId,WarehouseId,SkuId),
                FOREIGN KEY(TenantId,BindingId,OriginTripId,DestinationTripId,OrderLineId,SkuId)
                    REFERENCES dbo.App05ASuccessor(TenantId,BindingId,OriginTripId,DestinationTripId,OrderLineId,SkuId),
                FOREIGN KEY(TenantId,RightId,DestinationTripId,WarehouseId,SkuId,RightKind)
                    REFERENCES dbo.StockRight(TenantId,Id,TripId,StockLocationId,SkuId,RightKind),
                FOREIGN KEY(TenantId,RightId,OrderLineId) REFERENCES dbo.AReservation(TenantId,StockRightId,AOrderLineId)
            );
            CREATE INDEX IX_App05AHeldExecution_Origin ON dbo.App05AHeldExecution(TenantId,OriginalRightId);
            ALTER TABLE dbo.SaleDelivery ADD App05HeldExecutorId uniqueidentifier NULL;
            """);
        Execute.Sql("""
            ALTER TABLE dbo.SaleDelivery ADD App05ReserveBindingId AS
                (CASE WHEN App05HeldExecutorId IS NULL THEN App05BindingId END) PERSISTED;
            """);
        Execute.Sql("""
            ALTER TABLE dbo.SaleDelivery DROP CONSTRAINT FK_SaleDelivery_App05Reserve;
            ALTER TABLE dbo.SaleDelivery ADD CONSTRAINT FK_SaleDelivery_App05Reserve
                FOREIGN KEY(TenantId,StockRightId,App05ReserveBindingId,TripId,AOrderLineId,SkuId)
                REFERENCES dbo.App05ASuccessorReserve(TenantId,RightId,BindingId,DestinationTripId,OrderLineId,SkuId);
            ALTER TABLE dbo.SaleDelivery ADD CONSTRAINT CK_SaleDelivery_App05Held
                CHECK(App05HeldExecutorId IS NULL OR (App05HeldExecutorId=StockRightId AND App05BindingId IS NOT NULL));
            ALTER TABLE dbo.SaleDelivery ADD CONSTRAINT FK_SaleDelivery_App05Held
                FOREIGN KEY(TenantId,App05HeldExecutorId,App05BindingId,TripId,AOrderLineId,SkuId)
                REFERENCES dbo.App05AHeldExecution(TenantId,RightId,BindingId,DestinationTripId,OrderLineId,SkuId);
            """);
        Execute.Sql("""
            CREATE OR ALTER VIEW dbo.App05AExecutionRight AS
            SELECT r.TenantId,r.RightId,r.BindingId,b.OriginTripId,r.DestinationTripId,r.OrderLineId,b.ConfirmSourceId,r.WarehouseId,r.SkuId,
                CAST(NULL AS uniqueidentifier) AS HeldExecutorId
            FROM dbo.App05ASuccessorReserve r
            JOIN dbo.App05ASuccessor b ON b.TenantId=r.TenantId AND b.BindingId=r.BindingId
            UNION ALL
            SELECT r.TenantId,r.RightId,r.BindingId,b.OriginTripId,r.DestinationTripId,r.OrderLineId,b.ConfirmSourceId,r.WarehouseId,r.SkuId,r.RightId
            FROM dbo.App05AHeldExecution r
            JOIN dbo.App05ASuccessor b ON b.TenantId=r.TenantId AND b.BindingId=r.BindingId;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_App05AHeldExecution_Immutable ON dbo.App05AHeldExecution INSTEAD OF UPDATE, DELETE AS
            BEGIN
                THROW 51931, 'APP-05 successor execution provenance is immutable.', 1;
            END;
            """);
    }

    public override void Down() => throw new NotSupportedException("Forward-only synthetic DEV migration.");
}
