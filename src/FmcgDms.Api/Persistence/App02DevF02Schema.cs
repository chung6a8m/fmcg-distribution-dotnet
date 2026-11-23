using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(202611230003, "DEV-F02 synthetic employee advance sources and journal")]
[FluentMigrator.Tags(Profile)]
public sealed class App02DevF02Schema : Migration
{
    internal const string Profile = "DevF02";

    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE dbo.F02Advance (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL REFERENCES dbo.AppTenantBinding(TenantId),
                EmployeeId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CommandId uniqueidentifier NOT NULL,
                Issued bigint NOT NULL CHECK (Issued>0),
                Currency char(3) NOT NULL DEFAULT 'VND' CHECK (Currency='VND'),
                Evidence nvarchar(120) NOT NULL CHECK (LEN(Evidence)>0),
                OccurredAtUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
                CONSTRAINT UQ_F02Advance_Tenant UNIQUE (TenantId,Id),
                CONSTRAINT UQ_F02Advance_Evidence UNIQUE (TenantId,Evidence),
                CONSTRAINT UQ_F02Advance_Command UNIQUE (TenantId,ActorId,CommandId),
                CONSTRAINT FK_F02Advance_Command FOREIGN KEY (TenantId,ActorId,CommandId)
                    REFERENCES dbo.C1Command(TenantId,ActorId,CommandId),
                CONSTRAINT CK_F02Advance_Segregation CHECK (EmployeeId<>ActorId)
            );

            CREATE TABLE dbo.F02AdvanceEntry (
                Id uniqueidentifier NOT NULL PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                AdvanceId uniqueidentifier NOT NULL,
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CommandId uniqueidentifier NOT NULL,
                Kind varchar(20) NOT NULL CHECK (
                    Kind IN ('EXPENSE_REQUEST','EXPENSE_APPROVE','RETURN_OFFER','RETURN_ACCEPT')),
                ReferenceId uniqueidentifier NULL,
                Amount bigint NOT NULL CHECK (Amount>0),
                Evidence nvarchar(120) NOT NULL CHECK (LEN(Evidence)>0),
                DecisionReason nvarchar(200) NULL CHECK (DecisionReason IS NULL OR LEN(DecisionReason)>0),
                PolicyVersion varchar(16) NOT NULL DEFAULT 'DEV-F02-v1' CHECK (PolicyVersion='DEV-F02-v1'),
                OccurredAtUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
                CONSTRAINT UQ_F02Entry_Tenant UNIQUE (TenantId,AdvanceId,Id),
                CONSTRAINT UQ_F02Entry_Evidence UNIQUE (TenantId,Kind,Evidence),
                CONSTRAINT UQ_F02Entry_Command UNIQUE (TenantId,ActorId,CommandId),
                CONSTRAINT FK_F02Entry_Advance FOREIGN KEY (TenantId,AdvanceId)
                    REFERENCES dbo.F02Advance(TenantId,Id),
                CONSTRAINT FK_F02Entry_Reference FOREIGN KEY (TenantId,AdvanceId,ReferenceId)
                    REFERENCES dbo.F02AdvanceEntry(TenantId,AdvanceId,Id),
                CONSTRAINT FK_F02Entry_Command FOREIGN KEY (TenantId,ActorId,CommandId)
                    REFERENCES dbo.C1Command(TenantId,ActorId,CommandId),
                CONSTRAINT CK_F02Entry_DecisionReason CHECK (
                    (Kind IN ('EXPENSE_APPROVE','RETURN_ACCEPT') AND DecisionReason IS NOT NULL)
                    OR (Kind IN ('EXPENSE_REQUEST','RETURN_OFFER') AND DecisionReason IS NULL)),
                CONSTRAINT CK_F02Entry_Reference CHECK (
                    (Kind IN ('EXPENSE_REQUEST','RETURN_OFFER') AND ReferenceId IS NULL)
                    OR (Kind IN ('EXPENSE_APPROVE','RETURN_ACCEPT') AND ReferenceId IS NOT NULL))
            );
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_F02Advance_Immutable ON dbo.F02Advance INSTEAD OF UPDATE, DELETE AS
                THROW 51027, 'Issued advance sources are immutable.', 1;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_F02Entry_Immutable ON dbo.F02AdvanceEntry INSTEAD OF UPDATE, DELETE AS
                THROW 51028, 'Advance journal entries are immutable.', 1;
            """);
    }

    public override void Down() => throw new NotSupportedException("Use the nonce-owned scratch reset.");
}
