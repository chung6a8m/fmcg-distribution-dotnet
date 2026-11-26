using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(Version, "APP-05.5 approve A remainder closure and prove released rights")]
[FluentMigrator.Tags(App03A1Profile.SuccessorName)]
public sealed class App05ARemainderApprovalSchema : Migration
{
    internal const long Version = 202611260004;

    public override void Up()
    {
        Execute.Sql("""
            ALTER TABLE dbo.App05ARemainderClosureProposal ADD CONSTRAINT UQ_App05AClosureProposal_Approval
                UNIQUE(TenantId,ProposalId,OrderLineId,SkuId,Maker,Quantity);
            CREATE TABLE dbo.App05ARemainderApproval (
                TenantId nvarchar(64) NOT NULL,
                ProposalId uniqueidentifier NOT NULL,
                SourceId uniqueidentifier NOT NULL,
                OrderLineId uniqueidentifier NOT NULL,
                SkuId uniqueidentifier NOT NULL,
                Maker nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Checker nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CommandId uniqueidentifier NOT NULL,
                Quantity decimal(18,3) NOT NULL CHECK(Quantity>0),
                Released decimal(18,3) NOT NULL,
                ApprovedAt datetimeoffset(7) NOT NULL,
                PRIMARY KEY(TenantId,ProposalId),
                UNIQUE(TenantId,SourceId,OrderLineId,SkuId),
                UNIQUE(TenantId,Checker,CommandId),
                CHECK(Released>=0 AND Released<=Quantity),
                CHECK(Maker COLLATE Latin1_General_100_CI_AS<>Checker COLLATE Latin1_General_100_CI_AS),
                FOREIGN KEY(TenantId,SourceId) REFERENCES dbo.BusinessSource(TenantId,Id),
                FOREIGN KEY(TenantId,ProposalId,OrderLineId,SkuId,Maker,Quantity)
                    REFERENCES dbo.App05ARemainderClosureProposal(TenantId,ProposalId,OrderLineId,SkuId,Maker,Quantity)
            );
            CREATE INDEX IX_App05ARemainderApproval_Line ON dbo.App05ARemainderApproval(TenantId,OrderLineId);
            CREATE TABLE dbo.App05ARemainderRelease (
                TenantId nvarchar(64) NOT NULL,
                SourceId uniqueidentifier NOT NULL,
                StockRightId uniqueidentifier NOT NULL,
                OrderLineId uniqueidentifier NOT NULL,
                RightTripId uniqueidentifier NOT NULL,
                WarehouseId uniqueidentifier NOT NULL,
                SkuId uniqueidentifier NOT NULL,
                SourceLineId uniqueidentifier NOT NULL,
                RightKind char(1) NOT NULL CHECK(RightKind='A'),
                NeverLoaded decimal(18,3) NOT NULL CHECK(NeverLoaded>=0),
                Returned decimal(18,3) NOT NULL CHECK(Returned>=0),
                PRIMARY KEY(TenantId,SourceId,StockRightId),
                UNIQUE(TenantId,SourceLineId),
                CHECK(NeverLoaded+Returned>0),
                FOREIGN KEY(TenantId,SourceId,OrderLineId,SkuId)
                    REFERENCES dbo.App05ARemainderApproval(TenantId,SourceId,OrderLineId,SkuId),
                FOREIGN KEY(TenantId,StockRightId,OrderLineId) REFERENCES dbo.AReservation(TenantId,StockRightId,AOrderLineId),
                FOREIGN KEY(TenantId,StockRightId,RightTripId,WarehouseId,SkuId,RightKind)
                    REFERENCES dbo.StockRight(TenantId,Id,TripId,StockLocationId,SkuId,RightKind),
                FOREIGN KEY(TenantId,SourceLineId) REFERENCES dbo.BusinessSourceLine(TenantId,Id)
            );
            CREATE INDEX IX_App05ARemainderRelease_Right ON dbo.App05ARemainderRelease(TenantId,StockRightId);
            CREATE TABLE dbo.App05ARemainderReleaseLot (
                TenantId nvarchar(64) NOT NULL,
                SourceId uniqueidentifier NOT NULL,
                StockRightId uniqueidentifier NOT NULL,
                LotId uniqueidentifier NOT NULL,
                NeverLoaded decimal(18,3) NOT NULL CHECK(NeverLoaded>=0),
                Returned decimal(18,3) NOT NULL CHECK(Returned>=0),
                PRIMARY KEY(TenantId,SourceId,StockRightId,LotId),
                CHECK(NeverLoaded+Returned>0),
                FOREIGN KEY(TenantId,SourceId,StockRightId) REFERENCES dbo.App05ARemainderRelease(TenantId,SourceId,StockRightId),
                FOREIGN KEY(TenantId,StockRightId,LotId) REFERENCES dbo.F03LotHold(TenantId,StockRightId,LotId)
            );
            CREATE INDEX IX_App05ARemainderReleaseLot_Right ON dbo.App05ARemainderReleaseLot(TenantId,StockRightId,LotId);
            """);
        foreach (string table in new[] { "App05ARemainderApproval", "App05ARemainderRelease", "App05ARemainderReleaseLot" })
            Execute.Sql($"""
                CREATE TRIGGER dbo.TR_{table}_Immutable ON dbo.{table} INSTEAD OF UPDATE, DELETE AS
                BEGIN
                    THROW 51933, 'APP-05 remainder approval and release evidence is immutable.', 1;
                END;
                """);
    }

    public override void Down() => throw new NotSupportedException("Forward-only synthetic DEV migration.");
}
