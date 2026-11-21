using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(202611210001, "Synthetic standalone cash receipt allocation lineage")]
public sealed class App01dCashAllocationSchema : Migration
{
    public override void Up()
    {
        Execute.Sql("ALTER TABLE dbo.ReceiptAllocation ADD AllocationSourceLineId uniqueidentifier NULL;");
        Execute.Sql("""
            ALTER TABLE dbo.ReceiptAllocation DROP CONSTRAINT UQ_ReceiptAllocation_Pair;
            ALTER TABLE dbo.ReceiptAllocation ADD CONSTRAINT FK_ReceiptAllocation_StandaloneSource
                FOREIGN KEY (TenantId,AllocationSourceLineId) REFERENCES dbo.BusinessSourceLine(TenantId,Id);
            CREATE UNIQUE INDEX UX_ReceiptAllocation_LegacyPair
                ON dbo.ReceiptAllocation (TenantId,CashReceiptId,ReceivableOpenItemId)
                WHERE AllocationSourceLineId IS NULL;
            CREATE UNIQUE INDEX UX_ReceiptAllocation_StandaloneSource
                ON dbo.ReceiptAllocation (TenantId,AllocationSourceLineId)
                WHERE AllocationSourceLineId IS NOT NULL;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_CashAllocation_SourceImmutable ON dbo.BusinessSource AFTER UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM deleted WHERE SourceKind=N'CASH_RECEIPT_ALLOCATION')
                    THROW 51092, 'Cash allocation source is immutable', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_CashAllocation_SourceLineImmutable ON dbo.BusinessSourceLine AFTER UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM deleted d JOIN dbo.ReceiptAllocation a
                           ON a.TenantId=d.TenantId AND a.AllocationSourceLineId=d.Id)
                    THROW 51093, 'Cash allocation source line is immutable', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_CashAllocation_LedgerImmutable ON dbo.LedgerEffect AFTER UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM deleted d JOIN dbo.ReceiptAllocation a
                           ON a.TenantId=d.TenantId AND a.AllocationSourceLineId=d.BusinessSourceLineId)
                    THROW 51094, 'Cash allocation ledger effect is immutable', 1;
            END;
            """);
    }

    public override void Down() => Execute.Sql("""
        IF EXISTS (SELECT 1 FROM dbo.ReceiptAllocation WHERE AllocationSourceLineId IS NOT NULL)
            THROW 51095, 'Cannot downgrade with recorded cash allocations', 1;
        DROP TRIGGER dbo.TR_CashAllocation_LedgerImmutable;
        DROP TRIGGER dbo.TR_CashAllocation_SourceLineImmutable;
        DROP TRIGGER dbo.TR_CashAllocation_SourceImmutable;
        DROP INDEX UX_ReceiptAllocation_StandaloneSource ON dbo.ReceiptAllocation;
        DROP INDEX UX_ReceiptAllocation_LegacyPair ON dbo.ReceiptAllocation;
        ALTER TABLE dbo.ReceiptAllocation DROP CONSTRAINT FK_ReceiptAllocation_StandaloneSource;
        ALTER TABLE dbo.ReceiptAllocation DROP COLUMN AllocationSourceLineId;
        ALTER TABLE dbo.ReceiptAllocation ADD CONSTRAINT UQ_ReceiptAllocation_Pair
            UNIQUE (TenantId,CashReceiptId,ReceivableOpenItemId);
        """);
}
