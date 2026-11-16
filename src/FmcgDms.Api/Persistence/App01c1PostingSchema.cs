using FluentMigrator;

namespace FmcgDms.Api.Persistence;

/// <summary>
/// APP-01C.1: schema-only source-bound posting evidence for T3 B20/T4 A25.
/// No delivery, receipt, AR, COGS or entitlement consumption is posted here.
/// APP-01C.2/.3 must write all required facts and ledger effects in ONE
/// explicit serializable transaction. These are synthetic, unitless records.
/// </summary>
[Migration(202611160005, "APP-01C.1 synthetic sale and delivery source-ledger schema")]
public sealed class App01c1PostingSchema : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            -- Composite identities prevent attaching a posted record to the
            -- right SKU but the wrong trip/customer/order/source.
            ALTER TABLE dbo.AOrder ADD CONSTRAINT UQ_AOrder_DeliveryScope
                UNIQUE (TenantId, Id, TripId, CustomerId, ShipToSnapshotId);
            ALTER TABLE dbo.AOrder ADD CONSTRAINT UQ_AOrder_ConfirmScope
                UNIQUE (TenantId, Id, TripId, CustomerId);
            ALTER TABLE dbo.AOrderLine ADD CONSTRAINT UQ_AOrderLine_DeliveryScope
                UNIQUE (TenantId, Id, AOrderId, SkuId);
            ALTER TABLE dbo.BusinessSource ADD CONSTRAINT UQ_BusinessSource_PostScope
                UNIQUE (TenantId, Id, TripId, CustomerId, SourceKind);
            ALTER TABLE dbo.BusinessSourceLine ADD CONSTRAINT UQ_BusinessSourceLine_PostScope
                UNIQUE (TenantId, Id, BusinessSourceId, SkuId, BaseQuantity, Amount);
            ALTER TABLE dbo.BusinessSourceLine ADD CONSTRAINT UQ_BusinessSourceLine_ConfirmScope
                UNIQUE (TenantId, Id, BusinessSourceId, SkuId, BaseQuantity);
            ALTER TABLE dbo.TripManifestLine ADD CONSTRAINT UQ_TripManifestLine_PostScope
                UNIQUE (TenantId, Id, TripId, SkuId, StockRightId, RightKind, LoadSourceLineId);
            ALTER TABLE dbo.BCreditGrant ADD CONSTRAINT UQ_BCreditGrant_ConsumeScope
                UNIQUE (TenantId, Id, TripId, CustomerId, PoolCode, CurrencyUnit);

            -- Price and N-day terms must be frozen by ConfirmAOrder in C.3.
            -- Existing synthetic confirmations are NOT silently backfilled.
            CREATE TABLE dbo.AConfirmedLineSnapshot (
                TenantId nvarchar(64) NOT NULL,
                AOrderLineId uniqueidentifier NOT NULL,
                AOrderId uniqueidentifier NOT NULL,
                TripId uniqueidentifier NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                SkuId uniqueidentifier NOT NULL,
                ConfirmBusinessSourceId uniqueidentifier NOT NULL,
                ConfirmSourceKind nvarchar(48) NOT NULL
                    CONSTRAINT DF_AConfirmedLineSnapshot_Kind DEFAULT (N'A_CONFIRM'),
                ConfirmSourceLineId uniqueidentifier NOT NULL,
                ConfirmedBaseQuantity decimal(18,3) NOT NULL,
                UnitPriceSnapshot decimal(19,4) NOT NULL,
                TermsDaysSnapshot int NOT NULL,
                ConfirmedAtUtc datetime2(7) NOT NULL
                    CONSTRAINT DF_AConfirmedLineSnapshot_At DEFAULT (SYSUTCDATETIME()),
                RowVersion rowversion NOT NULL,
                CONSTRAINT PK_AConfirmedLineSnapshot PRIMARY KEY (TenantId, AOrderLineId),
                CONSTRAINT UQ_AConfirmedLineSnapshot_Source UNIQUE
                    (TenantId, ConfirmSourceLineId),
                CONSTRAINT UQ_AConfirmedLineSnapshot_DeliveryScope UNIQUE
                    (TenantId, AOrderLineId, AOrderId, SkuId),
                CONSTRAINT FK_AConfirmedLineSnapshot_Order FOREIGN KEY
                    (TenantId, AOrderId, TripId, CustomerId)
                    REFERENCES dbo.AOrder (TenantId, Id, TripId, CustomerId),
                CONSTRAINT FK_AConfirmedLineSnapshot_Line FOREIGN KEY
                    (TenantId, AOrderLineId, AOrderId, SkuId)
                    REFERENCES dbo.AOrderLine (TenantId, Id, AOrderId, SkuId),
                CONSTRAINT FK_AConfirmedLineSnapshot_Source FOREIGN KEY
                    (TenantId, ConfirmBusinessSourceId, TripId, CustomerId, ConfirmSourceKind)
                    REFERENCES dbo.BusinessSource (TenantId, Id, TripId, CustomerId, SourceKind),
                CONSTRAINT FK_AConfirmedLineSnapshot_SourceLine FOREIGN KEY
                    (TenantId, ConfirmSourceLineId, ConfirmBusinessSourceId,
                     SkuId, ConfirmedBaseQuantity)
                    REFERENCES dbo.BusinessSourceLine
                        (TenantId, Id, BusinessSourceId, SkuId, BaseQuantity),
                CONSTRAINT CK_AConfirmedLineSnapshot_Valid CHECK (
                    ConfirmSourceKind = N'A_CONFIRM'
                    AND ConfirmedBaseQuantity > 0 AND UnitPriceSnapshot >= 0
                    AND TermsDaysSnapshot >= 0)
            );

            -- One posted commercial source-line per delivery; no provisional
            -- delivery state that could be mistaken for an applied posting.
            CREATE TABLE dbo.SaleDelivery (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_SaleDelivery PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                TripId uniqueidentifier NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                ShipToSnapshotId uniqueidentifier NOT NULL,
                TripManifestLineId uniqueidentifier NOT NULL,
                LoadSourceLineId uniqueidentifier NOT NULL,
                StockRightId uniqueidentifier NOT NULL,
                RightKind char(1) NOT NULL,
                SkuId uniqueidentifier NOT NULL,
                AOrderId uniqueidentifier NULL,
                AOrderLineId uniqueidentifier NULL,
                BusinessSourceId uniqueidentifier NOT NULL,
                BusinessSourceKind nvarchar(48) NOT NULL,
                BusinessSourceLineId uniqueidentifier NOT NULL,
                BaseQuantity decimal(18,3) NOT NULL,
                PayableAmount decimal(19,4) NOT NULL,
                CostAmount decimal(19,4) NOT NULL,
                CostBasisVersion nvarchar(32) NOT NULL,
                BusinessDate date NOT NULL,
                InvoiceEligible bit NOT NULL
                    CONSTRAINT DF_SaleDelivery_InvoiceEligible DEFAULT (0),
                PostedAtUtc datetime2(7) NOT NULL
                    CONSTRAINT DF_SaleDelivery_Posted DEFAULT (SYSUTCDATETIME()),
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_SaleDelivery_Tenant_Id UNIQUE (TenantId, Id),
                CONSTRAINT UQ_SaleDelivery_SourceLine UNIQUE (TenantId, BusinessSourceLineId),
                CONSTRAINT UQ_SaleDelivery_StockIssue UNIQUE
                    (TenantId, Id, BusinessSourceLineId, TripManifestLineId,
                     SkuId, BaseQuantity, CostAmount),
                CONSTRAINT UQ_SaleDelivery_OpenItem UNIQUE
                    (TenantId, Id, CustomerId, BusinessSourceLineId, PayableAmount, RightKind),
                CONSTRAINT UQ_SaleDelivery_Receipt UNIQUE
                    (TenantId, Id, TripId, CustomerId, BusinessSourceLineId, RightKind),
                CONSTRAINT UQ_SaleDelivery_BConsume UNIQUE
                    (TenantId, Id, TripId, CustomerId, RightKind),
                CONSTRAINT FK_SaleDelivery_CustomerShipTo FOREIGN KEY
                    (TenantId, CustomerId, ShipToSnapshotId)
                    REFERENCES dbo.ShipToSnapshot (TenantId, CustomerId, Id),
                CONSTRAINT FK_SaleDelivery_LoadedManifestLine FOREIGN KEY
                    (TenantId, TripManifestLineId, TripId, SkuId, StockRightId,
                     RightKind, LoadSourceLineId)
                    REFERENCES dbo.TripManifestLine
                        (TenantId, Id, TripId, SkuId, StockRightId,
                         RightKind, LoadSourceLineId),
                CONSTRAINT FK_SaleDelivery_OriginSource FOREIGN KEY
                    (TenantId, BusinessSourceId, TripId, CustomerId, BusinessSourceKind)
                    REFERENCES dbo.BusinessSource
                        (TenantId, Id, TripId, CustomerId, SourceKind),
                CONSTRAINT FK_SaleDelivery_ExactSourceLine FOREIGN KEY
                    (TenantId, BusinessSourceLineId, BusinessSourceId,
                     SkuId, BaseQuantity, PayableAmount)
                    REFERENCES dbo.BusinessSourceLine
                        (TenantId, Id, BusinessSourceId, SkuId, BaseQuantity, Amount),
                CONSTRAINT FK_SaleDelivery_AOrder FOREIGN KEY
                    (TenantId, AOrderId, TripId, CustomerId, ShipToSnapshotId)
                    REFERENCES dbo.AOrder
                        (TenantId, Id, TripId, CustomerId, ShipToSnapshotId),
                CONSTRAINT FK_SaleDelivery_AOrderLine FOREIGN KEY
                    (TenantId, AOrderLineId, AOrderId, SkuId)
                    REFERENCES dbo.AOrderLine (TenantId, Id, AOrderId, SkuId),
                CONSTRAINT FK_SaleDelivery_FrozenAConfirm FOREIGN KEY
                    (TenantId, AOrderLineId, AOrderId, SkuId)
                    REFERENCES dbo.AConfirmedLineSnapshot
                        (TenantId, AOrderLineId, AOrderId, SkuId),
                CONSTRAINT CK_SaleDelivery_Mode CHECK (
                    (RightKind='A' AND BusinessSourceKind=N'A_DELIVERY'
                     AND AOrderId IS NOT NULL AND AOrderLineId IS NOT NULL)
                    OR (RightKind='B' AND BusinessSourceKind=N'B_SALE_DELIVERY'
                        AND AOrderId IS NULL AND AOrderLineId IS NULL)),
                CONSTRAINT CK_SaleDelivery_Values CHECK (
                    BaseQuantity > 0 AND PayableAmount >= 0 AND CostAmount >= 0
                    AND CostBasisVersion = N'SYNTHETIC_MA_V1'
                    AND InvoiceEligible = 0)
            );
            CREATE INDEX IX_SaleDelivery_Trip_Right
                ON dbo.SaleDelivery (TenantId, TripId, StockRightId, BusinessDate);

            -- Append-only outward stock issue: it inherits exact quantity/cost,
            -- SKU and source from the posted delivery. Load movements remain
            -- separate paired warehouse-to-vehicle transfers.
            CREATE TABLE dbo.DeliveryStockIssue (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_DeliveryStockIssue PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                SaleDeliveryId uniqueidentifier NOT NULL,
                BusinessSourceLineId uniqueidentifier NOT NULL,
                TripManifestLineId uniqueidentifier NOT NULL,
                SkuId uniqueidentifier NOT NULL,
                BaseQuantity decimal(18,3) NOT NULL,
                CostAmount decimal(19,4) NOT NULL,
                RecordedAtUtc datetime2(7) NOT NULL
                    CONSTRAINT DF_DeliveryStockIssue_At DEFAULT (SYSUTCDATETIME()),
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_DeliveryStockIssue_Delivery UNIQUE (TenantId, SaleDeliveryId),
                CONSTRAINT UQ_DeliveryStockIssue_Source UNIQUE (TenantId, BusinessSourceLineId),
                CONSTRAINT FK_DeliveryStockIssue_ExactDelivery FOREIGN KEY
                    (TenantId, SaleDeliveryId, BusinessSourceLineId, TripManifestLineId,
                     SkuId, BaseQuantity, CostAmount)
                    REFERENCES dbo.SaleDelivery
                        (TenantId, Id, BusinessSourceLineId, TripManifestLineId,
                         SkuId, BaseQuantity, CostAmount),
                CONSTRAINT CK_DeliveryStockIssue_Values CHECK
                    (BaseQuantity > 0 AND CostAmount >= 0)
            );

            -- Gross AR is created by delivery, with cash allocations applied
            -- separately in the same B transaction; persisted net AR is derived.
            CREATE TABLE dbo.ReceivableOpenItem (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_ReceivableOpenItem PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                SaleDeliveryId uniqueidentifier NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                BusinessSourceLineId uniqueidentifier NOT NULL,
                RightKind char(1) NOT NULL,
                OriginalAmount decimal(19,4) NOT NULL,
                AppliedAmount decimal(19,4) NOT NULL
                    CONSTRAINT DF_ReceivableOpenItem_Applied DEFAULT (0),
                OutstandingAmount AS (OriginalAmount - AppliedAmount) PERSISTED,
                DueBusinessDate date NULL,
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_ReceivableOpenItem_Delivery UNIQUE (TenantId, SaleDeliveryId),
                CONSTRAINT UQ_ReceivableOpenItem_Customer UNIQUE (TenantId, Id, CustomerId),
                CONSTRAINT FK_ReceivableOpenItem_ExactDelivery FOREIGN KEY
                    (TenantId, SaleDeliveryId, CustomerId,
                     BusinessSourceLineId, OriginalAmount, RightKind)
                    REFERENCES dbo.SaleDelivery
                        (TenantId, Id, CustomerId,
                         BusinessSourceLineId, PayableAmount, RightKind),
                CONSTRAINT CK_ReceivableOpenItem_Values CHECK (
                    OriginalAmount >= 0 AND AppliedAmount >= 0
                    AND AppliedAmount <= OriginalAmount
                    AND (RightKind='B' OR (RightKind='A' AND DueBusinessDate IS NOT NULL)))
            );

            -- Only confirmed physical CASH with evidence may create a receipt.
            -- An offered amount or unverified bank screenshot is not a receipt.
            CREATE TABLE dbo.CashReceipt (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_CashReceipt PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                SaleDeliveryId uniqueidentifier NOT NULL,
                TripId uniqueidentifier NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                BusinessSourceLineId uniqueidentifier NOT NULL,
                RightKind char(1) NOT NULL CONSTRAINT DF_CashReceipt_Kind DEFAULT ('B'),
                PaymentMethod nvarchar(16) NOT NULL CONSTRAINT DF_CashReceipt_Method DEFAULT (N'CASH'),
                VerificationStatus nvarchar(16) NOT NULL
                    CONSTRAINT DF_CashReceipt_Verification DEFAULT (N'VERIFIED'),
                VerificationEvidenceRef nvarchar(128) NOT NULL,
                ReceivedAmount decimal(19,4) NOT NULL,
                AppliedAmount decimal(19,4) NOT NULL
                    CONSTRAINT DF_CashReceipt_Applied DEFAULT (0),
                VerifiedAtUtc datetime2(7) NOT NULL,
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_CashReceipt_Delivery UNIQUE (TenantId, SaleDeliveryId),
                CONSTRAINT UQ_CashReceipt_Customer UNIQUE (TenantId, Id, CustomerId),
                CONSTRAINT FK_CashReceipt_BDelivery FOREIGN KEY
                    (TenantId, SaleDeliveryId, TripId, CustomerId,
                     BusinessSourceLineId, RightKind)
                    REFERENCES dbo.SaleDelivery
                        (TenantId, Id, TripId, CustomerId,
                         BusinessSourceLineId, RightKind),
                CONSTRAINT CK_CashReceipt_Valid CHECK (
                    RightKind = 'B' AND PaymentMethod=N'CASH'
                    AND VerificationStatus=N'VERIFIED'
                    AND LEN(LTRIM(RTRIM(VerificationEvidenceRef))) > 0
                    AND ReceivedAmount > 0 AND AppliedAmount >= 0
                    AND AppliedAmount <= ReceivedAmount)
            );

            CREATE TABLE dbo.ReceiptAllocation (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_ReceiptAllocation PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                CashReceiptId uniqueidentifier NOT NULL,
                ReceivableOpenItemId uniqueidentifier NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                AppliedAmount decimal(19,4) NOT NULL,
                RecordedAtUtc datetime2(7) NOT NULL
                    CONSTRAINT DF_ReceiptAllocation_At DEFAULT (SYSUTCDATETIME()),
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_ReceiptAllocation_Pair UNIQUE
                    (TenantId, CashReceiptId, ReceivableOpenItemId),
                CONSTRAINT FK_ReceiptAllocation_Cash FOREIGN KEY
                    (TenantId, CashReceiptId, CustomerId)
                    REFERENCES dbo.CashReceipt (TenantId, Id, CustomerId),
                CONSTRAINT FK_ReceiptAllocation_OpenItem FOREIGN KEY
                    (TenantId, ReceivableOpenItemId, CustomerId)
                    REFERENCES dbo.ReceivableOpenItem (TenantId, Id, CustomerId),
                CONSTRAINT CK_ReceiptAllocation_Amount CHECK (AppliedAmount > 0)
            );

            -- B grant consumption must identify the same Trip and Customer as
            -- the B delivery and source grant; no A delivery may consume B.
            CREATE TABLE dbo.BGrantConsumption (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_BGrantConsumption PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                SaleDeliveryId uniqueidentifier NOT NULL,
                BCreditGrantId uniqueidentifier NOT NULL,
                TripId uniqueidentifier NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                RightKind char(1) NOT NULL CONSTRAINT DF_BGrantConsumption_Kind DEFAULT ('B'),
                PoolCode nvarchar(48) NOT NULL CONSTRAINT DF_BGrantConsumption_Pool DEFAULT (N'B'),
                CurrencyUnit nvarchar(32) NOT NULL
                    CONSTRAINT DF_BGrantConsumption_Unit DEFAULT (N'SYNTHETIC_UNITLESS'),
                ConsumedAmount decimal(19,4) NOT NULL,
                RecordedAtUtc datetime2(7) NOT NULL
                    CONSTRAINT DF_BGrantConsumption_At DEFAULT (SYSUTCDATETIME()),
                RowVersion rowversion NOT NULL,
                -- Multiple source grants may fund a B sale, but never repeat
                -- the same grant/delivery consumption pair.
                CONSTRAINT UQ_BGrantConsumption_GrantDelivery UNIQUE
                    (TenantId, BCreditGrantId, SaleDeliveryId),
                CONSTRAINT FK_BGrantConsumption_BDelivery FOREIGN KEY
                    (TenantId, SaleDeliveryId, TripId, CustomerId, RightKind)
                    REFERENCES dbo.SaleDelivery
                        (TenantId, Id, TripId, CustomerId, RightKind),
                CONSTRAINT FK_BGrantConsumption_Grant FOREIGN KEY
                    (TenantId, BCreditGrantId, TripId, CustomerId, PoolCode, CurrencyUnit)
                    REFERENCES dbo.BCreditGrant
                        (TenantId, Id, TripId, CustomerId, PoolCode, CurrencyUnit),
                CONSTRAINT CK_BGrantConsumption_Amount CHECK (
                    RightKind='B' AND PoolCode=N'B'
                    AND CurrencyUnit=N'SYNTHETIC_UNITLESS'
                    AND ConsumedAmount > 0)
            );
            """);

        Execute.Sql("""
            CREATE TRIGGER dbo.TR_AConfirmedLineSnapshot_Immutable
            ON dbo.AConfirmedLineSnapshot AFTER UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM deleted)
                    THROW 51061, 'APP01C1 A confirm snapshot is immutable', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_SaleDelivery_Immutable
            ON dbo.SaleDelivery AFTER UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM deleted)
                    THROW 51062, 'APP01C1 posted delivery is immutable', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_DeliveryStockIssue_Immutable
            ON dbo.DeliveryStockIssue AFTER UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM deleted)
                    THROW 51063, 'APP01C1 stock issue is immutable', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_ReceiptAllocation_Immutable
            ON dbo.ReceiptAllocation AFTER UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM deleted)
                    THROW 51064, 'APP01C1 receipt allocation is immutable', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_BGrantConsumption_Immutable
            ON dbo.BGrantConsumption AFTER UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM deleted)
                    THROW 51065, 'APP01C1 B grant consumption is immutable', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_CashReceipt_VerifiedImmutable
            ON dbo.CashReceipt AFTER UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
                    THROW 51066, 'APP01C1 verified receipt cannot be deleted', 1;
                IF UPDATE(TenantId) OR UPDATE(SaleDeliveryId) OR UPDATE(TripId)
                    OR UPDATE(CustomerId) OR UPDATE(BusinessSourceLineId)
                    OR UPDATE(RightKind) OR UPDATE(PaymentMethod)
                    OR UPDATE(VerificationStatus) OR UPDATE(VerificationEvidenceRef)
                    OR UPDATE(ReceivedAmount) OR UPDATE(VerifiedAtUtc)
                    THROW 51066, 'APP01C1 verified receipt evidence is immutable', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_ReceivableOpenItem_SourceImmutable
            ON dbo.ReceivableOpenItem AFTER UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
                    THROW 51067, 'APP01C1 AR open item cannot be deleted', 1;
                IF UPDATE(TenantId) OR UPDATE(SaleDeliveryId) OR UPDATE(CustomerId)
                    OR UPDATE(BusinessSourceLineId) OR UPDATE(RightKind)
                    OR UPDATE(OriginalAmount) OR UPDATE(DueBusinessDate)
                    THROW 51067, 'APP01C1 AR source and due snapshot are immutable', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_LedgerEffect_PostedDeliveryImmutable
            ON dbo.LedgerEffect AFTER UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (
                    SELECT 1 FROM deleted d
                    JOIN dbo.BusinessSourceLine l ON l.TenantId=d.TenantId
                        AND l.Id=d.BusinessSourceLineId
                    JOIN dbo.SaleDelivery s ON s.TenantId=l.TenantId
                        AND s.BusinessSourceLineId=l.Id
                )
                    THROW 51068, 'APP01C1 posted delivery ledger cannot be rewritten', 1;
            END;
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DROP TRIGGER IF EXISTS dbo.TR_LedgerEffect_PostedDeliveryImmutable;
            DROP TRIGGER IF EXISTS dbo.TR_ReceivableOpenItem_SourceImmutable;
            DROP TRIGGER IF EXISTS dbo.TR_CashReceipt_VerifiedImmutable;
            DROP TRIGGER IF EXISTS dbo.TR_BGrantConsumption_Immutable;
            DROP TRIGGER IF EXISTS dbo.TR_ReceiptAllocation_Immutable;
            DROP TRIGGER IF EXISTS dbo.TR_DeliveryStockIssue_Immutable;
            DROP TRIGGER IF EXISTS dbo.TR_SaleDelivery_Immutable;
            DROP TRIGGER IF EXISTS dbo.TR_AConfirmedLineSnapshot_Immutable;
            DROP TABLE dbo.BGrantConsumption;
            DROP TABLE dbo.ReceiptAllocation;
            DROP TABLE dbo.CashReceipt;
            DROP TABLE dbo.ReceivableOpenItem;
            DROP TABLE dbo.DeliveryStockIssue;
            DROP TABLE dbo.SaleDelivery;
            DROP TABLE dbo.AConfirmedLineSnapshot;
            ALTER TABLE dbo.BCreditGrant DROP CONSTRAINT UQ_BCreditGrant_ConsumeScope;
            ALTER TABLE dbo.TripManifestLine DROP CONSTRAINT UQ_TripManifestLine_PostScope;
            ALTER TABLE dbo.BusinessSourceLine DROP CONSTRAINT UQ_BusinessSourceLine_ConfirmScope;
            ALTER TABLE dbo.BusinessSourceLine DROP CONSTRAINT UQ_BusinessSourceLine_PostScope;
            ALTER TABLE dbo.BusinessSource DROP CONSTRAINT UQ_BusinessSource_PostScope;
            ALTER TABLE dbo.AOrderLine DROP CONSTRAINT UQ_AOrderLine_DeliveryScope;
            ALTER TABLE dbo.AOrder DROP CONSTRAINT UQ_AOrder_ConfirmScope;
            ALTER TABLE dbo.AOrder DROP CONSTRAINT UQ_AOrder_DeliveryScope;
            """);
    }
}
