using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(202611220009, "APP-02D.3 trip cashier, scoped command and fixture pricing provenance")]
public sealed class App02d3Schema : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE dbo.D3CashierTrip (
                TenantId nvarchar(64) NOT NULL,
                TripId uniqueidentifier NOT NULL,
                ActorId nvarchar(128) NOT NULL,
                CONSTRAINT PK_D3CashierTrip PRIMARY KEY (TenantId,TripId),
                CONSTRAINT FK_D3CashierTrip_Trip FOREIGN KEY (TenantId,TripId) REFERENCES dbo.Trip(TenantId,Id),
                CONSTRAINT CK_D3CashierTrip_Actor CHECK (LEN(LTRIM(RTRIM(ActorId)))>0)
            );

            CREATE TABLE dbo.D3CommandScope (
                TenantId nvarchar(64) NOT NULL,
                CommandRowId uniqueidentifier NOT NULL,
                TripId uniqueidentifier NOT NULL,
                RoleCode nvarchar(16) NOT NULL,
                CONSTRAINT PK_D3CommandScope PRIMARY KEY (TenantId,CommandRowId),
                CONSTRAINT FK_D3CommandScope_Command FOREIGN KEY (TenantId,CommandRowId)
                    REFERENCES dbo.BusinessCommand(TenantId,Id),
                CONSTRAINT FK_D3CommandScope_Trip FOREIGN KEY (TenantId,TripId)
                    REFERENCES dbo.D3CashierTrip(TenantId,TripId),
                CONSTRAINT CK_D3CommandScope_Role CHECK (RoleCode IN (N'sales',N'warehouse',N'cashier'))
            );

            CREATE TABLE dbo.D3PriceProfile (
                TenantId nvarchar(64) NOT NULL,
                TripId uniqueidentifier NOT NULL,
                SkuId uniqueidentifier NOT NULL,
                PolicyVersion int NOT NULL,
                UnitPrice decimal(19,4) NOT NULL,
                Decision nvarchar(16) NOT NULL,
                EffectiveFromUtc datetime2(7) NOT NULL,
                EffectiveUntilUtc datetime2(7) NOT NULL,
                ApprovedAtUtc datetime2(7) NULL,
                Maker nvarchar(128) NOT NULL,
                Checker nvarchar(128) NULL,
                EvidenceRef nvarchar(128) NULL,
                CONSTRAINT PK_D3PriceProfile PRIMARY KEY (TenantId,TripId,SkuId,PolicyVersion),
                CONSTRAINT UQ_D3PriceProfile_Price UNIQUE (TenantId,TripId,SkuId,PolicyVersion,UnitPrice),
                CONSTRAINT FK_D3PriceProfile_Trip FOREIGN KEY (TenantId,TripId)
                    REFERENCES dbo.D3CashierTrip(TenantId,TripId),
                CONSTRAINT FK_D3PriceProfile_Sku FOREIGN KEY (TenantId,SkuId) REFERENCES dbo.Sku(TenantId,Id),
                CONSTRAINT CK_D3PriceProfile CHECK (PolicyVersion>0 AND UnitPrice>0
                    AND Decision IN (N'APPROVED',N'PENDING',N'REJECTED')
                    AND EffectiveFromUtc<EffectiveUntilUtc)
            );

            ALTER TABLE dbo.SaleDelivery ADD CONSTRAINT UQ_D3SaleDeliveryScope
                UNIQUE (TenantId,Id,TripId,SkuId);

            CREATE TABLE dbo.D3SalePricing (
                TenantId nvarchar(64) NOT NULL,
                SaleDeliveryId uniqueidentifier NOT NULL,
                TripId uniqueidentifier NOT NULL,
                SkuId uniqueidentifier NOT NULL,
                PolicyVersion int NOT NULL,
                UnitPrice decimal(19,4) NOT NULL,
                CONSTRAINT PK_D3SalePricing PRIMARY KEY (TenantId,SaleDeliveryId),
                CONSTRAINT FK_D3SalePricing_Delivery FOREIGN KEY (TenantId,SaleDeliveryId,TripId,SkuId)
                    REFERENCES dbo.SaleDelivery(TenantId,Id,TripId,SkuId),
                CONSTRAINT FK_D3SalePricing_Profile FOREIGN KEY (TenantId,TripId,SkuId,PolicyVersion,UnitPrice)
                    REFERENCES dbo.D3PriceProfile(TenantId,TripId,SkuId,PolicyVersion,UnitPrice)
            );
            """);

        foreach (string table in new[] { "D3CommandScope", "D3PriceProfile", "D3SalePricing" })
            Execute.Sql($"""
                CREATE TRIGGER dbo.TR_{table}_Immutable ON dbo.{table}
                AFTER UPDATE, DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS(SELECT 1 FROM deleted)
                        THROW 51044, 'D.3 provenance is immutable.', 1;
                END;
                """);
    }

    public override void Down() => throw new NotSupportedException("Forward-only application migration.");
}
