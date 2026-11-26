using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(Version, "APP-05.4b deliver the original Confirm on its assigned successor")]
[FluentMigrator.Tags(App03A1Profile.SuccessorName)]
public sealed class App05ASuccessorDeliverySchema : Migration
{
    internal const long Version = 202611260001;

    public override void Up()
    {
        Execute.Sql("""
            ALTER TABLE dbo.App05ASuccessor ADD CONSTRAINT UQ_App05ASuccessor_Delivery
                UNIQUE(TenantId,BindingId,OriginTripId,DestinationTripId,OrderLineId,SkuId);
            ALTER TABLE dbo.App05ASuccessorReserve ADD CONSTRAINT UQ_App05ASuccessorReserve_Delivery
                UNIQUE(TenantId,RightId,BindingId,DestinationTripId,OrderLineId,SkuId);
            ALTER TABLE dbo.SaleDelivery ADD App05BindingId uniqueidentifier NULL, App05OriginTripId uniqueidentifier NULL;
            """);
        Execute.Sql("""
            ALTER TABLE dbo.SaleDelivery ADD OrderIdentityTripId AS (ISNULL(App05OriginTripId,TripId)) PERSISTED;
            """);
        Execute.Sql("""
            ALTER TABLE dbo.SaleDelivery DROP CONSTRAINT FK_SaleDelivery_AOrderIdentity;
            ALTER TABLE dbo.SaleDelivery ADD CONSTRAINT FK_SaleDelivery_AOrderIdentity
                FOREIGN KEY(TenantId,AOrderId,OrderIdentityTripId,CustomerId) REFERENCES dbo.AOrder(TenantId,Id,TripId,CustomerId);
            ALTER TABLE dbo.SaleDelivery ADD CONSTRAINT CK_SaleDelivery_App05Successor
                CHECK((App05BindingId IS NULL AND App05OriginTripId IS NULL)
                    OR (App05BindingId IS NOT NULL AND App05OriginTripId IS NOT NULL
                        AND C4EvidenceId IS NOT NULL AND RightKind='A' AND AOrderId IS NOT NULL AND AOrderLineId IS NOT NULL));
            ALTER TABLE dbo.SaleDelivery ADD CONSTRAINT FK_SaleDelivery_App05Binding
                FOREIGN KEY(TenantId,App05BindingId,App05OriginTripId,TripId,AOrderLineId,SkuId)
                REFERENCES dbo.App05ASuccessor(TenantId,BindingId,OriginTripId,DestinationTripId,OrderLineId,SkuId);
            ALTER TABLE dbo.SaleDelivery ADD CONSTRAINT FK_SaleDelivery_App05Reserve
                FOREIGN KEY(TenantId,StockRightId,App05BindingId,TripId,AOrderLineId,SkuId)
                REFERENCES dbo.App05ASuccessorReserve(TenantId,RightId,BindingId,DestinationTripId,OrderLineId,SkuId);
            """);
        Execute.Sql("""
            CREATE VIEW dbo.App05AExecutionRight AS
            SELECT r.TenantId,r.RightId,r.BindingId,b.OriginTripId,r.DestinationTripId,r.OrderLineId,b.ConfirmSourceId,r.WarehouseId,r.SkuId
            FROM dbo.App05ASuccessorReserve r
            JOIN dbo.App05ASuccessor b ON b.TenantId=r.TenantId AND b.BindingId=r.BindingId;
            """);
    }

    public override void Down() => throw new NotSupportedException("Forward-only synthetic DEV migration.");
}

public sealed partial class App01a2CommandService
{
    // r is the FK-bound StockRight and s is the frozen confirmation of its AReservation line.
    private const string SuccessorExecutionRightJoin = """
        LEFT JOIN dbo.App05AExecutionRight successorRight ON successorRight.TenantId=r.TenantId AND successorRight.RightId=r.Id
            AND successorRight.DestinationTripId=r.TripId AND successorRight.WarehouseId=r.StockLocationId
            AND successorRight.OrderLineId=s.AOrderLineId AND successorRight.SkuId=s.SkuId
            AND successorRight.OriginTripId=s.TripId AND successorRight.ConfirmSourceId=s.ConfirmBusinessSourceId
        """;
}
