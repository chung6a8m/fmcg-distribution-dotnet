using FluentMigrator;

namespace FmcgDms.Api.Persistence;

/// <summary>
/// APP-01B.1 schema only: an immutable synthetic opening snapshot, mutable
/// per-location physical balances, a rights-bound manifest and one source-backed
/// transfer record per loaded manifest line. Posting belongs to APP-01B.2.
/// StockPool/StockRight remain ALLOCATION rights, not physical inventory.
/// </summary>
[Migration(202611160004, "APP-01B.1 synthetic physical stock and manifest state")]
public sealed class App01b1PhysicalStock : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            -- Composite alternate keys let FKs check role, Trip, SKU and source
            -- without trusting denormalized IDs supplied by a client.
            ALTER TABLE dbo.StockLocation ADD CONSTRAINT
                UQ_StockLocation_Tenant_Id_Kind UNIQUE (TenantId, Id, Kind);
            ALTER TABLE dbo.Trip ADD CONSTRAINT
                UQ_Trip_Tenant_Id_Vehicle UNIQUE (TenantId, Id, VehicleLocationId);
            ALTER TABLE dbo.BusinessSource ADD CONSTRAINT
                UQ_BusinessSource_ManifestScope UNIQUE (TenantId, Id, TripId, SourceKind);
            ALTER TABLE dbo.BusinessSourceLine ADD CONSTRAINT
                UQ_BusinessSourceLine_ManifestScope UNIQUE
                    (TenantId, Id, BusinessSourceId, LineNumber, SkuId, BaseQuantity, Amount);
            ALTER TABLE dbo.StockRight ADD CONSTRAINT
                UQ_StockRight_ManifestScope UNIQUE
                    (TenantId, Id, TripId, StockLocationId, SkuId, RightKind);

            -- T0 is a separately authored, immutable snapshot. It is NOT
            -- StockPool.OnHandBaseQuantity and cannot be silently re-opened.
            CREATE TABLE dbo.PhysicalStockOpening (
                TenantId nvarchar(64) NOT NULL,
                StockLocationId uniqueidentifier NOT NULL,
                SkuId uniqueidentifier NOT NULL,
                SnapshotCode nvarchar(80) NOT NULL,
                BaseQuantity decimal(18,3) NOT NULL,
                CostAmount decimal(19,4) NOT NULL,
                CapturedAtUtc datetime2(7) NOT NULL
                    CONSTRAINT DF_PhysicalStockOpening_Captured DEFAULT (SYSUTCDATETIME()),
                RowVersion rowversion NOT NULL,
                CONSTRAINT PK_PhysicalStockOpening PRIMARY KEY
                    (TenantId, StockLocationId, SkuId),
                CONSTRAINT FK_PhysicalStockOpening_Location FOREIGN KEY
                    (TenantId, StockLocationId) REFERENCES dbo.StockLocation (TenantId, Id),
                CONSTRAINT FK_PhysicalStockOpening_Sku FOREIGN KEY
                    (TenantId, SkuId) REFERENCES dbo.Sku (TenantId, Id),
                CONSTRAINT CK_PhysicalStockOpening_Amounts CHECK (
                    BaseQuantity >= 0 AND CostAmount >= 0
                    AND (BaseQuantity <> 0 OR CostAmount = 0)
                    AND LEN(LTRIM(RTRIM(SnapshotCode))) > 0)
            );

            -- Every live physical resource must be explicitly opened, including
            -- the vehicle's 0/0 balance. No implicit stock from rights/allocations.
            CREATE TABLE dbo.PhysicalStockBalance (
                TenantId nvarchar(64) NOT NULL,
                StockLocationId uniqueidentifier NOT NULL,
                SkuId uniqueidentifier NOT NULL,
                OnHandBaseQuantity decimal(18,3) NOT NULL,
                OnHandCostAmount decimal(19,4) NOT NULL,
                RowVersion rowversion NOT NULL,
                CONSTRAINT PK_PhysicalStockBalance PRIMARY KEY
                    (TenantId, StockLocationId, SkuId),
                CONSTRAINT FK_PhysicalStockBalance_Opening FOREIGN KEY
                    (TenantId, StockLocationId, SkuId)
                    REFERENCES dbo.PhysicalStockOpening (TenantId, StockLocationId, SkuId),
                CONSTRAINT CK_PhysicalStockBalance_Amounts CHECK (
                    OnHandBaseQuantity >= 0 AND OnHandCostAmount >= 0
                    AND (OnHandBaseQuantity <> 0 OR OnHandCostAmount = 0))
            );

            -- One manifest per Trip in this slice. Header source is present only
            -- after an atomic APP-01B.2 load. Role FKs reject vehicle/warehouse
            -- reversal and ensure the vehicle is the Trip's actual location.
            CREATE TABLE dbo.TripManifest (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_TripManifest PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                ManifestNumber nvarchar(80) NOT NULL,
                TripId uniqueidentifier NOT NULL,
                WarehouseLocationId uniqueidentifier NOT NULL,
                WarehouseKind nvarchar(16) NOT NULL
                    CONSTRAINT DF_TripManifest_WarehouseKind DEFAULT (N'WAREHOUSE'),
                VehicleLocationId uniqueidentifier NOT NULL,
                VehicleKind nvarchar(16) NOT NULL
                    CONSTRAINT DF_TripManifest_VehicleKind DEFAULT (N'VEHICLE'),
                LoadBusinessSourceId uniqueidentifier NULL,
                LoadSourceKind nvarchar(48) NOT NULL
                    CONSTRAINT DF_TripManifest_LoadSourceKind DEFAULT (N'MANIFEST_LOAD'),
                Status nvarchar(16) NOT NULL
                    CONSTRAINT DF_TripManifest_Status DEFAULT (N'DRAFT'),
                CreatedAtUtc datetime2(7) NOT NULL
                    CONSTRAINT DF_TripManifest_Created DEFAULT (SYSUTCDATETIME()),
                LoadedAtUtc datetime2(7) NULL,
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_TripManifest_Tenant_Id UNIQUE (TenantId, Id),
                CONSTRAINT UQ_TripManifest_Number UNIQUE (TenantId, ManifestNumber),
                CONSTRAINT UQ_TripManifest_Trip UNIQUE (TenantId, TripId),
                CONSTRAINT UQ_TripManifest_Dimensions UNIQUE
                    (TenantId, Id, TripId, WarehouseLocationId, VehicleLocationId),
                CONSTRAINT UQ_TripManifest_LoadSource UNIQUE
                    (TenantId, Id, LoadBusinessSourceId),
                CONSTRAINT FK_TripManifest_TripVehicle FOREIGN KEY
                    (TenantId, TripId, VehicleLocationId)
                    REFERENCES dbo.Trip (TenantId, Id, VehicleLocationId),
                CONSTRAINT FK_TripManifest_WarehouseRole FOREIGN KEY
                    (TenantId, WarehouseLocationId, WarehouseKind)
                    REFERENCES dbo.StockLocation (TenantId, Id, Kind),
                CONSTRAINT FK_TripManifest_VehicleRole FOREIGN KEY
                    (TenantId, VehicleLocationId, VehicleKind)
                    REFERENCES dbo.StockLocation (TenantId, Id, Kind),
                CONSTRAINT FK_TripManifest_LoadSource FOREIGN KEY
                    (TenantId, LoadBusinessSourceId, TripId, LoadSourceKind)
                    REFERENCES dbo.BusinessSource (TenantId, Id, TripId, SourceKind),
                CONSTRAINT CK_TripManifest_Status CHECK (
                    (Status = N'DRAFT' AND LoadBusinessSourceId IS NULL AND LoadedAtUtc IS NULL)
                    OR (Status = N'LOADED' AND LoadBusinessSourceId IS NOT NULL
                        AND LoadedAtUtc IS NOT NULL)),
                CONSTRAINT CK_TripManifest_Roles CHECK (
                    WarehouseKind = N'WAREHOUSE' AND VehicleKind = N'VEHICLE'
                    AND WarehouseLocationId <> VehicleLocationId
                    AND LoadSourceKind = N'MANIFEST_LOAD'
                    AND LEN(LTRIM(RTRIM(ManifestNumber))) > 0)
            );
            CREATE UNIQUE INDEX UX_TripManifest_OneLoadSource
                ON dbo.TripManifest (TenantId, LoadBusinessSourceId)
                WHERE LoadBusinessSourceId IS NOT NULL;

            -- RightKind and the exact Trip/warehouse/SKU are FK-bound to a
            -- preexisting A reservation or B quota right. Loading a right twice
            -- is not a valid second manifest line, even across manifests.
            CREATE TABLE dbo.TripManifestLine (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_TripManifestLine PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                TripManifestId uniqueidentifier NOT NULL,
                TripId uniqueidentifier NOT NULL,
                WarehouseLocationId uniqueidentifier NOT NULL,
                VehicleLocationId uniqueidentifier NOT NULL,
                LineNumber int NOT NULL,
                SkuId uniqueidentifier NOT NULL,
                StockRightId uniqueidentifier NOT NULL,
                RightKind char(1) NOT NULL,
                PlannedBaseQuantity decimal(18,3) NOT NULL,
                LoadedBaseQuantity decimal(18,3) NOT NULL
                    CONSTRAINT DF_TripManifestLine_LoadedQty DEFAULT (0),
                LoadedCostAmount decimal(19,4) NOT NULL
                    CONSTRAINT DF_TripManifestLine_LoadedCost DEFAULT (0),
                LoadBusinessSourceId uniqueidentifier NULL,
                LoadSourceLineId uniqueidentifier NULL,
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_TripManifestLine_Tenant_Id UNIQUE (TenantId, Id),
                CONSTRAINT UQ_TripManifestLine_Number UNIQUE
                    (TenantId, TripManifestId, LineNumber),
                CONSTRAINT UQ_TripManifestLine_Right UNIQUE (TenantId, StockRightId),
                CONSTRAINT UQ_TripManifestLine_Movement UNIQUE
                    (TenantId, Id, LoadSourceLineId, LoadedBaseQuantity, LoadedCostAmount),
                CONSTRAINT FK_TripManifestLine_Header FOREIGN KEY
                    (TenantId, TripManifestId, TripId, WarehouseLocationId, VehicleLocationId)
                    REFERENCES dbo.TripManifest
                        (TenantId, Id, TripId, WarehouseLocationId, VehicleLocationId),
                CONSTRAINT FK_TripManifestLine_HeaderSource FOREIGN KEY
                    (TenantId, TripManifestId, LoadBusinessSourceId)
                    REFERENCES dbo.TripManifest (TenantId, Id, LoadBusinessSourceId),
                CONSTRAINT FK_TripManifestLine_Right FOREIGN KEY
                    (TenantId, StockRightId, TripId, WarehouseLocationId, SkuId, RightKind)
                    REFERENCES dbo.StockRight
                        (TenantId, Id, TripId, StockLocationId, SkuId, RightKind),
                CONSTRAINT FK_TripManifestLine_ExactSourceLine FOREIGN KEY
                    (TenantId, LoadSourceLineId, LoadBusinessSourceId, LineNumber,
                        SkuId, LoadedBaseQuantity, LoadedCostAmount)
                    REFERENCES dbo.BusinessSourceLine
                        (TenantId, Id, BusinessSourceId, LineNumber, SkuId,
                            BaseQuantity, Amount),
                CONSTRAINT CK_TripManifestLine_Amounts CHECK (
                    LineNumber > 0 AND RightKind IN ('A','B')
                    AND PlannedBaseQuantity > 0 AND LoadedBaseQuantity >= 0
                    AND LoadedBaseQuantity <= PlannedBaseQuantity AND LoadedCostAmount >= 0
                    AND ((LoadedBaseQuantity = 0 AND LoadedCostAmount = 0
                        AND LoadBusinessSourceId IS NULL AND LoadSourceLineId IS NULL)
                      OR (LoadedBaseQuantity > 0 AND LoadBusinessSourceId IS NOT NULL
                        AND LoadSourceLineId IS NOT NULL)))
            );
            CREATE UNIQUE INDEX UX_TripManifestLine_OneSourceLine
                ON dbo.TripManifestLine (TenantId, LoadSourceLineId)
                WHERE LoadSourceLineId IS NOT NULL;

            -- One movement is ONE paired warehouse->vehicle transfer, not two
            -- independently inserted +/- legs. Its warehouse, vehicle, Trip and
            -- A/B right are inherited from the FK-bound manifest line/header.
            -- Atomic balance updates and conservation checks are APP-01B.2 work.
            CREATE TABLE dbo.PhysicalStockMovement (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_PhysicalStockMovement PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                TripManifestLineId uniqueidentifier NOT NULL,
                LoadSourceLineId uniqueidentifier NOT NULL,
                BaseQuantity decimal(18,3) NOT NULL,
                CostAmount decimal(19,4) NOT NULL,
                RecordedAtUtc datetime2(7) NOT NULL
                    CONSTRAINT DF_PhysicalStockMovement_Recorded DEFAULT (SYSUTCDATETIME()),
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_PhysicalStockMovement_Tenant_Id UNIQUE (TenantId, Id),
                CONSTRAINT UQ_PhysicalStockMovement_Line UNIQUE
                    (TenantId, TripManifestLineId),
                CONSTRAINT UQ_PhysicalStockMovement_Source UNIQUE
                    (TenantId, LoadSourceLineId),
                CONSTRAINT FK_PhysicalStockMovement_LoadedLine FOREIGN KEY
                    (TenantId, TripManifestLineId, LoadSourceLineId, BaseQuantity, CostAmount)
                    REFERENCES dbo.TripManifestLine
                        (TenantId, Id, LoadSourceLineId, LoadedBaseQuantity, LoadedCostAmount),
                CONSTRAINT CK_PhysicalStockMovement_Amounts CHECK (
                    BaseQuantity > 0 AND CostAmount >= 0)
            );
            CREATE INDEX IX_TripManifestLine_TripSku
                ON dbo.TripManifestLine (TenantId, TripId, SkuId);
            """);

        // Database-level immutable evidence. A failed load transaction rolls
        // these inserts back; no physical movement can be edited/deleted later.
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_PhysicalStockOpening_Immutable
            ON dbo.PhysicalStockOpening AFTER UPDATE, DELETE
            AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM deleted)
                    THROW 51041, 'APP01B1 opening snapshot is immutable', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_PhysicalStockMovement_Immutable
            ON dbo.PhysicalStockMovement AFTER UPDATE, DELETE
            AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM deleted)
                    THROW 51042, 'APP01B1 physical movement is immutable', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_TripManifest_ImmutableLoaded
            ON dbo.TripManifest AFTER UPDATE, DELETE
            AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM deleted WHERE Status = N'LOADED')
                    THROW 51043, 'APP01B1 loaded manifest is immutable', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_TripManifestLine_InsertOnlyDraft
            ON dbo.TripManifestLine AFTER INSERT
            AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (
                    SELECT 1 FROM inserted i
                    JOIN dbo.TripManifest h
                        ON h.TenantId=i.TenantId AND h.Id=i.TripManifestId
                    WHERE h.Status <> N'DRAFT')
                    THROW 51045, 'APP01B1 cannot append lines to a loaded manifest', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_TripManifestLine_ImmutableLoaded
            ON dbo.TripManifestLine AFTER UPDATE, DELETE
            AS
            BEGIN
                SET NOCOUNT ON;
                -- A loaded line is always immutable. Once its parent is LOADED,
                -- an unposted line cannot be deleted or restaged either. The
                -- only permitted update is an exact staged -> source-backed
                -- load, preserving identity/right/quantities planned at DRAFT.
                IF EXISTS (
                    SELECT 1
                    FROM deleted d
                    LEFT JOIN inserted i
                        ON i.TenantId=d.TenantId AND i.Id=d.Id
                    LEFT JOIN dbo.TripManifest h
                        ON h.TenantId=d.TenantId AND h.Id=d.TripManifestId
                    WHERE d.LoadSourceLineId IS NOT NULL
                       OR (h.Status=N'LOADED' AND (
                            i.Id IS NULL
                            OR i.TripManifestId <> d.TripManifestId
                            OR i.TripId <> d.TripId
                            OR i.WarehouseLocationId <> d.WarehouseLocationId
                            OR i.VehicleLocationId <> d.VehicleLocationId
                            OR i.LineNumber <> d.LineNumber
                            OR i.SkuId <> d.SkuId
                            OR i.StockRightId <> d.StockRightId
                            OR i.RightKind <> d.RightKind
                            OR i.PlannedBaseQuantity <> d.PlannedBaseQuantity
                            OR i.LoadBusinessSourceId IS NULL
                            OR i.LoadSourceLineId IS NULL
                            OR i.LoadedBaseQuantity <= 0))
                )
                    THROW 51044, 'APP01B1 loaded manifest line is immutable', 1;
            END;
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DROP TRIGGER IF EXISTS dbo.TR_TripManifestLine_ImmutableLoaded;
            DROP TRIGGER IF EXISTS dbo.TR_TripManifestLine_InsertOnlyDraft;
            DROP TRIGGER IF EXISTS dbo.TR_TripManifest_ImmutableLoaded;
            DROP TRIGGER IF EXISTS dbo.TR_PhysicalStockMovement_Immutable;
            DROP TRIGGER IF EXISTS dbo.TR_PhysicalStockOpening_Immutable;
            DROP TABLE dbo.PhysicalStockMovement;
            DROP TABLE dbo.TripManifestLine;
            DROP TABLE dbo.TripManifest;
            DROP TABLE dbo.PhysicalStockBalance;
            DROP TABLE dbo.PhysicalStockOpening;
            ALTER TABLE dbo.StockRight DROP CONSTRAINT UQ_StockRight_ManifestScope;
            ALTER TABLE dbo.BusinessSourceLine DROP CONSTRAINT UQ_BusinessSourceLine_ManifestScope;
            ALTER TABLE dbo.BusinessSource DROP CONSTRAINT UQ_BusinessSource_ManifestScope;
            ALTER TABLE dbo.Trip DROP CONSTRAINT UQ_Trip_Tenant_Id_Vehicle;
            ALTER TABLE dbo.StockLocation DROP CONSTRAINT UQ_StockLocation_Tenant_Id_Kind;
            """);
    }
}
