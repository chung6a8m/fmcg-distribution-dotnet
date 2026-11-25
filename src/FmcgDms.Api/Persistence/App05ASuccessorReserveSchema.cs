using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(Version, "APP-05.4a bind new stock reservations to the successor")]
[FluentMigrator.Tags(App03A1Profile.SuccessorName)]
public sealed class App05ASuccessorReserveSchema : Migration
{
    internal const long Version = 202611250004;

    public override void Up()
    {
        Execute.Sql("""
            ALTER TABLE dbo.App05ASuccessor ADD CONSTRAINT UQ_App05ASuccessor_ReserveScope
                UNIQUE(TenantId,BindingId,OrderLineId,DestinationTripId,SkuId);

            CREATE TABLE dbo.App05ASuccessorReserve (
                TenantId nvarchar(64) NOT NULL,
                BindingId uniqueidentifier NOT NULL,
                OrderLineId uniqueidentifier NOT NULL,
                DestinationTripId uniqueidentifier NOT NULL,
                WarehouseId uniqueidentifier NOT NULL,
                SkuId uniqueidentifier NOT NULL,
                RightId uniqueidentifier NOT NULL,
                RightKind char(1) NOT NULL DEFAULT('A') CHECK(RightKind='A'),
                PRIMARY KEY(TenantId,RightId),
                FOREIGN KEY(TenantId,BindingId,OrderLineId,DestinationTripId,SkuId)
                    REFERENCES dbo.App05ASuccessor(TenantId,BindingId,OrderLineId,DestinationTripId,SkuId),
                FOREIGN KEY(TenantId,RightId,DestinationTripId,WarehouseId,SkuId,RightKind)
                    REFERENCES dbo.StockRight(TenantId,Id,TripId,StockLocationId,SkuId,RightKind),
                FOREIGN KEY(TenantId,RightId,OrderLineId)
                    REFERENCES dbo.AReservation(TenantId,StockRightId,AOrderLineId)
            );
            CREATE INDEX IX_App05ASuccessorReserve_Binding ON dbo.App05ASuccessorReserve(TenantId,BindingId);
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_App05ASuccessorReserve_Immutable ON dbo.App05ASuccessorReserve INSTEAD OF UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                THROW 51059, 'APP-05 successor reserve history is immutable.', 1;
            END;
            """);
    }

    public override void Down() => throw new NotSupportedException("Forward-only synthetic DEV migration.");
}
