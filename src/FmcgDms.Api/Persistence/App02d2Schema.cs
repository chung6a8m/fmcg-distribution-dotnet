using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(202611220008, "APP-02D.2 scoped B sales and immutable ShipTo lineage")]
public sealed class App02d2Schema : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE dbo.D2ShipToBinding (
                TenantId nvarchar(64) NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                ShipToSnapshotId uniqueidentifier NOT NULL,
                SelectionId uniqueidentifier NOT NULL,
                CONSTRAINT PK_D2ShipToBinding PRIMARY KEY(TenantId,ShipToSnapshotId),
                CONSTRAINT UQ_D2ShipToBinding_Sale UNIQUE(TenantId,CustomerId,ShipToSnapshotId,SelectionId),
                CONSTRAINT FK_D2ShipToBinding_Snapshot FOREIGN KEY(TenantId,CustomerId,ShipToSnapshotId)
                    REFERENCES dbo.ShipToSnapshot(TenantId,CustomerId,Id),
                CONSTRAINT FK_D2ShipToBinding_Selection FOREIGN KEY(TenantId,CustomerId,SelectionId)
                    REFERENCES dbo.C1ShipToSelection(TenantId,CustomerId,Id)
            );

            CREATE TABLE dbo.D2SaleCommandScope (
                TenantId nvarchar(64) NOT NULL,
                CommandRowId uniqueidentifier NOT NULL,
                TripId uniqueidentifier NOT NULL,
                CONSTRAINT PK_D2SaleCommandScope PRIMARY KEY(TenantId,CommandRowId),
                CONSTRAINT FK_D2SaleCommandScope_Command FOREIGN KEY(TenantId,CommandRowId)
                    REFERENCES dbo.BusinessCommand(TenantId,Id),
                CONSTRAINT FK_D2SaleCommandScope_Trip FOREIGN KEY(TenantId,TripId)
                    REFERENCES dbo.Trip(TenantId,Id)
            );

            ALTER TABLE dbo.SaleDelivery ADD D2SelectionId uniqueidentifier NULL;
            """);

        Execute.Sql("""
            ALTER TABLE dbo.SaleDelivery ADD CONSTRAINT FK_SaleDelivery_D2ShipTo
                FOREIGN KEY(TenantId,CustomerId,ShipToSnapshotId,D2SelectionId)
                REFERENCES dbo.D2ShipToBinding(TenantId,CustomerId,ShipToSnapshotId,SelectionId);
            ALTER TABLE dbo.SaleDelivery ADD CONSTRAINT CK_SaleDelivery_D2Mode
                CHECK(D2SelectionId IS NULL OR (RightKind='B' AND InvoiceEligible=0));
            """);

        // Keep the legacy and C.4 predicates. D.2 needs its own command scope
        // and the committed D.1 warehouse source for the exact loaded B line.
        Execute.Sql("""
            CREATE OR ALTER TRIGGER dbo.TR_SaleDelivery_Scope ON dbo.SaleDelivery AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (
                    SELECT 1 FROM inserted i
                    JOIN dbo.TripManifestLine ml ON ml.TenantId=i.TenantId AND ml.Id=i.TripManifestLineId
                    JOIN dbo.BusinessSource bs ON bs.TenantId=i.TenantId AND bs.Id=i.BusinessSourceId
                    LEFT JOIN dbo.C2Confirmation c ON c.TenantId=i.TenantId AND c.OrderId=i.AOrderId
                    LEFT JOIN dbo.C4DeliveryEvidence e ON e.TenantId=i.TenantId AND e.Id=i.C4EvidenceId
                    LEFT JOIN dbo.C4ShipToAmendment am ON am.TenantId=i.TenantId AND am.Id=i.C4ShipToAmendmentId
                    WHERE (i.C4EvidenceId IS NULL AND i.D2SelectionId IS NULL AND ml.IsC3=1)
                       OR (i.D2SelectionId IS NOT NULL AND (ml.IsC3=0 OR ml.RightKind<>'B'
                        OR NOT EXISTS(
                            SELECT 1 FROM dbo.D2SaleCommandScope sc
                            JOIN dbo.BusinessCommand sale ON sale.TenantId=sc.TenantId AND sale.Id=sc.CommandRowId
                                AND sale.Status='RECEIVED'
                            WHERE sc.TenantId=i.TenantId AND sc.TripId=i.TripId AND sc.CommandRowId=bs.BusinessCommandId)
                        OR NOT EXISTS(
                            SELECT 1 FROM dbo.BusinessSourceLine sl
                            JOIN dbo.BusinessSource src ON src.TenantId=sl.TenantId AND src.Id=sl.BusinessSourceId
                                AND src.SourceKind='MANIFEST_LOAD' AND src.TripId=i.TripId
                            JOIN dbo.BusinessCommand load ON load.TenantId=src.TenantId AND load.Id=src.BusinessCommandId
                                AND load.Status='APPLIED'
                            JOIN dbo.C2CommandScope sc ON sc.TenantId=load.TenantId AND sc.CommandRowId=load.Id
                                AND sc.IsD1Load=1 AND sc.LoadTripId=i.TripId
                            WHERE sl.TenantId=i.TenantId AND sl.Id=i.LoadSourceLineId)))
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

        // A D.1 grant's approval ID is the versioned proof. Settlement may
        // change its counters, but replacing the proof would invalidate D.2.
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_D1CreditGrant_ProofImmutable ON dbo.BCreditGrant
            AFTER UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS(
                    SELECT 1 FROM deleted oldGrant
                    JOIN dbo.BusinessSourceLine sl ON sl.TenantId=oldGrant.TenantId
                        AND sl.Id=oldGrant.BusinessSourceLineId
                    JOIN dbo.BusinessSource src ON src.TenantId=sl.TenantId AND src.Id=sl.BusinessSourceId
                    JOIN dbo.C2CommandScope scope ON scope.TenantId=src.TenantId
                        AND scope.CommandRowId=src.BusinessCommandId AND scope.D1GrantTripId=oldGrant.TripId
                        AND scope.RoleCode='finance'
                    LEFT JOIN inserted newGrant ON newGrant.TenantId=oldGrant.TenantId AND newGrant.Id=oldGrant.Id
                    WHERE newGrant.Id IS NULL OR EXISTS(
                        SELECT oldGrant.TripId,oldGrant.CustomerId,oldGrant.ShipToSnapshotId,oldGrant.PoolCode,
                            oldGrant.CurrencyUnit,oldGrant.BusinessSourceLineId,oldGrant.GrantedAmount,oldGrant.ApprovalId
                        EXCEPT
                        SELECT newGrant.TripId,newGrant.CustomerId,newGrant.ShipToSnapshotId,newGrant.PoolCode,
                            newGrant.CurrencyUnit,newGrant.BusinessSourceLineId,newGrant.GrantedAmount,newGrant.ApprovalId))
                    THROW 51043, 'D.1 credit grant approval and source are immutable.', 1;
            END;
            """);

        foreach (string table in new[] { "D2ShipToBinding", "D2SaleCommandScope" })
            Execute.Sql($"""
                CREATE TRIGGER dbo.TR_{table}_Immutable ON dbo.{table}
                AFTER UPDATE, DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS(SELECT 1 FROM deleted)
                        THROW 51042, 'D.2 scope provenance is immutable.', 1;
                END;
                """);
    }

    public override void Down() => throw new NotSupportedException("Forward-only application migration.");
}
