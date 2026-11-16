using FluentMigrator;

namespace FmcgDms.Api.Persistence;

/// <summary>
/// APP-01A.1: one integrated, source-traceable schema per tenant database.
/// Schema-level guards only; command/stock-right aggregation is APP-01A.2.
/// </summary>
[Migration(202611160001, "APP-01A.1 integrated tenant, source, order and rights schema")]
public sealed class IntegratedSchema : Migration
{
    public override void Up()
    {
        // The existing one-row tenant binding is the DB-enforced authority for every row.
        // This migration is applied only through the nonce-guarded APP-00A LocalDB path.
        Execute.Sql("""
            ALTER TABLE dbo.AppTenantBinding
                ADD CONSTRAINT UQ_AppTenantBinding_TenantId UNIQUE (TenantId);

            CREATE TABLE dbo.Customer (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_Customer PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                Code nvarchar(32) NOT NULL,
                Name nvarchar(256) NOT NULL,
                IsActive bit NOT NULL CONSTRAINT DF_Customer_IsActive DEFAULT (1),
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_Customer_TenantId_Id UNIQUE (TenantId, Id),
                CONSTRAINT UQ_Customer_Tenant_Code UNIQUE (TenantId, Code),
                CONSTRAINT FK_Customer_Tenant FOREIGN KEY (TenantId)
                    REFERENCES dbo.AppTenantBinding (TenantId),
                CONSTRAINT CK_Customer_Code CHECK (LEN(LTRIM(RTRIM(Code))) > 0)
            );

            CREATE TABLE dbo.ShipToSnapshot (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_ShipToSnapshot PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                DisplayName nvarchar(256) NOT NULL,
                Address nvarchar(500) NOT NULL,
                CapturedAtUtc datetime2(7) NOT NULL
                    CONSTRAINT DF_ShipToSnapshot_CapturedAt DEFAULT (SYSUTCDATETIME()),
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_ShipToSnapshot_TenantId_Id UNIQUE (TenantId, Id),
                CONSTRAINT UQ_ShipToSnapshot_Tenant_Customer_Id UNIQUE (TenantId, CustomerId, Id),
                CONSTRAINT FK_ShipToSnapshot_Customer FOREIGN KEY (TenantId, CustomerId)
                    REFERENCES dbo.Customer (TenantId, Id)
            );

            CREATE TABLE dbo.Sku (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_Sku PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                Code nvarchar(32) NOT NULL,
                Name nvarchar(256) NOT NULL,
                BaseUom nvarchar(24) NOT NULL,
                IsActive bit NOT NULL CONSTRAINT DF_Sku_IsActive DEFAULT (1),
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_Sku_TenantId_Id UNIQUE (TenantId, Id),
                CONSTRAINT UQ_Sku_Tenant_Code UNIQUE (TenantId, Code),
                CONSTRAINT FK_Sku_Tenant FOREIGN KEY (TenantId)
                    REFERENCES dbo.AppTenantBinding (TenantId),
                CONSTRAINT CK_Sku_BaseUom CHECK (LEN(LTRIM(RTRIM(BaseUom))) > 0)
            );

            CREATE TABLE dbo.StockLocation (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_StockLocation PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                Code nvarchar(32) NOT NULL,
                Kind nvarchar(16) NOT NULL,
                IsActive bit NOT NULL CONSTRAINT DF_StockLocation_IsActive DEFAULT (1),
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_StockLocation_TenantId_Id UNIQUE (TenantId, Id),
                CONSTRAINT UQ_StockLocation_Tenant_Code UNIQUE (TenantId, Code),
                CONSTRAINT FK_StockLocation_Tenant FOREIGN KEY (TenantId)
                    REFERENCES dbo.AppTenantBinding (TenantId),
                CONSTRAINT CK_StockLocation_Kind
                    CHECK (Kind IN (N'WAREHOUSE', N'VEHICLE', N'QUARANTINE'))
            );

            CREATE TABLE dbo.Trip (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_Trip PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                Code nvarchar(48) NOT NULL,
                BusinessDate date NOT NULL,
                VehicleLocationId uniqueidentifier NULL,
                Status nvarchar(32) NOT NULL
                    CONSTRAINT DF_Trip_Status DEFAULT (N'PREPARED'),
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_Trip_TenantId_Id UNIQUE (TenantId, Id),
                CONSTRAINT UQ_Trip_Tenant_Code UNIQUE (TenantId, Code),
                CONSTRAINT FK_Trip_Tenant FOREIGN KEY (TenantId)
                    REFERENCES dbo.AppTenantBinding (TenantId),
                CONSTRAINT FK_Trip_VehicleLocation FOREIGN KEY (TenantId, VehicleLocationId)
                    REFERENCES dbo.StockLocation (TenantId, Id),
                CONSTRAINT CK_Trip_Status CHECK (Status IN (
                    N'PREPARED', N'RELEASED', N'IN_PROGRESS', N'RECONCILING',
                    N'CLOSED', N'CLOSED_WITH_OPEN_CASES'))
            );

            CREATE TABLE dbo.BusinessCommand (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_BusinessCommand PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                ProducerId nvarchar(128) NOT NULL,
                CommandId nvarchar(128) NOT NULL,
                PayloadHashVersion smallint NOT NULL,
                PayloadHash binary(32) NOT NULL,
                ActorId nvarchar(128) NOT NULL,
                Status nvarchar(24) NOT NULL,
                OutcomeCode nvarchar(64) NULL,
                ReasonCode nvarchar(64) NULL,
                SubmittedAtUtc datetime2(7) NOT NULL
                    CONSTRAINT DF_BusinessCommand_SubmittedAt DEFAULT (SYSUTCDATETIME()),
                CompletedAtUtc datetime2(7) NULL,
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_BusinessCommand_TenantId_Id UNIQUE (TenantId, Id),
                CONSTRAINT UQ_BusinessCommand_Idempotency UNIQUE (TenantId, ProducerId, CommandId),
                CONSTRAINT FK_BusinessCommand_Tenant FOREIGN KEY (TenantId)
                    REFERENCES dbo.AppTenantBinding (TenantId),
                CONSTRAINT CK_BusinessCommand_HashVersion CHECK (PayloadHashVersion >= 1),
                CONSTRAINT CK_BusinessCommand_Status
                    CHECK (Status IN (N'RECEIVED', N'PENDING', N'APPLIED', N'REJECTED'))
            );

            CREATE TABLE dbo.BusinessSource (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_BusinessSource PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                BusinessCommandId uniqueidentifier NOT NULL,
                SourceKind nvarchar(48) NOT NULL,
                SourceNumber nvarchar(80) NOT NULL,
                ActorId nvarchar(128) NOT NULL,
                CustomerId uniqueidentifier NULL,
                TripId uniqueidentifier NULL,
                OccurredAtUtc datetime2(7) NOT NULL,
                RecordedAtUtc datetime2(7) NOT NULL
                    CONSTRAINT DF_BusinessSource_RecordedAt DEFAULT (SYSUTCDATETIME()),
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_BusinessSource_TenantId_Id UNIQUE (TenantId, Id),
                CONSTRAINT UQ_BusinessSource_Identity UNIQUE (TenantId, SourceKind, SourceNumber),
                CONSTRAINT FK_BusinessSource_Command FOREIGN KEY (TenantId, BusinessCommandId)
                    REFERENCES dbo.BusinessCommand (TenantId, Id),
                CONSTRAINT FK_BusinessSource_Customer FOREIGN KEY (TenantId, CustomerId)
                    REFERENCES dbo.Customer (TenantId, Id),
                CONSTRAINT FK_BusinessSource_Trip FOREIGN KEY (TenantId, TripId)
                    REFERENCES dbo.Trip (TenantId, Id)
            );

            CREATE TABLE dbo.BusinessSourceLine (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_BusinessSourceLine PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                BusinessSourceId uniqueidentifier NOT NULL,
                LineNo int NOT NULL,
                SkuId uniqueidentifier NULL,
                BaseQuantity decimal(18,3) NULL,
                Amount decimal(19,4) NULL,
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_BusinessSourceLine_TenantId_Id UNIQUE (TenantId, Id),
                CONSTRAINT UQ_BusinessSourceLine_Number UNIQUE (TenantId, BusinessSourceId, LineNo),
                CONSTRAINT FK_BusinessSourceLine_Source FOREIGN KEY (TenantId, BusinessSourceId)
                    REFERENCES dbo.BusinessSource (TenantId, Id),
                CONSTRAINT FK_BusinessSourceLine_Sku FOREIGN KEY (TenantId, SkuId)
                    REFERENCES dbo.Sku (TenantId, Id),
                CONSTRAINT CK_BusinessSourceLine_LineNo CHECK (LineNo > 0)
            );

            CREATE TABLE dbo.AOrder (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_AOrder PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                OrderNumber nvarchar(64) NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                ShipToSnapshotId uniqueidentifier NOT NULL,
                TripId uniqueidentifier NULL,
                BusinessDate date NOT NULL,
                Status nvarchar(32) NOT NULL CONSTRAINT DF_AOrder_Status DEFAULT (N'DRAFT'),
                ConfirmedAtUtc datetime2(7) NULL,
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_AOrder_TenantId_Id UNIQUE (TenantId, Id),
                CONSTRAINT UQ_AOrder_Number UNIQUE (TenantId, OrderNumber),
                CONSTRAINT FK_AOrder_CustomerShipTo FOREIGN KEY
                    (TenantId, CustomerId, ShipToSnapshotId)
                    REFERENCES dbo.ShipToSnapshot (TenantId, CustomerId, Id),
                CONSTRAINT FK_AOrder_Trip FOREIGN KEY (TenantId, TripId)
                    REFERENCES dbo.Trip (TenantId, Id),
                CONSTRAINT CK_AOrder_Status CHECK (Status IN (
                    N'DRAFT', N'CONFIRMED', N'PARTIALLY_DELIVERED',
                    N'FULFILLED', N'REMAINDER_CLOSED'))
            );

            CREATE TABLE dbo.AOrderLine (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_AOrderLine PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                AOrderId uniqueidentifier NOT NULL,
                LineNo int NOT NULL,
                SkuId uniqueidentifier NOT NULL,
                BaseQuantity decimal(18,3) NOT NULL,
                UomCode nvarchar(24) NOT NULL,
                UomToBaseFactor decimal(19,6) NOT NULL,
                UnitPrice decimal(19,4) NULL,
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_AOrderLine_TenantId_Id UNIQUE (TenantId, Id),
                CONSTRAINT UQ_AOrderLine_Number UNIQUE (TenantId, AOrderId, LineNo),
                CONSTRAINT FK_AOrderLine_Order FOREIGN KEY (TenantId, AOrderId)
                    REFERENCES dbo.AOrder (TenantId, Id),
                CONSTRAINT FK_AOrderLine_Sku FOREIGN KEY (TenantId, SkuId)
                    REFERENCES dbo.Sku (TenantId, Id),
                CONSTRAINT CK_AOrderLine_Amounts CHECK (
                    LineNo > 0 AND BaseQuantity > 0 AND UomToBaseFactor > 0
                    AND (UnitPrice IS NULL OR UnitPrice >= 0))
            );

            -- Single table of physical rights shared by A reservations and B quotas.
            -- The SUM(rights) <= stock invariant requires a locked transaction in APP-01A.2.
            CREATE TABLE dbo.StockRight (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_StockRight PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                TripId uniqueidentifier NOT NULL,
                StockLocationId uniqueidentifier NOT NULL,
                SkuId uniqueidentifier NOT NULL,
                BusinessSourceLineId uniqueidentifier NOT NULL,
                RightKind char(1) NOT NULL,
                GrantedBaseQuantity decimal(18,3) NOT NULL,
                ConsumedBaseQuantity decimal(18,3) NOT NULL
                    CONSTRAINT DF_StockRight_Consumed DEFAULT (0),
                ReleasedBaseQuantity decimal(18,3) NOT NULL
                    CONSTRAINT DF_StockRight_Released DEFAULT (0),
                Status nvarchar(24) NOT NULL CONSTRAINT DF_StockRight_Status DEFAULT (N'HELD'),
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_StockRight_TenantId_Id UNIQUE (TenantId, Id),
                CONSTRAINT UQ_StockRight_Tenant_Id_Kind UNIQUE (TenantId, Id, RightKind),
                CONSTRAINT UQ_StockRight_Source UNIQUE (TenantId, BusinessSourceLineId),
                CONSTRAINT FK_StockRight_Trip FOREIGN KEY (TenantId, TripId)
                    REFERENCES dbo.Trip (TenantId, Id),
                CONSTRAINT FK_StockRight_Location FOREIGN KEY (TenantId, StockLocationId)
                    REFERENCES dbo.StockLocation (TenantId, Id),
                CONSTRAINT FK_StockRight_Sku FOREIGN KEY (TenantId, SkuId)
                    REFERENCES dbo.Sku (TenantId, Id),
                CONSTRAINT FK_StockRight_Source FOREIGN KEY (TenantId, BusinessSourceLineId)
                    REFERENCES dbo.BusinessSourceLine (TenantId, Id),
                CONSTRAINT CK_StockRight_Kind CHECK (RightKind IN ('A', 'B')),
                CONSTRAINT CK_StockRight_Quantities CHECK (
                    GrantedBaseQuantity > 0 AND ConsumedBaseQuantity >= 0
                    AND ReleasedBaseQuantity >= 0
                    AND ConsumedBaseQuantity + ReleasedBaseQuantity <= GrantedBaseQuantity),
                CONSTRAINT CK_StockRight_Status
                    CHECK (Status IN (N'HELD', N'CONSUMED', N'RELEASED'))
            );

            CREATE TABLE dbo.AReservation (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_AReservation PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                StockRightId uniqueidentifier NOT NULL,
                RightKind char(1) NOT NULL CONSTRAINT DF_AReservation_Kind DEFAULT ('A'),
                AOrderLineId uniqueidentifier NOT NULL,
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_AReservation_TenantId_Id UNIQUE (TenantId, Id),
                CONSTRAINT UQ_AReservation_StockRight UNIQUE (TenantId, StockRightId),
                CONSTRAINT FK_AReservation_Right FOREIGN KEY (TenantId, StockRightId, RightKind)
                    REFERENCES dbo.StockRight (TenantId, Id, RightKind),
                CONSTRAINT FK_AReservation_OrderLine FOREIGN KEY (TenantId, AOrderLineId)
                    REFERENCES dbo.AOrderLine (TenantId, Id),
                CONSTRAINT CK_AReservation_Kind CHECK (RightKind = 'A')
            );

            CREATE TABLE dbo.BQuota (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_BQuota PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                StockRightId uniqueidentifier NOT NULL,
                RightKind char(1) NOT NULL CONSTRAINT DF_BQuota_Kind DEFAULT ('B'),
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_BQuota_TenantId_Id UNIQUE (TenantId, Id),
                CONSTRAINT UQ_BQuota_StockRight UNIQUE (TenantId, StockRightId),
                CONSTRAINT FK_BQuota_Right FOREIGN KEY (TenantId, StockRightId, RightKind)
                    REFERENCES dbo.StockRight (TenantId, Id, RightKind),
                CONSTRAINT CK_BQuota_Kind CHECK (RightKind = 'B')
            );

            CREATE TABLE dbo.BCreditGrant (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_BCreditGrant PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                TripId uniqueidentifier NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                ShipToSnapshotId uniqueidentifier NOT NULL,
                PoolCode nvarchar(48) NOT NULL,
                CurrencyUnit nvarchar(32) NOT NULL,
                BusinessSourceLineId uniqueidentifier NOT NULL,
                GrantedAmount decimal(19,4) NOT NULL,
                ConsumedAmount decimal(19,4) NOT NULL
                    CONSTRAINT DF_BCreditGrant_Consumed DEFAULT (0),
                ReleasedAmount decimal(19,4) NOT NULL
                    CONSTRAINT DF_BCreditGrant_Released DEFAULT (0),
                Status nvarchar(24) NOT NULL CONSTRAINT DF_BCreditGrant_Status DEFAULT (N'HELD'),
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_BCreditGrant_TenantId_Id UNIQUE (TenantId, Id),
                CONSTRAINT UQ_BCreditGrant_Source UNIQUE (TenantId, BusinessSourceLineId),
                CONSTRAINT FK_BCreditGrant_Trip FOREIGN KEY (TenantId, TripId)
                    REFERENCES dbo.Trip (TenantId, Id),
                CONSTRAINT FK_BCreditGrant_CustomerShipTo FOREIGN KEY
                    (TenantId, CustomerId, ShipToSnapshotId)
                    REFERENCES dbo.ShipToSnapshot (TenantId, CustomerId, Id),
                CONSTRAINT FK_BCreditGrant_Source FOREIGN KEY (TenantId, BusinessSourceLineId)
                    REFERENCES dbo.BusinessSourceLine (TenantId, Id),
                CONSTRAINT CK_BCreditGrant_Amounts CHECK (
                    GrantedAmount > 0 AND ConsumedAmount >= 0
                    AND ReleasedAmount >= 0
                    AND ConsumedAmount + ReleasedAmount <= GrantedAmount),
                CONSTRAINT CK_BCreditGrant_Status CHECK
                    (Status IN (N'HELD', N'CONSUMED', N'RELEASED'))
            );

            CREATE TABLE dbo.LedgerEffect (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_LedgerEffect PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                BusinessSourceLineId uniqueidentifier NOT NULL,
                EffectKey nvarchar(64) NOT NULL,
                LedgerKind nvarchar(32) NOT NULL,
                CustomerId uniqueidentifier NULL,
                SkuId uniqueidentifier NULL,
                StockLocationId uniqueidentifier NULL,
                QuantityDelta decimal(18,3) NOT NULL
                    CONSTRAINT DF_LedgerEffect_Quantity DEFAULT (0),
                AmountDelta decimal(19,4) NOT NULL
                    CONSTRAINT DF_LedgerEffect_Amount DEFAULT (0),
                RecordedAtUtc datetime2(7) NOT NULL
                    CONSTRAINT DF_LedgerEffect_RecordedAt DEFAULT (SYSUTCDATETIME()),
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_LedgerEffect_TenantId_Id UNIQUE (TenantId, Id),
                CONSTRAINT UQ_LedgerEffect_SourceEffect UNIQUE
                    (TenantId, BusinessSourceLineId, EffectKey),
                CONSTRAINT FK_LedgerEffect_Source FOREIGN KEY (TenantId, BusinessSourceLineId)
                    REFERENCES dbo.BusinessSourceLine (TenantId, Id),
                CONSTRAINT FK_LedgerEffect_Customer FOREIGN KEY (TenantId, CustomerId)
                    REFERENCES dbo.Customer (TenantId, Id),
                CONSTRAINT FK_LedgerEffect_Sku FOREIGN KEY (TenantId, SkuId)
                    REFERENCES dbo.Sku (TenantId, Id),
                CONSTRAINT FK_LedgerEffect_Location FOREIGN KEY (TenantId, StockLocationId)
                    REFERENCES dbo.StockLocation (TenantId, Id),
                CONSTRAINT CK_LedgerEffect_Nonzero CHECK (QuantityDelta <> 0 OR AmountDelta <> 0)
            );

            CREATE INDEX IX_BusinessSource_Tenant_Recorded
                ON dbo.BusinessSource (TenantId, RecordedAtUtc, Id);
            CREATE INDEX IX_AOrder_Tenant_Customer
                ON dbo.AOrder (TenantId, CustomerId, Status);
            CREATE INDEX IX_StockRight_Tenant_Resource
                ON dbo.StockRight (TenantId, StockLocationId, SkuId, Status);
            CREATE INDEX IX_BCreditGrant_Tenant_CustomerPool
                ON dbo.BCreditGrant (TenantId, CustomerId, PoolCode, Status);
            CREATE INDEX IX_LedgerEffect_Tenant_Recorded
                ON dbo.LedgerEffect (TenantId, RecordedAtUtc, BusinessSourceLineId);
            """);
    }

    public override void Down()
    {
        // Deliberately reverse FK order. Never invoke Down on a real tenant database.
        Execute.Sql("""
            DROP TABLE dbo.LedgerEffect;
            DROP TABLE dbo.BCreditGrant;
            DROP TABLE dbo.BQuota;
            DROP TABLE dbo.AReservation;
            DROP TABLE dbo.StockRight;
            DROP TABLE dbo.AOrderLine;
            DROP TABLE dbo.AOrder;
            DROP TABLE dbo.BusinessSourceLine;
            DROP TABLE dbo.BusinessSource;
            DROP TABLE dbo.BusinessCommand;
            DROP TABLE dbo.Trip;
            DROP TABLE dbo.StockLocation;
            DROP TABLE dbo.Sku;
            DROP TABLE dbo.ShipToSnapshot;
            DROP TABLE dbo.Customer;
            ALTER TABLE dbo.AppTenantBinding
                DROP CONSTRAINT UQ_AppTenantBinding_TenantId;
            """);
    }
}
