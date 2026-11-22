using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(202611220003, "APP-02C.3 atomic partial load manifests")]
public sealed class App02c3Schema : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE dbo.C3TripWriter (
                TenantId nvarchar(64) NOT NULL,
                TripId uniqueidentifier NOT NULL,
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CONSTRAINT PK_C3TripWriter PRIMARY KEY (TenantId,TripId),
                CONSTRAINT FK_C3TripWriter_Trip FOREIGN KEY (TenantId,TripId) REFERENCES dbo.Trip(TenantId,Id)
            );
            ALTER TABLE dbo.C2CommandScope ADD LoadTripId uniqueidentifier NULL;
            """);
        Execute.Sql("""
            ALTER TABLE dbo.C2CommandScope ADD CONSTRAINT FK_C2CommandScope_LoadTrip
                FOREIGN KEY(TenantId,LoadTripId) REFERENCES dbo.Trip(TenantId,Id);
            ALTER TABLE dbo.C2CommandScope ADD CONSTRAINT CK_C2CommandScope_LoadRole
                CHECK(LoadTripId IS NULL OR RoleCode='warehouse');
            ALTER TABLE dbo.TripManifest ADD IsC3 bit NOT NULL CONSTRAINT DF_TripManifest_IsC3 DEFAULT(0);
            ALTER TABLE dbo.TripManifestLine ADD IsC3 bit NOT NULL CONSTRAINT DF_TripManifestLine_IsC3 DEFAULT(0);
            """);
        Execute.Sql("""
            ALTER TABLE dbo.TripManifest DROP CONSTRAINT UQ_TripManifest_Trip;
            CREATE UNIQUE INDEX UX_TripManifest_LegacyTrip ON dbo.TripManifest(TenantId,TripId) WHERE IsC3=0;
            CREATE INDEX IX_TripManifest_TripModel ON dbo.TripManifest(TenantId,TripId,IsC3);
            ALTER TABLE dbo.TripManifest ADD CONSTRAINT UQ_TripManifest_Model UNIQUE(TenantId,Id,IsC3);
            ALTER TABLE dbo.TripManifestLine DROP CONSTRAINT UQ_TripManifestLine_Right;
            CREATE UNIQUE INDEX UX_TripManifestLine_LegacyRight ON dbo.TripManifestLine(TenantId,StockRightId) WHERE IsC3=0;
            CREATE UNIQUE INDEX UX_TripManifestLine_BatchRight ON dbo.TripManifestLine(TenantId,TripManifestId,StockRightId);
            CREATE INDEX IX_TripManifestLine_RightModel ON dbo.TripManifestLine(TenantId,StockRightId,IsC3) INCLUDE(LoadedBaseQuantity);
            ALTER TABLE dbo.TripManifestLine ADD CONSTRAINT FK_TripManifestLine_Model
                FOREIGN KEY(TenantId,TripManifestId,IsC3) REFERENCES dbo.TripManifest(TenantId,Id,IsC3);
            ALTER TABLE dbo.TripManifestLine ADD CONSTRAINT CK_TripManifestLine_C3Right CHECK(IsC3=0 OR RightKind='A');
            """);
    }

    public override void Down() => throw new NotSupportedException("Forward-only application migration.");
}
