using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(202611240006, "APP-03.8 read-only Owner grant")]
[FluentMigrator.Tags(App03A1Profile.FinanceName)]
public sealed class App03OwnerSchema : Migration
{
    internal const long Version = 202611240006;

    public override void Up() => Execute.Sql("""
        ALTER TABLE dbo.App02ActorRoleGrant DROP CONSTRAINT CK_App02Grant_Role;
        ALTER TABLE dbo.App02ActorRoleGrant ADD CONSTRAINT CK_App02Grant_Role CHECK (RoleCode IN
            (N'sales',N'warehouse',N'cashier',N'cashier_supervisor',N'case_checker',
             N'finance',N'credit_maker',N'credit_checker',N'owner'));
        """);

    public override void Down() => throw new NotSupportedException("Forward-only DEV upgrade.");
}
