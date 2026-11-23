using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(202611230004, "DEV-F03 unit and lot provenance on A reserve, load and delivery")]
[FluentMigrator.Tags(Profile)]
public sealed class App02DevF03Schema : Migration
{
    internal const string Profile = "DevF03";

    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE dbo.F03Unit (
                TenantId nvarchar(64) NOT NULL,
                SkuId uniqueidentifier NOT NULL,
                UomCode nvarchar(24) COLLATE Latin1_General_100_BIN2 NOT NULL CHECK (LEN(UomCode)>0),
                Factor decimal(19,6) NOT NULL CHECK (Factor>0),
                Step decimal(18,3) NOT NULL CHECK (Step>0),
                CONSTRAINT PK_F03Unit PRIMARY KEY (TenantId,SkuId,UomCode),
                CONSTRAINT FK_F03Unit_Sku FOREIGN KEY (TenantId,SkuId) REFERENCES dbo.Sku(TenantId,Id)
            );
            CREATE TABLE dbo.F03DraftQuantity (
                TenantId nvarchar(64) NOT NULL,
                DraftId uniqueidentifier NOT NULL,
                LineNumber int NOT NULL,
                UomCode nvarchar(24) NOT NULL CHECK (LEN(UomCode)>0),
                EnteredQuantity decimal(18,3) NOT NULL CHECK (EnteredQuantity>0),
                Factor decimal(19,6) NOT NULL CHECK (Factor>0),
                Step decimal(18,3) NOT NULL CHECK (Step>0),
                BaseStep decimal(18,3) NOT NULL CHECK (BaseStep>0),
                LotTracked bit NOT NULL,
                CONSTRAINT PK_F03DraftQuantity PRIMARY KEY (TenantId,DraftId,LineNumber),
                CONSTRAINT FK_F03DraftQuantity_Line FOREIGN KEY (TenantId,DraftId,LineNumber)
                    REFERENCES dbo.C2OrderDraftLine(TenantId,DraftId,LineNumber)
            );
            CREATE TABLE dbo.F03Lot (
                TenantId nvarchar(64) NOT NULL,
                Id uniqueidentifier NOT NULL,
                SkuId uniqueidentifier NOT NULL,
                Code nvarchar(64) NOT NULL CHECK (LEN(Code)>0),
                ExpiresOn date NOT NULL,
                CONSTRAINT PK_F03Lot PRIMARY KEY (TenantId,Id),
                CONSTRAINT UQ_F03Lot_SkuCode UNIQUE (TenantId,SkuId,Code),
                CONSTRAINT FK_F03Lot_Sku FOREIGN KEY (TenantId,SkuId) REFERENCES dbo.Sku(TenantId,Id)
            );
            CREATE TABLE dbo.F03LotOpening (
                TenantId nvarchar(64) NOT NULL,
                LotId uniqueidentifier NOT NULL,
                LocationId uniqueidentifier NOT NULL,
                Quantity decimal(18,3) NOT NULL CHECK (Quantity>0),
                CONSTRAINT PK_F03LotOpening PRIMARY KEY (TenantId,LotId,LocationId),
                CONSTRAINT FK_F03LotOpening_Lot FOREIGN KEY (TenantId,LotId) REFERENCES dbo.F03Lot(TenantId,Id),
                CONSTRAINT FK_F03LotOpening_Location FOREIGN KEY (TenantId,LocationId)
                    REFERENCES dbo.StockLocation(TenantId,Id)
            );
            CREATE TABLE dbo.F03LotHold (
                TenantId nvarchar(64) NOT NULL,
                StockRightId uniqueidentifier NOT NULL,
                LotId uniqueidentifier NOT NULL,
                Quantity decimal(18,3) NOT NULL CHECK (Quantity>0),
                CONSTRAINT PK_F03LotHold PRIMARY KEY (TenantId,StockRightId,LotId),
                CONSTRAINT FK_F03LotHold_Right FOREIGN KEY (TenantId,StockRightId) REFERENCES dbo.StockRight(TenantId,Id),
                CONSTRAINT FK_F03LotHold_Lot FOREIGN KEY (TenantId,LotId) REFERENCES dbo.F03Lot(TenantId,Id)
            );
            CREATE INDEX IX_F03LotHold_Lot ON dbo.F03LotHold(TenantId,LotId);
            CREATE TABLE dbo.F03LotLoad (
                TenantId nvarchar(64) NOT NULL,
                ManifestLineId uniqueidentifier NOT NULL,
                LotId uniqueidentifier NOT NULL,
                Quantity decimal(18,3) NOT NULL CHECK (Quantity>0),
                CONSTRAINT PK_F03LotLoad PRIMARY KEY (TenantId,ManifestLineId,LotId),
                CONSTRAINT FK_F03LotLoad_Manifest FOREIGN KEY (TenantId,ManifestLineId)
                    REFERENCES dbo.TripManifestLine(TenantId,Id),
                CONSTRAINT FK_F03LotLoad_Lot FOREIGN KEY (TenantId,LotId) REFERENCES dbo.F03Lot(TenantId,Id)
            );
            CREATE TABLE dbo.F03LotIssue (
                TenantId nvarchar(64) NOT NULL,
                DeliveryId uniqueidentifier NOT NULL,
                LotId uniqueidentifier NOT NULL,
                Quantity decimal(18,3) NOT NULL CHECK (Quantity>0),
                CONSTRAINT PK_F03LotIssue PRIMARY KEY (TenantId,DeliveryId,LotId),
                CONSTRAINT FK_F03LotIssue_Delivery FOREIGN KEY (TenantId,DeliveryId)
                    REFERENCES dbo.SaleDelivery(TenantId,Id),
                CONSTRAINT FK_F03LotIssue_Lot FOREIGN KEY (TenantId,LotId) REFERENCES dbo.F03Lot(TenantId,Id)
            );
            """);
        foreach (string table in new[] { "F03DraftQuantity", "F03Lot", "F03LotOpening", "F03LotHold",
                     "F03LotLoad", "F03LotIssue" })
        {
            Execute.Sql($"""
                CREATE TRIGGER dbo.TR_{table}_Immutable ON dbo.{table} INSTEAD OF UPDATE, DELETE AS
                    THROW 51029, 'DEV-F03 source evidence is immutable.', 1;
                """);
        }
    }

    public override void Down() => throw new NotSupportedException("Use the nonce-owned scratch reset.");
}
