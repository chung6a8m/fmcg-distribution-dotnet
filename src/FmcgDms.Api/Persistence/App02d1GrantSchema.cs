using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(202611220006, "APP-02D.1 synthetic grant issuer and scoped command provenance")]
public sealed class App02d1GrantSchema : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            ALTER TABLE dbo.App02ActorRoleGrant DROP CONSTRAINT CK_App02Grant_Role;
            ALTER TABLE dbo.App02ActorRoleGrant ADD CONSTRAINT CK_App02Grant_Role CHECK (RoleCode IN
                (N'sales',N'warehouse',N'cashier',N'cashier_supervisor',N'case_checker',
                 N'finance',N'credit_maker',N'credit_checker'));

            DECLARE @roleCheck sysname = (
                SELECT name FROM sys.check_constraints
                WHERE parent_object_id=OBJECT_ID(N'dbo.C2CommandScope')
                AND parent_column_id=COLUMNPROPERTY(OBJECT_ID(N'dbo.C2CommandScope'),N'RoleCode','ColumnId'));
            IF @roleCheck IS NULL THROW 51040, 'Expected C.2 command role constraint is missing.', 1;
            DECLARE @dropRoleCheck nvarchar(300) = N'ALTER TABLE dbo.C2CommandScope DROP CONSTRAINT ' + QUOTENAME(@roleCheck);
            EXEC sys.sp_executesql @dropRoleCheck;
            """);
        Execute.Sql("""
            ALTER TABLE dbo.C2CommandScope ADD D1GrantTripId uniqueidentifier NULL;
            """);
        Execute.Sql("""
            ALTER TABLE dbo.C2CommandScope ADD CONSTRAINT FK_C2CommandScope_D1GrantTrip
                FOREIGN KEY(TenantId,D1GrantTripId) REFERENCES dbo.Trip(TenantId,Id);
            ALTER TABLE dbo.C2CommandScope ADD CONSTRAINT CK_C2CommandScope_D1GrantRole CHECK (
                (D1GrantTripId IS NULL AND RoleCode IN (N'sales',N'warehouse'))
                OR (D1GrantTripId IS NOT NULL AND RoleCode IN (N'warehouse',N'finance')
                    AND LoadTripId IS NULL AND DeliveryTripId IS NULL));
            """);
    }

    public override void Down() => throw new NotSupportedException("Forward-only application migration.");
}
