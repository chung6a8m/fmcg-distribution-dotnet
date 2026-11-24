using FluentMigrator;

namespace FmcgDms.Api.Persistence;

[Migration(202611240005, "APP-03.7 Finance historical case linkage")]
[FluentMigrator.Tags(App03A1Profile.FinanceName)]
public sealed class App03FinanceCaseLinksSchema : Migration
{
    internal const long Version = 202611240005;

    public override void Up()
    {
        Execute.Sql("""
            CREATE VIEW d4app.FinanceCaseLinks AS
                SELECT c.Id,c.ReportId,c.SourceLineId,c.ClosedReportId,c.ClosedBy,
                    l.DeliveryId,l.Reason,l.OccurredAt,
                    COALESCE(canonical.Id,c.Id) AS CanonicalCaseId
                FROM d4app.AccountingCase c
                JOIN d4app.AccountingReportLine l ON l.ReportId=c.ReportId AND l.SourceLineId=c.SourceLineId
                LEFT JOIN d4app.FinanceCases canonical ON canonical.PostingId=l.PostingId
                    AND canonical.SourceLineId=l.SourceLineId AND canonical.SourceHash=l.SourceHash AND canonical.Reason=l.Reason;
            """);
        Execute.Sql("DENY INSERT,UPDATE,DELETE ON d4app.FinanceCaseLinks TO d4app;");
    }

    public override void Down() => throw new NotSupportedException("Scratch schemas are removed with the owned database.");
}
