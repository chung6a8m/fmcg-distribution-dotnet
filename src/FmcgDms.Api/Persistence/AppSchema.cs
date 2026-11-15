using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(202611150001, "APP-00A tenant binding and nonproduction diagnostic table")]
public sealed class AppSchema : Migration
{
    public override void Up()
    {
        Create.Table("AppTenantBinding").InSchema("dbo")
            .WithColumn("Slot").AsInt32().PrimaryKey().NotNullable()
            .WithColumn("TenantId").AsString(64).NotNullable()
            .WithColumn("BoundAtUtc").AsDateTime2().NotNullable();
        Create.Table("DiagnosticItem").InSchema("dbo")
            .WithColumn("Id").AsGuid().PrimaryKey().NotNullable()
            .WithColumn("Description").AsString(120).NotNullable()
            .WithColumn("CreatedAtUtc").AsDateTime2().NotNullable();
        // This is not the unified A/B/AR/COGS ledger. APP-01A designs that schema.
    }

    public override void Down()
    {
        Delete.Table("DiagnosticItem").InSchema("dbo");
        Delete.Table("AppTenantBinding").InSchema("dbo");
    }
}
