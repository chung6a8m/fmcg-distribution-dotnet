using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(202611220004, "APP-02C.4 partial A delivery and due-dated AR")]
public sealed class App02c4Schema : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE dbo.C4DeliveryWriter (
                TenantId nvarchar(64) NOT NULL,
                TripId uniqueidentifier NOT NULL,
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CONSTRAINT PK_C4DeliveryWriter PRIMARY KEY(TenantId,TripId),
                CONSTRAINT FK_C4Writer_Trip FOREIGN KEY(TenantId,TripId) REFERENCES dbo.Trip(TenantId,Id)
            );
            CREATE TABLE dbo.C4ShipToAmendment (
                TenantId nvarchar(64) NOT NULL,
                Id uniqueidentifier NOT NULL,
                OrderId uniqueidentifier NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                TripId uniqueidentifier NOT NULL,
                Version int NOT NULL CHECK(Version>1),
                SelectionId uniqueidentifier NOT NULL,
                ShipToSnapshotId uniqueidentifier NOT NULL,
                Reason nvarchar(240) NOT NULL CHECK(LEN(Reason)>0),
                CONSTRAINT PK_C4ShipToAmendment PRIMARY KEY(TenantId,Id),
                CONSTRAINT UQ_C4Amendment_Version UNIQUE(TenantId,OrderId,Version),
                CONSTRAINT UQ_C4Amendment_Selection UNIQUE(TenantId,SelectionId),
                CONSTRAINT UQ_C4Amendment_Delivery UNIQUE(TenantId,Id,OrderId,ShipToSnapshotId),
                CONSTRAINT FK_C4Amendment_Order FOREIGN KEY(TenantId,OrderId,TripId,CustomerId)
                    REFERENCES dbo.AOrder(TenantId,Id,TripId,CustomerId),
                CONSTRAINT FK_C4Amendment_Source FOREIGN KEY(TenantId,Id) REFERENCES dbo.BusinessSource(TenantId,Id),
                CONSTRAINT FK_C4Amendment_Selection FOREIGN KEY(TenantId,CustomerId,SelectionId)
                    REFERENCES dbo.C1ShipToSelection(TenantId,CustomerId,Id),
                CONSTRAINT FK_C4Amendment_Snapshot FOREIGN KEY(TenantId,CustomerId,ShipToSnapshotId)
                    REFERENCES dbo.ShipToSnapshot(TenantId,CustomerId,Id)
            );
            CREATE TABLE dbo.C4DeliveryEvidence (
                TenantId nvarchar(64) NOT NULL,
                Id uniqueidentifier NOT NULL,
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                ClientCommandId uniqueidentifier NOT NULL,
                TripId uniqueidentifier NOT NULL,
                ManifestLineId uniqueidentifier NOT NULL,
                ShipToVersion int NOT NULL CHECK(ShipToVersion>0),
                Quantity decimal(18,3) NOT NULL CHECK(Quantity>0),
                OccurredAtUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
                CONSTRAINT PK_C4DeliveryEvidence PRIMARY KEY(TenantId,Id),
                CONSTRAINT FK_C4Evidence_Command FOREIGN KEY(TenantId,Id) REFERENCES dbo.BusinessCommand(TenantId,Id),
                CONSTRAINT UQ_C4Evidence_Command UNIQUE(TenantId,ActorId,ClientCommandId),
                CONSTRAINT UQ_C4Evidence_Delivery UNIQUE(TenantId,Id,TripId,ManifestLineId,Quantity),
                CONSTRAINT FK_C4Evidence_Manifest FOREIGN KEY(TenantId,ManifestLineId) REFERENCES dbo.TripManifestLine(TenantId,Id),
                CONSTRAINT FK_C4Evidence_Trip FOREIGN KEY(TenantId,TripId) REFERENCES dbo.Trip(TenantId,Id)
            );
            ALTER TABLE dbo.C2CommandScope ADD DeliveryTripId uniqueidentifier NULL;
            ALTER TABLE dbo.SaleDelivery ADD C4EvidenceId uniqueidentifier NULL, C4ShipToAmendmentId uniqueidentifier NULL,
                C4PriorLineQuantity decimal(18,3) NULL;
            """);
        Execute.Sql("""
            ALTER TABLE dbo.SaleDelivery ADD
                LegacyTripBusinessDate AS (CASE WHEN C4EvidenceId IS NULL THEN BusinessDate END) PERSISTED,
                LegacyOrderShipToSnapshotId AS (CASE WHEN C4EvidenceId IS NULL THEN ShipToSnapshotId END) PERSISTED;
            """);
        Execute.Sql("""
            ALTER TABLE dbo.C2CommandScope ADD CONSTRAINT FK_C4Command_Trip
                FOREIGN KEY(TenantId,DeliveryTripId) REFERENCES dbo.Trip(TenantId,Id);
            ALTER TABLE dbo.C2CommandScope ADD CONSTRAINT CK_C4Command_Scope
                CHECK(DeliveryTripId IS NULL OR (LoadTripId IS NULL AND RoleCode='sales'));
            ALTER TABLE dbo.SaleDelivery DROP CONSTRAINT FK_SaleDelivery_TripBusinessDate, FK_SaleDelivery_AOrder;
            ALTER TABLE dbo.SaleDelivery ADD CONSTRAINT FK_SaleDelivery_TripBusinessDate
                FOREIGN KEY(TenantId,TripId,LegacyTripBusinessDate) REFERENCES dbo.Trip(TenantId,Id,BusinessDate);
            ALTER TABLE dbo.SaleDelivery ADD CONSTRAINT FK_SaleDelivery_AOrder
                FOREIGN KEY(TenantId,AOrderId,TripId,CustomerId,LegacyOrderShipToSnapshotId)
                REFERENCES dbo.AOrder(TenantId,Id,TripId,CustomerId,ShipToSnapshotId);
            ALTER TABLE dbo.SaleDelivery ADD CONSTRAINT FK_SaleDelivery_AOrderIdentity
                FOREIGN KEY(TenantId,AOrderId,TripId,CustomerId) REFERENCES dbo.AOrder(TenantId,Id,TripId,CustomerId);
            ALTER TABLE dbo.SaleDelivery ADD CONSTRAINT FK_SaleDelivery_C4Evidence
                FOREIGN KEY(TenantId,C4EvidenceId,TripId,TripManifestLineId,BaseQuantity)
                REFERENCES dbo.C4DeliveryEvidence(TenantId,Id,TripId,ManifestLineId,Quantity);
            ALTER TABLE dbo.SaleDelivery ADD CONSTRAINT FK_SaleDelivery_C4Amendment
                FOREIGN KEY(TenantId,C4ShipToAmendmentId,AOrderId,ShipToSnapshotId)
                REFERENCES dbo.C4ShipToAmendment(TenantId,Id,OrderId,ShipToSnapshotId);
            ALTER TABLE dbo.SaleDelivery ADD CONSTRAINT CK_SaleDelivery_C4Mode
                CHECK((C4EvidenceId IS NOT NULL AND RightKind='A' AND C4PriorLineQuantity IS NOT NULL AND C4PriorLineQuantity>=0)
                    OR (C4EvidenceId IS NULL AND C4ShipToAmendmentId IS NULL AND C4PriorLineQuantity IS NULL));
            CREATE UNIQUE INDEX UX_SaleDelivery_C4Evidence ON dbo.SaleDelivery(TenantId,C4EvidenceId) WHERE C4EvidenceId IS NOT NULL;
            """);
        // The legacy FKs bind their persisted witnesses. C.4 binds its occurred receipt and exact version.
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_SaleDelivery_Scope ON dbo.SaleDelivery AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (
                    SELECT 1 FROM inserted i
                    JOIN dbo.TripManifestLine ml ON ml.TenantId=i.TenantId AND ml.Id=i.TripManifestLineId
                    JOIN dbo.BusinessSource bs ON bs.TenantId=i.TenantId AND bs.Id=i.BusinessSourceId
                    LEFT JOIN dbo.C2Confirmation c ON c.TenantId=i.TenantId AND c.OrderId=i.AOrderId
                    LEFT JOIN dbo.C4DeliveryEvidence e ON e.TenantId=i.TenantId AND e.Id=i.C4EvidenceId
                    LEFT JOIN dbo.C4ShipToAmendment am ON am.TenantId=i.TenantId AND am.Id=i.C4ShipToAmendmentId
                    WHERE (i.C4EvidenceId IS NULL AND ml.IsC3=1)
                       OR (i.C4EvidenceId IS NOT NULL AND (ml.IsC3=0 OR c.OrderId IS NULL
                        OR bs.BusinessCommandId<>e.Id OR bs.OccurredAtUtc<>e.OccurredAtUtc
                        OR (e.ShipToVersion=1 AND (am.Id IS NOT NULL OR c.ShipToSnapshotId<>i.ShipToSnapshotId))
                        OR (e.ShipToVersion>1 AND (am.Id IS NULL OR am.Version<>e.ShipToVersion))
                        OR i.C4PriorLineQuantity<>(SELECT COALESCE(SUM(d.BaseQuantity),0) FROM dbo.SaleDelivery d
                            WHERE d.TenantId=i.TenantId AND d.AOrderLineId=i.AOrderLineId AND d.Id<>i.Id)
                        OR i.BusinessDate<>CAST((e.OccurredAtUtc AT TIME ZONE 'UTC') AT TIME ZONE 'SE Asia Standard Time' AS date)
                        OR EXISTS(SELECT 1 FROM dbo.C4ShipToAmendment x WITH (HOLDLOCK)
                            WHERE x.TenantId=i.TenantId AND x.OrderId=i.AOrderId AND x.Version>e.ShipToVersion)))
                )
                    THROW 51604, 'Delivery date, model or ShipTo lineage is invalid.', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_AOrder_DeliveryShipTo ON dbo.AOrder AFTER UPDATE AS
            BEGIN
                SET NOCOUNT ON;
                IF UPDATE(ShipToSnapshotId) AND EXISTS(SELECT 1 FROM inserted i
                    JOIN deleted old ON old.TenantId=i.TenantId AND old.Id=i.Id
                    WHERE old.ShipToSnapshotId<>i.ShipToSnapshotId AND EXISTS(SELECT 1 FROM dbo.C2Confirmation c WITH (HOLDLOCK)
                        WHERE c.TenantId=i.TenantId AND c.OrderId=i.Id))
                    THROW 51607, 'The original Order ShipTo is immutable.', 1;
            END;
            """);
        foreach (string table in new[] { "C4ShipToAmendment", "C4DeliveryEvidence" })
            Execute.Sql($"""
                CREATE TRIGGER dbo.TR_{table}_Immutable ON dbo.{table} INSTEAD OF UPDATE, DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51605, 'C.4 delivery evidence and amendments are immutable.', 1;
                END;
                """);
    }

    public override void Down() => throw new NotSupportedException("Forward-only application migration.");
}
