using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(Version, "APP-05.1 trip readiness authority and follow-up history")]
[FluentMigrator.Tags(App03A1Profile.CloseReadinessName)]
public sealed class App05FollowUpSchema : Migration
{
    internal const long Version = 202611250001;

    public override void Up()
    {
        Execute.Sql("""
            ALTER TABLE dbo.App02ActorRoleGrant DROP CONSTRAINT CK_App02Grant_Role;
            ALTER TABLE dbo.App02ActorRoleGrant ADD CONSTRAINT CK_App02Grant_Role CHECK (RoleCode IN
                (N'sales',N'warehouse',N'cashier',N'cashier_supervisor',N'case_checker',
                 N'finance',N'credit_maker',N'credit_checker',N'owner',N'tenant_ops',N'trip_assignee'));

            CREATE TABLE dbo.App05TripOperator (
                TenantId nvarchar(64) NOT NULL,
                TripId uniqueidentifier NOT NULL,
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                IsActive bit NOT NULL DEFAULT(1),
                PRIMARY KEY(TenantId,TripId,ActorId),
                FOREIGN KEY(TenantId,TripId) REFERENCES dbo.Trip(TenantId,Id)
            );

            CREATE TABLE dbo.App05FollowUp (
                TenantId nvarchar(64) NOT NULL,
                TripId uniqueidentifier NOT NULL,
                SourceKind varchar(32) NOT NULL,
                SourceId uniqueidentifier NOT NULL,
                FollowUpId uniqueidentifier NOT NULL,
                PRIMARY KEY(TenantId,TripId,SourceKind,SourceId),
                UNIQUE(TenantId,FollowUpId),
                UNIQUE(TenantId,TripId,SourceKind,SourceId,FollowUpId),
                FOREIGN KEY(TenantId,TripId) REFERENCES dbo.Trip(TenantId,Id),
                CHECK (SourceKind IN ('A_REMAINDER','D4_RECONCILIATION','F05_RETURN','F06_COST','F08_REFUND'))
            );

            CREATE TABLE dbo.App05FollowUpAssignment (
                EventId bigint IDENTITY NOT NULL PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                TripId uniqueidentifier NOT NULL,
                SourceKind varchar(32) NOT NULL,
                SourceId uniqueidentifier NOT NULL,
                FollowUpId uniqueidentifier NOT NULL,
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CommandId uniqueidentifier NOT NULL,
                PayloadHash binary(32) NOT NULL,
                ExpectedRevision char(64) NOT NULL,
                Assignee nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                DueAt datetimeoffset(7) NOT NULL,
                NextAction nvarchar(240) NOT NULL CHECK(LEN(NextAction)>0),
                Reason nvarchar(240) NOT NULL CHECK(LEN(Reason)>0),
                AssignedAt datetimeoffset(7) NOT NULL,
                UNIQUE (TenantId,ActorId,CommandId),
                FOREIGN KEY(TenantId,TripId,SourceKind,SourceId,FollowUpId)
                    REFERENCES dbo.App05FollowUp(TenantId,TripId,SourceKind,SourceId,FollowUpId),
                FOREIGN KEY(TenantId,TripId,Assignee)
                    REFERENCES dbo.App05TripOperator(TenantId,TripId,ActorId),
                FOREIGN KEY(TenantId,TripId,ActorId)
                    REFERENCES dbo.App05TripOperator(TenantId,TripId,ActorId),
                CHECK (DueAt>AssignedAt)
            );

            CREATE INDEX IX_App05FollowUpAssignment_Latest
                ON dbo.App05FollowUpAssignment(TenantId,FollowUpId,EventId DESC);
            """);

        foreach (string table in new[] { "App05FollowUp", "App05FollowUpAssignment" })
            Execute.Sql($"""
                CREATE TRIGGER dbo.TR_{table}_Immutable ON dbo.{table} INSTEAD OF UPDATE, DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51055, 'APP-05 follow-up history is immutable.', 1;
                END;
                """);
    }

    public override void Down() => throw new NotSupportedException("Forward-only synthetic DEV migration.");
}
