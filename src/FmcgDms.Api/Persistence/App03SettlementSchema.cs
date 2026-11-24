using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(Version, "APP-03.6 settlement bootstrap and lot return provenance")]
[FluentMigrator.Tags(App03A1Profile.SettlementName)]
public sealed class App03SettlementSchema : Migration
{
    internal const long Version = 202611240002;

    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE dbo.App03SettlementDay (
                BusinessDate date NOT NULL PRIMARY KEY
            );

            CREATE TABLE dbo.App03LotUnload (
                TenantId nvarchar(64) NOT NULL,
                ManifestLineId uniqueidentifier NOT NULL,
                LotId uniqueidentifier NOT NULL,
                Quantity decimal(18,3) NOT NULL CHECK(Quantity>0),
                CONSTRAINT PK_App03LotUnload PRIMARY KEY(TenantId,ManifestLineId,LotId),
                CONSTRAINT FK_App03LotUnload_Load FOREIGN KEY(TenantId,ManifestLineId,LotId)
                    REFERENCES dbo.F03LotLoad(TenantId,ManifestLineId,LotId),
                CONSTRAINT FK_App03LotUnload_Unload FOREIGN KEY(TenantId,ManifestLineId)
                    REFERENCES dbo.D3TripUnloadLine(TenantId,TripManifestLineId)
            );
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_App03LotUnload_Immutable ON dbo.App03LotUnload
            INSTEAD OF UPDATE, DELETE AS
                THROW 51706, 'APP-03 returned lot evidence is immutable.', 1;
            """);
    }

    public override void Down() => throw new NotSupportedException("Use the nonce-owned scratch reset.");
}
