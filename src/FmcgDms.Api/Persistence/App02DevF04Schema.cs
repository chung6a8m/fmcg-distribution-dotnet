using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(202611230005, "DEV-F04 immutable VND price, discount and VAT snapshots")]
[FluentMigrator.Tags(Profile)]
public sealed class App02DevF04Schema : Migration
{
    internal const string Profile = "DevF04";

    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE dbo.F04PricePlan (
                TenantId nvarchar(64) NOT NULL,
                Id uniqueidentifier NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                Version int NOT NULL CHECK (Version>0),
                TaxMode varchar(12) NOT NULL CHECK (TaxMode IN ('EXCLUSIVE','INCLUSIVE')),
                OrderDiscount bigint NOT NULL CHECK (OrderDiscount>=0),
                EffectiveFromUtc datetime2(7) NOT NULL,
                EffectiveUntilUtc datetime2(7) NOT NULL CHECK (EffectiveUntilUtc>EffectiveFromUtc),
                Maker nvarchar(128) NOT NULL CHECK (LEN(Maker)>0),
                Checker nvarchar(128) NOT NULL CHECK (LEN(Checker)>0 AND Checker<>Maker),
                ApprovedAtUtc datetime2(7) NOT NULL,
                EvidenceRef nvarchar(120) NOT NULL CHECK (LEN(EvidenceRef)>0),
                CONSTRAINT PK_F04PricePlan PRIMARY KEY (TenantId,Id),
                CONSTRAINT UQ_F04PricePlan_Version UNIQUE (TenantId,CustomerId,Version),
                CONSTRAINT FK_F04PricePlan_Customer FOREIGN KEY (TenantId,CustomerId)
                    REFERENCES dbo.Customer(TenantId,Id)
            );
            CREATE TABLE dbo.F04PlanQuote (
                TenantId nvarchar(64) NOT NULL,
                PlanId uniqueidentifier NOT NULL,
                PolicyId uniqueidentifier NOT NULL,
                CONSTRAINT PK_F04PlanQuote PRIMARY KEY (TenantId,PlanId,PolicyId),
                CONSTRAINT FK_F04PlanQuote_Plan FOREIGN KEY (TenantId,PlanId)
                    REFERENCES dbo.F04PricePlan(TenantId,Id),
                CONSTRAINT FK_F04PlanQuote_Policy FOREIGN KEY (TenantId,PolicyId)
                    REFERENCES dbo.C2CommercialPolicy(TenantId,Id)
            );
            CREATE TABLE dbo.F04PriceLine (
                TenantId nvarchar(64) NOT NULL,
                OrderLineId uniqueidentifier NOT NULL,
                PlanId uniqueidentifier NOT NULL,
                QuotePolicyId uniqueidentifier NOT NULL,
                ResidualProfile varchar(32) NOT NULL DEFAULT 'VND_NET_TAX_CUMULATIVE_V1'
                    CHECK (ResidualProfile='VND_NET_TAX_CUMULATIVE_V1'),
                SourceBase bigint NOT NULL CHECK (SourceBase>=0),
                AllocatedDiscount bigint NOT NULL CHECK (AllocatedDiscount>=0 AND AllocatedDiscount<=SourceBase),
                NetAmount bigint NOT NULL CHECK (NetAmount>=0),
                TaxAmount bigint NOT NULL CHECK (TaxAmount>=0),
                GrossAmount bigint NOT NULL CHECK (GrossAmount=NetAmount+TaxAmount),
                QuoteUnitPrice decimal(19,4) NOT NULL CHECK (QuoteUnitPrice>=0),
                TaxRate decimal(9,6) NOT NULL CHECK (TaxRate BETWEEN 0 AND 1),
                Currency char(3) NOT NULL DEFAULT 'VND' CHECK (Currency='VND'),
                CONSTRAINT PK_F04PriceLine PRIMARY KEY (TenantId,OrderLineId),
                CONSTRAINT FK_F04PriceLine_Quote FOREIGN KEY (TenantId,PlanId,QuotePolicyId)
                    REFERENCES dbo.F04PlanQuote(TenantId,PlanId,PolicyId),
                CONSTRAINT FK_F04PriceLine_Order FOREIGN KEY (TenantId,OrderLineId)
                    REFERENCES dbo.AOrderLine(TenantId,Id)
            );
            CREATE TABLE dbo.F04DeliveryPricing (
                TenantId nvarchar(64) NOT NULL,
                DeliveryId uniqueidentifier NOT NULL,
                OrderLineId uniqueidentifier NOT NULL,
                NetAmount bigint NOT NULL CHECK (NetAmount>=0),
                TaxAmount bigint NOT NULL CHECK (TaxAmount>=0),
                GrossAmount bigint NOT NULL CHECK (GrossAmount=NetAmount+TaxAmount),
                Currency char(3) NOT NULL CHECK (Currency='VND'),
                CONSTRAINT PK_F04DeliveryPricing PRIMARY KEY (TenantId,DeliveryId),
                CONSTRAINT FK_F04DeliveryPricing_Delivery FOREIGN KEY (TenantId,DeliveryId)
                    REFERENCES dbo.SaleDelivery(TenantId,Id),
                CONSTRAINT FK_F04DeliveryPricing_Line FOREIGN KEY (TenantId,OrderLineId)
                    REFERENCES dbo.F04PriceLine(TenantId,OrderLineId)
            );
            """);
        foreach (string table in new[] { "F04PricePlan", "F04PlanQuote", "F04PriceLine", "F04DeliveryPricing" })
        {
            Execute.Sql($"""
                CREATE TRIGGER dbo.TR_{table}_Immutable ON dbo.{table} INSTEAD OF UPDATE, DELETE AS
                    THROW 51030, 'DEV-F04 price source evidence is immutable.', 1;
                """);
        }
    }

    public override void Down() => throw new NotSupportedException("Use the nonce-owned scratch reset.");
}
