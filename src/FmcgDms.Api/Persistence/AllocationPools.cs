using FluentMigrator;

namespace FmcgDms.Api.Persistence;

/// <summary>
/// APP-01A.2 synthetic opening snapshots. Both A and B allocate from the same
/// warehouse/SKU pool; these are rights, not physical stock movements.
/// </summary>
[Migration(202611160002, "APP-01A.2 stock and synthetic B credit allocation pools")]
public sealed class AllocationPools : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE dbo.StockPool (
                TenantId nvarchar(64) NOT NULL,
                StockLocationId uniqueidentifier NOT NULL,
                SkuId uniqueidentifier NOT NULL,
                OnHandBaseQuantity decimal(18,3) NOT NULL,
                AllocatedBaseQuantity decimal(18,3) NOT NULL
                    CONSTRAINT DF_StockPool_Allocated DEFAULT (0),
                RowVersion rowversion NOT NULL,
                CONSTRAINT PK_StockPool PRIMARY KEY (TenantId, StockLocationId, SkuId),
                CONSTRAINT FK_StockPool_Location FOREIGN KEY (TenantId, StockLocationId)
                    REFERENCES dbo.StockLocation (TenantId, Id),
                CONSTRAINT FK_StockPool_Sku FOREIGN KEY (TenantId, SkuId)
                    REFERENCES dbo.Sku (TenantId, Id),
                CONSTRAINT CK_StockPool_Amounts CHECK (
                    OnHandBaseQuantity >= 0 AND AllocatedBaseQuantity >= 0
                    AND AllocatedBaseQuantity <= OnHandBaseQuantity)
            );

            -- A synthetic, explicitly seeded policy capacity, not an implicit real
            -- customer credit approval. No pool row = no grant authority.
            CREATE TABLE dbo.BCreditPool (
                TenantId nvarchar(64) NOT NULL,
                TripId uniqueidentifier NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                PoolCode nvarchar(48) NOT NULL,
                CurrencyUnit nvarchar(32) NOT NULL,
                ApprovedLimit decimal(19,4) NOT NULL,
                GrantedAmount decimal(19,4) NOT NULL
                    CONSTRAINT DF_BCreditPool_Granted DEFAULT (0),
                RowVersion rowversion NOT NULL,
                CONSTRAINT PK_BCreditPool PRIMARY KEY
                    (TenantId, TripId, CustomerId, PoolCode, CurrencyUnit),
                CONSTRAINT FK_BCreditPool_Trip FOREIGN KEY (TenantId, TripId)
                    REFERENCES dbo.Trip (TenantId, Id),
                CONSTRAINT FK_BCreditPool_Customer FOREIGN KEY (TenantId, CustomerId)
                    REFERENCES dbo.Customer (TenantId, Id),
                CONSTRAINT CK_BCreditPool_Amounts CHECK (
                    ApprovedLimit >= 0 AND GrantedAmount >= 0
                    AND GrantedAmount <= ApprovedLimit)
            );
            """);
    }

    public override void Down()
    {
        Delete.Table("BCreditPool").InSchema("dbo");
        Delete.Table("StockPool").InSchema("dbo");
    }
}
