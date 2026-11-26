using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(Version, "APP-05.5 store source-bound A remainder closure proposals")]
[FluentMigrator.Tags(App03A1Profile.SuccessorName)]
public sealed class App05ARemainderClosureSchema : Migration
{
    internal const long Version = 202611260003;

    public override void Up()
    {
        Execute.Sql("""
            CREATE TABLE dbo.App05ARemainderClosureProposal (
                TenantId nvarchar(64) NOT NULL,
                ProposalId uniqueidentifier NOT NULL,
                TripId uniqueidentifier NOT NULL,
                OrderLineId uniqueidentifier NOT NULL,
                ConfirmSourceId uniqueidentifier NOT NULL,
                SkuId uniqueidentifier NOT NULL,
                FollowUpId uniqueidentifier NOT NULL,
                SourceKind varchar(32) NOT NULL DEFAULT('A_REMAINDER') CHECK(SourceKind='A_REMAINDER'),
                CommandId uniqueidentifier NOT NULL,
                Maker nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Quantity decimal(18,3) NOT NULL CHECK(Quantity>0),
                Reason nvarchar(240) NOT NULL CHECK(LEN(Reason)>0),
                SourceRevision binary(32) NOT NULL,
                RecordedAt datetimeoffset(7) NOT NULL,
                PRIMARY KEY(TenantId,ProposalId),
                FOREIGN KEY(TenantId,TripId) REFERENCES dbo.App05TripClosure(TenantId,TripId),
                FOREIGN KEY(TenantId,TripId,OrderLineId,ConfirmSourceId,SkuId)
                    REFERENCES dbo.AConfirmedLineSnapshot(TenantId,TripId,AOrderLineId,ConfirmBusinessSourceId,SkuId),
                FOREIGN KEY(TenantId,TripId,SourceKind,OrderLineId,FollowUpId)
                    REFERENCES dbo.App05FollowUp(TenantId,TripId,SourceKind,SourceId,FollowUpId),
                UNIQUE(TenantId,Maker,CommandId)
            );
            CREATE INDEX IX_App05ARemainderClosureProposal_Line
                ON dbo.App05ARemainderClosureProposal(TenantId,TripId,OrderLineId,RecordedAt);
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_App05ARemainderClosureProposal_Immutable
            ON dbo.App05ARemainderClosureProposal INSTEAD OF UPDATE, DELETE AS
            BEGIN
                THROW 51932, 'APP-05 remainder closure proposal is immutable.', 1;
            END;
            """);
    }

    public override void Down() => throw new NotSupportedException("Forward-only synthetic DEV migration.");
}
