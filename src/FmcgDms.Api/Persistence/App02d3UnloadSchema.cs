using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(202611220010, "APP-02D.3 trip-wide stock return and source-bound B quota release")]
public sealed class App02d3UnloadSchema : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE dbo.D3TripUnload (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_D3TripUnload PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                TripId uniqueidentifier NOT NULL,
                WarehouseLocationId uniqueidentifier NOT NULL,
                VehicleLocationId uniqueidentifier NOT NULL,
                SourceKind nvarchar(48) NOT NULL CONSTRAINT DF_D3TripUnload_Kind DEFAULT(N'TRIP_UNLOAD'),
                WarehouseKind nvarchar(16) NOT NULL CONSTRAINT DF_D3TripUnload_WarehouseKind DEFAULT(N'WAREHOUSE'),
                RecordedAtUtc datetime2(7) NOT NULL,
                CONSTRAINT UQ_D3TripUnload_Trip UNIQUE(TenantId,TripId),
                CONSTRAINT UQ_D3TripUnload_LineScope UNIQUE(TenantId,Id,TripId,WarehouseLocationId,VehicleLocationId),
                CONSTRAINT UQ_D3TripUnload_ReleaseScope UNIQUE(TenantId,Id,TripId,WarehouseLocationId),
                CONSTRAINT FK_D3TripUnload_Source FOREIGN KEY(TenantId,Id,TripId,SourceKind)
                    REFERENCES dbo.BusinessSource(TenantId,Id,TripId,SourceKind),
                CONSTRAINT FK_D3TripUnload_Trip FOREIGN KEY(TenantId,TripId,VehicleLocationId)
                    REFERENCES dbo.Trip(TenantId,Id,VehicleLocationId),
                CONSTRAINT FK_D3TripUnload_Warehouse FOREIGN KEY(TenantId,WarehouseLocationId,WarehouseKind)
                    REFERENCES dbo.StockLocation(TenantId,Id,Kind),
                CONSTRAINT CK_D3TripUnload_Kind CHECK(SourceKind=N'TRIP_UNLOAD' AND WarehouseKind=N'WAREHOUSE')
            );

            CREATE TABLE dbo.D3TripUnloadLine (
                TenantId nvarchar(64) NOT NULL,
                TripUnloadId uniqueidentifier NOT NULL,
                TripId uniqueidentifier NOT NULL,
                WarehouseLocationId uniqueidentifier NOT NULL,
                VehicleLocationId uniqueidentifier NOT NULL,
                TripManifestLineId uniqueidentifier NOT NULL,
                LoadSourceLineId uniqueidentifier NOT NULL,
                StockRightId uniqueidentifier NOT NULL,
                RightKind char(1) NOT NULL,
                SkuId uniqueidentifier NOT NULL,
                SourceLineId uniqueidentifier NOT NULL,
                BaseQuantity decimal(18,3) NOT NULL,
                CostAmount decimal(19,4) NOT NULL,
                CONSTRAINT PK_D3TripUnloadLine PRIMARY KEY(TenantId,TripManifestLineId),
                CONSTRAINT UQ_D3TripUnloadLine_Source UNIQUE(TenantId,SourceLineId),
                CONSTRAINT FK_D3TripUnloadLine_Header FOREIGN KEY(TenantId,TripUnloadId,TripId,WarehouseLocationId,VehicleLocationId)
                    REFERENCES dbo.D3TripUnload(TenantId,Id,TripId,WarehouseLocationId,VehicleLocationId),
                CONSTRAINT FK_D3TripUnloadLine_Manifest FOREIGN KEY(TenantId,TripManifestLineId,TripId,SkuId,StockRightId,RightKind,LoadSourceLineId)
                    REFERENCES dbo.TripManifestLine(TenantId,Id,TripId,SkuId,StockRightId,RightKind,LoadSourceLineId),
                CONSTRAINT FK_D3TripUnloadLine_Right FOREIGN KEY(TenantId,StockRightId,TripId,WarehouseLocationId,SkuId,RightKind)
                    REFERENCES dbo.StockRight(TenantId,Id,TripId,StockLocationId,SkuId,RightKind),
                CONSTRAINT FK_D3TripUnloadLine_Source FOREIGN KEY(TenantId,SourceLineId,TripUnloadId,SkuId,BaseQuantity,CostAmount)
                    REFERENCES dbo.BusinessSourceLine(TenantId,Id,BusinessSourceId,SkuId,BaseQuantity,Amount),
                CONSTRAINT CK_D3TripUnloadLine_Amount CHECK(BaseQuantity>0 AND CostAmount>=0)
            );

            CREATE TABLE dbo.D3BQuotaRelease (
                TenantId nvarchar(64) NOT NULL,
                TripUnloadId uniqueidentifier NOT NULL,
                TripId uniqueidentifier NOT NULL,
                WarehouseLocationId uniqueidentifier NOT NULL,
                StockRightId uniqueidentifier NOT NULL,
                SkuId uniqueidentifier NOT NULL,
                RightKind char(1) NOT NULL CONSTRAINT DF_D3BQuotaRelease_Kind DEFAULT('B'),
                SourceLineId uniqueidentifier NOT NULL,
                BaseQuantity decimal(18,3) NOT NULL CONSTRAINT DF_D3BQuotaRelease_Base DEFAULT(0),
                CostAmount decimal(19,4) NOT NULL CONSTRAINT DF_D3BQuotaRelease_Cost DEFAULT(0),
                ReleasedBaseQuantity decimal(18,3) NOT NULL,
                NeverLoadedBaseQuantity decimal(18,3) NOT NULL,
                CONSTRAINT PK_D3BQuotaRelease PRIMARY KEY(TenantId,StockRightId),
                CONSTRAINT UQ_D3BQuotaRelease_Source UNIQUE(TenantId,SourceLineId),
                CONSTRAINT FK_D3BQuotaRelease_Header FOREIGN KEY(TenantId,TripUnloadId,TripId,WarehouseLocationId)
                    REFERENCES dbo.D3TripUnload(TenantId,Id,TripId,WarehouseLocationId),
                CONSTRAINT FK_D3BQuotaRelease_Right FOREIGN KEY(TenantId,StockRightId,TripId,WarehouseLocationId,SkuId,RightKind)
                    REFERENCES dbo.StockRight(TenantId,Id,TripId,StockLocationId,SkuId,RightKind),
                CONSTRAINT FK_D3BQuotaRelease_Quota FOREIGN KEY(TenantId,StockRightId)
                    REFERENCES dbo.BQuota(TenantId,StockRightId),
                CONSTRAINT FK_D3BQuotaRelease_Source FOREIGN KEY(TenantId,SourceLineId,TripUnloadId,SkuId,BaseQuantity,CostAmount)
                    REFERENCES dbo.BusinessSourceLine(TenantId,Id,BusinessSourceId,SkuId,BaseQuantity,Amount),
                CONSTRAINT CK_D3BQuotaRelease_Amount CHECK(RightKind='B' AND BaseQuantity=0 AND CostAmount=0
                    AND ReleasedBaseQuantity>0 AND NeverLoadedBaseQuantity>=0 AND NeverLoadedBaseQuantity<=ReleasedBaseQuantity)
            );
            """);

        foreach (string table in new[] { "D3TripUnload", "D3TripUnloadLine", "D3BQuotaRelease" })
            Execute.Sql($"""
                CREATE TRIGGER dbo.TR_{table}_Immutable ON dbo.{table}
                AFTER UPDATE, DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS(SELECT 1 FROM deleted)
                        THROW 51044, 'D.3 return and release evidence is immutable.', 1;
                END;
                """);
    }

    public override void Down() => throw new NotSupportedException("Forward-only application migration.");
}
