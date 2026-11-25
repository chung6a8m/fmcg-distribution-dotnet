using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(Version, "APP-05.2 atomic trip closure and zero-stock handoff")]
[FluentMigrator.Tags(App03A1Profile.TripClosureName)]
public sealed class App05TripClosureSchema : Migration
{
    internal const long Version = 202611250002;

    public override void Up()
    {
        Execute.Sql("""
            ALTER TABLE dbo.D3TripUnload ADD CONSTRAINT UQ_D3TripUnload_ClosureScope UNIQUE(TenantId,TripId,Id);
            ALTER TABLE dbo.AConfirmedLineSnapshot ADD CONSTRAINT UQ_AConfirm_ZeroScope
                UNIQUE(TenantId,TripId,AOrderLineId,ConfirmBusinessSourceId);

            CREATE TABLE dbo.App05ZeroStockHandoffLine (
                TenantId nvarchar(64) NOT NULL,
                TripId uniqueidentifier NOT NULL,
                HandoffId uniqueidentifier NOT NULL,
                OrderLineId uniqueidentifier NOT NULL,
                ConfirmSourceId uniqueidentifier NOT NULL,
                HeldBaseQuantity decimal(18,3) NOT NULL CHECK(HeldBaseQuantity>=0),
                PRIMARY KEY(TenantId,TripId,OrderLineId),
                FOREIGN KEY(TenantId,TripId,HandoffId) REFERENCES dbo.D3TripUnload(TenantId,TripId,Id),
                FOREIGN KEY(TenantId,TripId,OrderLineId,ConfirmSourceId)
                    REFERENCES dbo.AConfirmedLineSnapshot(TenantId,TripId,AOrderLineId,ConfirmBusinessSourceId)
            );

            CREATE TABLE dbo.App05TripClosure (
                TenantId nvarchar(64) NOT NULL,
                TripId uniqueidentifier NOT NULL,
                ClosureId uniqueidentifier NOT NULL,
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CommandId uniqueidentifier NOT NULL,
                ExpectedRevision char(64) NOT NULL,
                PayloadHash binary(32) NOT NULL,
                Status nvarchar(32) NOT NULL,
                ClosedAt datetimeoffset(7) NOT NULL,
                OpenWorkCount int NOT NULL,
                HandoffId uniqueidentifier NOT NULL,
                Snapshot nvarchar(max) NOT NULL CHECK(ISJSON(Snapshot)=1),
                PRIMARY KEY(TenantId,TripId),
                UNIQUE(TenantId,ClosureId),
                UNIQUE(TenantId,ActorId,CommandId),
                FOREIGN KEY(TenantId,TripId,HandoffId) REFERENCES dbo.D3TripUnload(TenantId,TripId,Id),
                FOREIGN KEY(TenantId,TripId,ActorId) REFERENCES dbo.App05TripOperator(TenantId,TripId,ActorId),
                CHECK((Status='CLOSED' AND OpenWorkCount=0) OR
                    (Status='CLOSED_WITH_OPEN_CASES' AND OpenWorkCount>0))
            );

            CREATE TABLE dbo.App05TripCloseRejection (
                TenantId nvarchar(64) NOT NULL,
                TripId uniqueidentifier NOT NULL,
                ActorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CommandId uniqueidentifier NOT NULL,
                PayloadHash binary(32) NOT NULL,
                Code varchar(32) NOT NULL CHECK(Code IN ('READINESS_CHANGED','TRIP_CLOSE_NOT_READY','TRIP_ALREADY_CLOSED')),
                PRIMARY KEY(TenantId,ActorId,CommandId),
                FOREIGN KEY(TenantId,TripId,ActorId) REFERENCES dbo.App05TripOperator(TenantId,TripId,ActorId)
            );
            """);

        foreach (string table in new[] { "App05ZeroStockHandoffLine", "App05TripClosure", "App05TripCloseRejection" })
            Execute.Sql($"""
                CREATE TRIGGER dbo.TR_{table}_Immutable ON dbo.{table} INSTEAD OF UPDATE, DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51056, 'APP-05.2 closure and handoff evidence is immutable.', 1;
                END;
                """);
    }

    public override void Down() => throw new NotSupportedException("Forward-only synthetic DEV migration.");
}
