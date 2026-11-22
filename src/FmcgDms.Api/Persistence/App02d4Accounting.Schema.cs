namespace FmcgDms.Api.Persistence;

internal static partial class App02d4Accounting
{
    internal const string Schema = """
        CREATE TABLE d4accounting.Receipt (
            Id uniqueidentifier NOT NULL PRIMARY KEY,
            DeliveryId uniqueidentifier NOT NULL,
            SourceLineId uniqueidentifier NOT NULL UNIQUE,
            PayloadHash char(64) NOT NULL,
            Period char(7) NOT NULL,
            Quantity decimal(19,4) NOT NULL,
            Gross decimal(19,4) NOT NULL,
            Ar decimal(19,4) NOT NULL,
            Cost decimal(19,4) NOT NULL,
            ReceivedAt datetimeoffset(7) NOT NULL,
            TransportId nvarchar(100) NOT NULL
        );
        CREATE TABLE d4accounting.Posting (
            Id uniqueidentifier NOT NULL PRIMARY KEY,
            ReceiptId uniqueidentifier NOT NULL REFERENCES d4accounting.Receipt(Id),
            SourceLineId uniqueidentifier NOT NULL,
            Kind varchar(10) NOT NULL,
            OriginalId uniqueidentifier NULL REFERENCES d4accounting.Posting(Id),
            CorrectionId uniqueidentifier NULL,
            ReportId uniqueidentifier NULL,
            ApprovalId uniqueidentifier NULL,
            Period char(7) NOT NULL,
            Quantity decimal(19,4) NULL,
            Gross decimal(19,4) NULL,
            Ar decimal(19,4) NULL,
            Cost decimal(19,4) NULL,
            Mapping nvarchar(32) NULL,
            Scenario nvarchar(32) NOT NULL,
            PostedAt datetimeoffset(7) NOT NULL,
            CONSTRAINT CK_D4PostingKind CHECK (
                (Kind='BASE' AND OriginalId IS NULL AND CorrectionId IS NULL AND ReportId IS NULL AND ApprovalId IS NULL)
                OR (Kind='CORRECTION' AND OriginalId IS NOT NULL AND CorrectionId IS NOT NULL AND ReportId IS NOT NULL
                    AND ApprovalId IS NOT NULL AND Mapping IS NOT NULL AND Quantity IS NOT NULL AND Gross IS NOT NULL
                    AND Ar IS NOT NULL AND Cost IS NOT NULL))
        );
        CREATE UNIQUE INDEX UX_D4BasePosting ON d4accounting.Posting(SourceLineId) WHERE Kind='BASE';
        CREATE UNIQUE INDEX UX_D4CorrectionPosting ON d4accounting.Posting(CorrectionId) WHERE CorrectionId IS NOT NULL;
        CREATE TABLE d4accounting.Journal (
            Id bigint IDENTITY PRIMARY KEY,
            Operation nvarchar(16) NOT NULL,
            SourceLineId uniqueidentifier NOT NULL,
            TransportId nvarchar(100) NOT NULL,
            Outcome nvarchar(32) NOT NULL,
            RecordedAt datetimeoffset(7) NOT NULL
        );
        CREATE TABLE d4app.AccountingReport (
            Id uniqueidentifier NOT NULL PRIMARY KEY,
            FromUtc datetimeoffset(7) NOT NULL,
            Cutoff datetimeoffset(7) NOT NULL,
            AsOf datetimeoffset(7) NOT NULL,
            ExpectedCount int NULL,
            ReceivedCount int NOT NULL,
            PostedCount int NOT NULL,
            OkCount int NOT NULL,
            DiffCount int NOT NULL,
            PendingCount int NOT NULL,
            OutsideCount int NOT NULL,
            Status nvarchar(32) NOT NULL,
            Reason nvarchar(64) NULL,
            CreatedAt datetimeoffset(7) NOT NULL
        );
        CREATE TABLE d4app.AccountingReportLine (
            ReportId uniqueidentifier NOT NULL REFERENCES d4app.AccountingReport(Id),
            DeliveryId uniqueidentifier NOT NULL,
            SourceLineId uniqueidentifier NOT NULL,
            SourceHash char(64) NOT NULL,
            OccurredAt datetimeoffset(7) NOT NULL,
            Period char(7) NOT NULL,
            ReceiptId uniqueidentifier NULL,
            PostingId uniqueidentifier NULL,
            Status nvarchar(32) NOT NULL,
            Reason nvarchar(64) NULL,
            ExpectedQuantity decimal(19,4) NOT NULL,
            ExpectedGross decimal(19,4) NOT NULL,
            ExpectedAr decimal(19,4) NOT NULL,
            ExpectedCost decimal(19,4) NOT NULL,
            ActualQuantity decimal(19,4) NULL,
            ActualGross decimal(19,4) NULL,
            ActualAr decimal(19,4) NULL,
            ActualCost decimal(19,4) NULL,
            DiffQuantity decimal(19,4) NULL,
            DiffGross decimal(19,4) NULL,
            DiffAr decimal(19,4) NULL,
            DiffCost decimal(19,4) NULL,
            CONSTRAINT PK_D4AccountingReportLine PRIMARY KEY (ReportId,SourceLineId)
        );
        CREATE TABLE d4app.AccountingCase (
            Id uniqueidentifier NOT NULL PRIMARY KEY,
            ReportId uniqueidentifier NOT NULL,
            SourceLineId uniqueidentifier NOT NULL,
            OwnerRole nvarchar(32) NOT NULL,
            NextAction nvarchar(64) NOT NULL,
            ClosedReportId uniqueidentifier NULL REFERENCES d4app.AccountingReport(Id),
            ClosedBy nvarchar(128) NULL,
            ClosedAt datetimeoffset(7) NULL,
            CONSTRAINT UQ_D4CaseReportLine UNIQUE (ReportId,SourceLineId),
            CONSTRAINT FK_D4CaseReportLine FOREIGN KEY (ReportId,SourceLineId)
                REFERENCES d4app.AccountingReportLine(ReportId,SourceLineId)
        );
        CREATE TABLE d4app.AccountingCorrection (
            Id uniqueidentifier NOT NULL PRIMARY KEY,
            CaseId uniqueidentifier NOT NULL UNIQUE REFERENCES d4app.AccountingCase(Id),
            OriginalId uniqueidentifier NOT NULL UNIQUE,
            ReceiptId uniqueidentifier NOT NULL,
            ReportId uniqueidentifier NOT NULL,
            SourceLineId uniqueidentifier NOT NULL,
            Period char(7) NOT NULL,
            Quantity decimal(19,4) NOT NULL,
            Gross decimal(19,4) NOT NULL,
            Ar decimal(19,4) NOT NULL,
            Cost decimal(19,4) NOT NULL,
            Maker nvarchar(128) NOT NULL,
            Checker nvarchar(128) NULL,
            ApprovalId uniqueidentifier NULL,
            ApprovedAt datetimeoffset(7) NULL,
            PostingId uniqueidentifier NULL
        );
        """;
}
