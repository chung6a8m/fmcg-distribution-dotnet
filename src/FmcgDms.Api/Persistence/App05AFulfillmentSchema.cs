using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(Version, "APP-05.3 source-bound successor assignments and HELD rights")]
[FluentMigrator.Tags(App03A1Profile.SuccessorName)]
public sealed class App05AFulfillmentSchema : Migration
{
    internal const long Version = 202611250003;

    public override void Up()
    {
        Execute.Sql("""
            ALTER TABLE dbo.AConfirmedLineSnapshot ADD CONSTRAINT UQ_App05AConfirmSuccessor
                UNIQUE(TenantId,TripId,AOrderLineId,ConfirmBusinessSourceId,SkuId);

            CREATE TABLE dbo.App05ASuccessor (
                TenantId nvarchar(64) NOT NULL,
                BindingId uniqueidentifier NOT NULL,
                OriginTripId uniqueidentifier NOT NULL,
                DestinationTripId uniqueidentifier NOT NULL,
                OrderLineId uniqueidentifier NOT NULL,
                ConfirmSourceId uniqueidentifier NOT NULL,
                SkuId uniqueidentifier NOT NULL,
                SourceKind varchar(32) NOT NULL DEFAULT('A_REMAINDER') CHECK(SourceKind='A_REMAINDER'),
                FollowUpId uniqueidentifier NOT NULL,
                Quantity decimal(18,3) NOT NULL CHECK(Quantity>0),
                PRIMARY KEY(TenantId,BindingId),
                UNIQUE(TenantId,BindingId,OrderLineId,OriginTripId,SkuId),
                FOREIGN KEY(TenantId,OriginTripId) REFERENCES dbo.App05TripClosure(TenantId,TripId),
                FOREIGN KEY(TenantId,DestinationTripId) REFERENCES dbo.Trip(TenantId,Id),
                FOREIGN KEY(TenantId,OriginTripId,OrderLineId,ConfirmSourceId,SkuId)
                    REFERENCES dbo.AConfirmedLineSnapshot(TenantId,TripId,AOrderLineId,ConfirmBusinessSourceId,SkuId),
                FOREIGN KEY(TenantId,OriginTripId,SourceKind,OrderLineId,FollowUpId)
                    REFERENCES dbo.App05FollowUp(TenantId,TripId,SourceKind,SourceId,FollowUpId),
                CHECK(OriginTripId<>DestinationTripId)
            );
            CREATE INDEX IX_App05ASuccessor_Destination ON dbo.App05ASuccessor(TenantId,DestinationTripId);
            CREATE INDEX IX_App05ASuccessor_Line ON dbo.App05ASuccessor(TenantId,OrderLineId);

            CREATE TABLE dbo.App05AHeldTransfer (
                TenantId nvarchar(64) NOT NULL,
                SuccessorRightId uniqueidentifier NOT NULL,
                BindingId uniqueidentifier NOT NULL,
                OrderLineId uniqueidentifier NOT NULL,
                OriginalRightId uniqueidentifier NOT NULL,
                OriginTripId uniqueidentifier NOT NULL,
                WarehouseId uniqueidentifier NOT NULL,
                SkuId uniqueidentifier NOT NULL,
                RightKind char(1) NOT NULL DEFAULT('A') CHECK(RightKind='A'),
                NeverLoaded decimal(18,3) NOT NULL CHECK(NeverLoaded>=0),
                Returned decimal(18,3) NOT NULL CHECK(Returned>=0),
                CHECK(NeverLoaded+Returned>0),
                PRIMARY KEY(TenantId,SuccessorRightId),
                UNIQUE(TenantId,SuccessorRightId,OriginalRightId),
                FOREIGN KEY(TenantId,BindingId,OrderLineId,OriginTripId,SkuId)
                    REFERENCES dbo.App05ASuccessor(TenantId,BindingId,OrderLineId,OriginTripId,SkuId),
                FOREIGN KEY(TenantId,OriginalRightId,OriginTripId,WarehouseId,SkuId,RightKind)
                    REFERENCES dbo.StockRight(TenantId,Id,TripId,StockLocationId,SkuId,RightKind),
                FOREIGN KEY(TenantId,OriginalRightId,OrderLineId)
                    REFERENCES dbo.AReservation(TenantId,StockRightId,AOrderLineId)
            );
            CREATE INDEX IX_App05AHeldTransfer_Right ON dbo.App05AHeldTransfer(TenantId,OriginalRightId);
            CREATE INDEX IX_App05AHeldTransfer_Binding ON dbo.App05AHeldTransfer(TenantId,BindingId);

            CREATE TABLE dbo.App05AHeldTransferLot (
                TenantId nvarchar(64) NOT NULL,
                SuccessorRightId uniqueidentifier NOT NULL,
                OriginalRightId uniqueidentifier NOT NULL,
                LotId uniqueidentifier NOT NULL,
                NeverLoaded decimal(18,3) NOT NULL CHECK(NeverLoaded>=0),
                Returned decimal(18,3) NOT NULL CHECK(Returned>=0),
                CHECK(NeverLoaded+Returned>0),
                PRIMARY KEY(TenantId,SuccessorRightId,LotId),
                FOREIGN KEY(TenantId,SuccessorRightId,OriginalRightId)
                    REFERENCES dbo.App05AHeldTransfer(TenantId,SuccessorRightId,OriginalRightId),
                FOREIGN KEY(TenantId,OriginalRightId,LotId)
                    REFERENCES dbo.F03LotHold(TenantId,StockRightId,LotId)
            );

            CREATE TABLE dbo.App05ASuccessorCommand (
                TenantId nvarchar(64) NOT NULL,
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CommandId uniqueidentifier NOT NULL,
                PayloadHash binary(32) NOT NULL,
                Receipt nvarchar(max) NOT NULL CHECK(ISJSON(Receipt)=1),
                Applied bit NOT NULL,
                RecordedAt datetimeoffset(7) NOT NULL,
                PRIMARY KEY(TenantId,ActorId,CommandId)
            );
            """);
        foreach (string table in new[] { "App05ASuccessor", "App05AHeldTransfer", "App05AHeldTransferLot", "App05ASuccessorCommand" })
            Execute.Sql($"""
                CREATE TRIGGER dbo.TR_{table}_Immutable ON dbo.{table} INSTEAD OF UPDATE, DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51058, 'APP-05 successor history is immutable.', 1;
                END;
                """);
    }

    public override void Down() => throw new NotSupportedException("Forward-only synthetic DEV migration.");
}
