using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(202611220007, "APP-02D.1 B rights in partial manifest batches")]
public sealed class App02d1LoadSchema : Migration
{
    public override void Up()
    {
        Execute.Sql("ALTER TABLE dbo.C2CommandScope ADD IsD1Load bit NOT NULL CONSTRAINT DF_C2CommandScope_D1Load DEFAULT(0);");
        Execute.Sql("""
            ALTER TABLE dbo.C2CommandScope ADD CONSTRAINT CK_C2CommandScope_D1Load
                CHECK(IsD1Load=0 OR (RoleCode=N'warehouse' AND LoadTripId IS NOT NULL
                    AND DeliveryTripId IS NULL AND D1GrantTripId IS NULL));
            ALTER TABLE dbo.TripManifestLine DROP CONSTRAINT CK_TripManifestLine_C3Right;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_TripManifestLine_D1QuotaSource ON dbo.TripManifestLine
            AFTER INSERT, UPDATE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS(
                    SELECT 1
                    FROM inserted i
                    WHERE i.IsC3=1 AND i.RightKind='B' AND NOT EXISTS(
                        SELECT 1
                        FROM dbo.StockRight r
                        JOIN dbo.BQuota q ON q.TenantId=r.TenantId AND q.StockRightId=r.Id
                        JOIN dbo.BusinessSourceLine sl ON sl.TenantId=r.TenantId AND sl.Id=r.BusinessSourceLineId
                        JOIN dbo.BusinessSource bs ON bs.TenantId=sl.TenantId AND bs.Id=sl.BusinessSourceId
                        JOIN dbo.BusinessCommand cm ON cm.TenantId=bs.TenantId AND cm.Id=bs.BusinessCommandId
                        JOIN dbo.C2CommandScope scope ON scope.TenantId=cm.TenantId AND scope.CommandRowId=cm.Id
                        WHERE r.TenantId=i.TenantId AND r.Id=i.StockRightId
                            AND bs.SourceKind='STOCK_ALLOC' AND bs.TripId=i.TripId
                            AND cm.Status='APPLIED' AND scope.D1GrantTripId=i.TripId AND scope.RoleCode='warehouse'))
                    THROW 51041, 'Partial B manifest requires a committed D.1 quota source.', 1;
            END;
            """);
    }

    public override void Down() => throw new NotSupportedException("Forward-only application migration.");
}
