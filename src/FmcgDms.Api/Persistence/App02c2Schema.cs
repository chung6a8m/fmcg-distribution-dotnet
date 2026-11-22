using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(202611220002, "APP-02C.2 confirmed A orders and approved synthetic commercial policy")]
public sealed class App02c2Schema : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            ALTER TABLE dbo.App02ActorRoleGrant DROP CONSTRAINT CK_App02Grant_Role;
            ALTER TABLE dbo.App02ActorRoleGrant ADD CONSTRAINT CK_App02Grant_Role
                CHECK (RoleCode IN (N'sales',N'warehouse',N'cashier',N'cashier_supervisor',N'case_checker'));

            CREATE TABLE dbo.C2CommandScope (
                TenantId nvarchar(64) NOT NULL,
                CommandRowId uniqueidentifier NOT NULL,
                RoleCode nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL CHECK (RoleCode IN (N'sales',N'warehouse')),
                CONSTRAINT PK_C2CommandScope PRIMARY KEY (TenantId,CommandRowId),
                CONSTRAINT FK_C2CommandScope_Command FOREIGN KEY (TenantId,CommandRowId)
                    REFERENCES dbo.BusinessCommand(TenantId,Id)
            );
            CREATE TABLE dbo.C2OrderDraft (
                TenantId nvarchar(64) NOT NULL,
                Id uniqueidentifier NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                TripId uniqueidentifier NOT NULL,
                SelectionId uniqueidentifier NOT NULL,
                CreatedBy nvarchar(128) NOT NULL,
                CONSTRAINT PK_C2OrderDraft PRIMARY KEY (TenantId,Id),
                CONSTRAINT FK_C2Draft_Source FOREIGN KEY (TenantId,Id) REFERENCES dbo.BusinessSource(TenantId,Id),
                CONSTRAINT FK_C2Draft_Trip FOREIGN KEY (TenantId,TripId) REFERENCES dbo.Trip(TenantId,Id),
                CONSTRAINT FK_C2Draft_Selection FOREIGN KEY (TenantId,CustomerId,SelectionId)
                    REFERENCES dbo.C1ShipToSelection(TenantId,CustomerId,Id)
            );
            CREATE TABLE dbo.C2OrderDraftLine (
                TenantId nvarchar(64) NOT NULL,
                DraftId uniqueidentifier NOT NULL,
                LineNumber int NOT NULL CHECK (LineNumber>0),
                SkuId uniqueidentifier NOT NULL,
                BaseQuantity decimal(18,3) NOT NULL CHECK (BaseQuantity>0),
                BaseUom nvarchar(24) NOT NULL CHECK (LEN(LTRIM(RTRIM(BaseUom)))>0),
                CONSTRAINT PK_C2DraftLine PRIMARY KEY (TenantId,DraftId,LineNumber),
                CONSTRAINT UQ_C2DraftLine_Sku UNIQUE (TenantId,DraftId,SkuId),
                CONSTRAINT FK_C2DraftLine_Draft FOREIGN KEY (TenantId,DraftId) REFERENCES dbo.C2OrderDraft(TenantId,Id),
                CONSTRAINT FK_C2DraftLine_Sku FOREIGN KEY (TenantId,SkuId) REFERENCES dbo.Sku(TenantId,Id)
            );
            CREATE TABLE dbo.C2CommercialPolicy (
                TenantId nvarchar(64) NOT NULL,
                Id uniqueidentifier NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                SkuId uniqueidentifier NOT NULL,
                Version int NOT NULL CHECK (Version>0),
                Decision varchar(12) NOT NULL CHECK (Decision IN ('APPROVED','PENDING','UNSET')),
                EffectiveFromUtc datetime2(7) NOT NULL,
                EffectiveUntilUtc datetime2(7) NOT NULL,
                Maker nvarchar(128) NOT NULL,
                Checker nvarchar(128) NULL,
                ApprovedAtUtc datetime2(7) NULL,
                EvidenceRef nvarchar(128) NOT NULL,
                UnitPrice decimal(19,4) NULL,
                DiscountPerUnit decimal(19,4) NULL,
                TaxRate decimal(9,6) NULL,
                TermsDays int NULL,
                CreditCap decimal(19,4) NULL,
                SellerTimeZone varchar(64) NULL,
                CONSTRAINT PK_C2CommercialPolicy PRIMARY KEY (TenantId,Id),
                CONSTRAINT UQ_C2Policy_ScopeVersion UNIQUE (TenantId,CustomerId,SkuId,Version),
                CONSTRAINT FK_C2Policy_Customer FOREIGN KEY (TenantId,CustomerId) REFERENCES dbo.Customer(TenantId,Id),
                CONSTRAINT FK_C2Policy_Sku FOREIGN KEY (TenantId,SkuId) REFERENCES dbo.Sku(TenantId,Id),
                CONSTRAINT CK_C2Policy_Window CHECK (EffectiveFromUtc<EffectiveUntilUtc)
            );
            CREATE TABLE dbo.C2Confirmation (
                TenantId nvarchar(64) NOT NULL,
                OrderId uniqueidentifier NOT NULL,
                CustomerId uniqueidentifier NOT NULL,
                SelectionId uniqueidentifier NOT NULL,
                ShipToSnapshotId uniqueidentifier NOT NULL,
                ConfirmSourceId uniqueidentifier NOT NULL,
                CONSTRAINT PK_C2Confirmation PRIMARY KEY (TenantId,OrderId),
                CONSTRAINT UQ_C2Confirmation_Selection UNIQUE (TenantId,SelectionId),
                CONSTRAINT FK_C2Confirmation_Draft FOREIGN KEY (TenantId,OrderId) REFERENCES dbo.C2OrderDraft(TenantId,Id),
                CONSTRAINT FK_C2Confirmation_Order FOREIGN KEY (TenantId,OrderId) REFERENCES dbo.AOrder(TenantId,Id),
                CONSTRAINT FK_C2Confirmation_Selection FOREIGN KEY (TenantId,CustomerId,SelectionId)
                    REFERENCES dbo.C1ShipToSelection(TenantId,CustomerId,Id),
                CONSTRAINT FK_C2Confirmation_ShipTo FOREIGN KEY (TenantId,CustomerId,ShipToSnapshotId)
                    REFERENCES dbo.ShipToSnapshot(TenantId,CustomerId,Id),
                CONSTRAINT FK_C2Confirmation_Source FOREIGN KEY (TenantId,ConfirmSourceId) REFERENCES dbo.BusinessSource(TenantId,Id)
            );
            CREATE TABLE dbo.C2LinePolicy (
                TenantId nvarchar(64) NOT NULL,
                OrderLineId uniqueidentifier NOT NULL,
                PolicyId uniqueidentifier NOT NULL,
                PolicyVersion int NOT NULL CHECK (PolicyVersion>0),
                SourceHash binary(32) NOT NULL,
                PolicyPayload nvarchar(3000) NOT NULL CHECK (ISJSON(PolicyPayload)=1),
                BaseUom nvarchar(24) NOT NULL,
                GrossAmount decimal(19,4) NOT NULL CHECK (GrossAmount>=0),
                CONSTRAINT PK_C2LinePolicy PRIMARY KEY (TenantId,OrderLineId),
                CONSTRAINT FK_C2LinePolicy_Approved FOREIGN KEY (TenantId,PolicyId) REFERENCES dbo.C2CommercialPolicy(TenantId,Id),
                CONSTRAINT FK_C2LinePolicy_Line FOREIGN KEY (TenantId,OrderLineId)
                    REFERENCES dbo.AConfirmedLineSnapshot(TenantId,AOrderLineId)
            );
            """);
        foreach (string table in new[] { "C2CommandScope", "C2OrderDraft", "C2OrderDraftLine", "C2CommercialPolicy", "C2Confirmation", "C2LinePolicy" })
            Execute.Sql($"""
                CREATE TRIGGER dbo.TR_{table}_Immutable ON dbo.{table} INSTEAD OF UPDATE, DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51023, 'C.2 source history is immutable.', 1;
                END;
                """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_C2DraftLine_OnlyDuringCreate ON dbo.C2OrderDraftLine AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (
                    SELECT 1 FROM inserted i
                    WHERE NOT EXISTS (
                        SELECT 1 FROM dbo.BusinessSource s JOIN dbo.BusinessCommand c
                            ON c.TenantId=s.TenantId AND c.Id=s.BusinessCommandId
                        JOIN dbo.C2CommandScope scope
                            ON scope.TenantId=c.TenantId AND scope.CommandRowId=c.Id AND scope.RoleCode='sales'
                        WHERE s.TenantId=i.TenantId AND s.Id=i.DraftId
                            AND s.SourceKind='C2_A_DRAFT' AND c.Status='RECEIVED'))
                    THROW 51025, 'C.2 draft lines can only be inserted during draft creation.', 1;
            END;
            """);
    }

    public override void Down() => throw new NotSupportedException("Forward-only application migration.");
}
