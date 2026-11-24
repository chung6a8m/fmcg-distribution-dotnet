using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[FluentMigrator.Tags(App03A1Profile.FieldName)]
[Migration(202611240001, "APP-03.5 immutable VND B sale pricing")]
public sealed class App03FieldSchema : Migration
{
    internal const long Version = 202611240001;

    public override void Up() => Execute.Sql("""
        CREATE TABLE dbo.App03BSalePricing (
            TenantId nvarchar(64) NOT NULL,
            SaleDeliveryId uniqueidentifier NOT NULL,
            PlanId uniqueidentifier NOT NULL,
            QuotePolicyId uniqueidentifier NOT NULL,
            Currency char(3) NOT NULL CHECK (Currency='VND'),
            UnitPrice decimal(19,4) NOT NULL CHECK (UnitPrice>0 AND UnitPrice=FLOOR(UnitPrice)),
            TaxAmount decimal(19,4) NOT NULL CHECK (TaxAmount=0),
            CONSTRAINT PK_App03BSalePricing PRIMARY KEY (TenantId,SaleDeliveryId),
            CONSTRAINT FK_App03BSalePricing_Delivery FOREIGN KEY (TenantId,SaleDeliveryId)
                REFERENCES dbo.SaleDelivery(TenantId,Id),
            CONSTRAINT FK_App03BSalePricing_Quote FOREIGN KEY (TenantId,PlanId,QuotePolicyId)
                REFERENCES dbo.F04PlanQuote(TenantId,PlanId,PolicyId)
        );
        EXEC(N'CREATE TRIGGER dbo.TR_App03BSalePricing_Immutable ON dbo.App03BSalePricing
            INSTEAD OF UPDATE, DELETE AS THROW 51704, ''B sale pricing is immutable.'', 1;');
        """);

    public override void Down() => throw new NotSupportedException("Forward-only DEV upgrade.");
}
