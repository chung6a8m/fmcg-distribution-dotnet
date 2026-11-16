using FluentMigrator;

namespace FmcgDms.Api.Persistence;

/// <summary>
/// APP-01D schema: cash offer is non-posting; only an independently counted
/// cashier acceptance moves custody to fund. Unload moves the exact remaining
/// loaded stock back to the manifest warehouse and releases only unused B right.
/// Synthetic unitless evidence; no production cashier or closing integration.
/// </summary>
[Migration(202611160008, "APP-01D synthetic cashier handover and trip unload evidence")]
public sealed class App01dCashierUnloadSchema : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            ALTER TABLE dbo.BusinessSourceLine
                ADD CONSTRAINT UQ_APP01D_LineOwner
                UNIQUE (TenantId, Id, BusinessSourceId);

            -- Independently seeded/checker-owned evidence, not an HTTP
            -- "verified=true" flag. A witness can be consumed only once.
            CREATE TABLE dbo.CashierCountEvidence (
                TenantId nvarchar(64) NOT NULL,
                EvidenceRef nvarchar(128) NOT NULL,
                TripId uniqueidentifier NOT NULL,
                CountedAmount decimal(19,4) NOT NULL,
                CountedBy nvarchar(128) NOT NULL,
                VerificationStatus nvarchar(16) NOT NULL,
                CountedAtUtc datetime2(7) NOT NULL,
                RowVersion rowversion NOT NULL,
                CONSTRAINT PK_CashierCountEvidence PRIMARY KEY (TenantId, EvidenceRef),
                CONSTRAINT UQ_CashierCountEvidence_AcceptScope UNIQUE
                    (TenantId, EvidenceRef, TripId, CountedAmount, CountedBy),
                CONSTRAINT FK_CashierCountEvidence_Trip FOREIGN KEY (TenantId, TripId)
                    REFERENCES dbo.Trip (TenantId, Id),
                CONSTRAINT CK_CashierCountEvidence_Verified CHECK (
                    CountedAmount > 0 AND VerificationStatus=N'VERIFIED'
                    AND LEN(LTRIM(RTRIM(EvidenceRef)))>0
                    AND LEN(LTRIM(RTRIM(CountedBy)))>0)
            );

            -- Id is the CASH_HANDOVER_OFFER BusinessSource.Id, so consumers
            -- must use the immutable canonical source identity on acceptance.
            CREATE TABLE dbo.CashHandoverOffer (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_CashHandoverOffer PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                TripId uniqueidentifier NOT NULL,
                SourceKind nvarchar(48) NOT NULL
                    CONSTRAINT DF_CashHandoverOffer_Kind DEFAULT (N'CASH_HANDOVER_OFFER'),
                SourceLineId uniqueidentifier NOT NULL,
                OfferedAmount decimal(19,4) NOT NULL,
                OfferedBy nvarchar(128) NOT NULL,
                OfferedAtUtc datetime2(7) NOT NULL
                    CONSTRAINT DF_CashHandoverOffer_At DEFAULT (SYSUTCDATETIME()),
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_CashHandoverOffer_Scope
                    UNIQUE (TenantId, Id, TripId, OfferedAmount),
                CONSTRAINT UQ_CashHandoverOffer_SourceLine UNIQUE (TenantId, SourceLineId),
                CONSTRAINT FK_CashHandoverOffer_Source FOREIGN KEY
                    (TenantId, Id, TripId, SourceKind)
                    REFERENCES dbo.BusinessSource (TenantId, Id, TripId, SourceKind),
                CONSTRAINT FK_CashHandoverOffer_Line FOREIGN KEY
                    (TenantId, SourceLineId, Id)
                    REFERENCES dbo.BusinessSourceLine (TenantId, Id, BusinessSourceId),
                CONSTRAINT CK_CashHandoverOffer_Amount CHECK (
                    SourceKind=N'CASH_HANDOVER_OFFER' AND OfferedAmount>0
                    AND LEN(LTRIM(RTRIM(OfferedBy)))>0)
            );

            -- Acceptance is append-only, one per offer and one per physical
            -- cashier count. Composite FKs enforce exact Trip and count amount.
            CREATE TABLE dbo.CashHandoverAcceptance (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_CashHandoverAcceptance PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                TripId uniqueidentifier NOT NULL,
                OfferId uniqueidentifier NOT NULL,
                SourceKind nvarchar(48) NOT NULL
                    CONSTRAINT DF_CashHandoverAcceptance_Kind DEFAULT (N'CASH_HANDOVER_ACCEPT'),
                SourceLineId uniqueidentifier NOT NULL,
                EvidenceRef nvarchar(128) NOT NULL,
                AcceptedBy nvarchar(128) NOT NULL,
                AcceptedAmount decimal(19,4) NOT NULL,
                AcceptedAtUtc datetime2(7) NOT NULL
                    CONSTRAINT DF_CashHandoverAcceptance_At DEFAULT (SYSUTCDATETIME()),
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_CashHandoverAcceptance_Offer UNIQUE (TenantId, OfferId),
                CONSTRAINT UQ_CashHandoverAcceptance_Count UNIQUE (TenantId, EvidenceRef),
                CONSTRAINT FK_CashHandoverAcceptance_Offer FOREIGN KEY
                    (TenantId, OfferId, TripId, AcceptedAmount)
                    REFERENCES dbo.CashHandoverOffer (TenantId, Id, TripId, OfferedAmount),
                CONSTRAINT FK_CashHandoverAcceptance_Count FOREIGN KEY
                    (TenantId, EvidenceRef, TripId, AcceptedAmount, AcceptedBy)
                    REFERENCES dbo.CashierCountEvidence
                        (TenantId, EvidenceRef, TripId, CountedAmount, CountedBy),
                CONSTRAINT FK_CashHandoverAcceptance_Source FOREIGN KEY
                    (TenantId, Id, TripId, SourceKind)
                    REFERENCES dbo.BusinessSource (TenantId, Id, TripId, SourceKind),
                CONSTRAINT FK_CashHandoverAcceptance_Line FOREIGN KEY
                    (TenantId, SourceLineId, Id)
                    REFERENCES dbo.BusinessSourceLine (TenantId, Id, BusinessSourceId),
                CONSTRAINT CK_CashHandoverAcceptance_Valid CHECK (
                    SourceKind=N'CASH_HANDOVER_ACCEPT'
                    AND AcceptedAmount>0 AND LEN(LTRIM(RTRIM(AcceptedBy)))>0)
            );

            -- One terminal remainder unload per Trip/manifest. All loaded
            -- lines are accounted for from immutable load and delivery sources.
            CREATE TABLE dbo.TripUnload (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_TripUnload PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                TripId uniqueidentifier NOT NULL,
                TripManifestId uniqueidentifier NOT NULL,
                WarehouseLocationId uniqueidentifier NOT NULL,
                VehicleLocationId uniqueidentifier NOT NULL,
                SourceKind nvarchar(48) NOT NULL
                    CONSTRAINT DF_TripUnload_Kind DEFAULT (N'TRIP_UNLOAD'),
                RecordedAtUtc datetime2(7) NOT NULL
                    CONSTRAINT DF_TripUnload_At DEFAULT (SYSUTCDATETIME()),
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_TripUnload_Trip UNIQUE (TenantId, TripId),
                CONSTRAINT UQ_TripUnload_IdTrip UNIQUE (TenantId, Id, TripId),
                CONSTRAINT FK_TripUnload_Manifest FOREIGN KEY
                    (TenantId, TripManifestId, TripId, WarehouseLocationId, VehicleLocationId)
                    REFERENCES dbo.TripManifest
                        (TenantId, Id, TripId, WarehouseLocationId, VehicleLocationId),
                CONSTRAINT FK_TripUnload_Source FOREIGN KEY
                    (TenantId, Id, TripId, SourceKind)
                    REFERENCES dbo.BusinessSource (TenantId, Id, TripId, SourceKind),
                CONSTRAINT CK_TripUnload_Kind CHECK (SourceKind=N'TRIP_UNLOAD')
            );

            CREATE TABLE dbo.TripUnloadLine (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_TripUnloadLine PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                TripUnloadId uniqueidentifier NOT NULL,
                TripId uniqueidentifier NOT NULL,
                TripManifestLineId uniqueidentifier NOT NULL,
                LoadSourceLineId uniqueidentifier NOT NULL,
                StockRightId uniqueidentifier NOT NULL,
                RightKind char(1) NOT NULL,
                SkuId uniqueidentifier NOT NULL,
                SourceLineId uniqueidentifier NOT NULL,
                BaseQuantity decimal(18,3) NOT NULL,
                CostAmount decimal(19,4) NOT NULL,
                ReleasedBQuantity decimal(18,3) NOT NULL,
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_TripUnloadLine_Manifest UNIQUE (TenantId, TripManifestLineId),
                CONSTRAINT UQ_TripUnloadLine_SourceLine UNIQUE (TenantId, SourceLineId),
                CONSTRAINT FK_TripUnloadLine_Header FOREIGN KEY
                    (TenantId, TripUnloadId, TripId)
                    REFERENCES dbo.TripUnload (TenantId, Id, TripId),
                CONSTRAINT FK_TripUnloadLine_LoadedRight FOREIGN KEY
                    (TenantId, TripManifestLineId, TripId, SkuId, StockRightId,
                     RightKind, LoadSourceLineId)
                    REFERENCES dbo.TripManifestLine
                        (TenantId, Id, TripId, SkuId, StockRightId,
                         RightKind, LoadSourceLineId),
                CONSTRAINT FK_TripUnloadLine_SourceLine FOREIGN KEY
                    (TenantId, SourceLineId, TripUnloadId, SkuId, BaseQuantity, CostAmount)
                    REFERENCES dbo.BusinessSourceLine
                        (TenantId, Id, BusinessSourceId, SkuId, BaseQuantity, Amount),
                CONSTRAINT CK_TripUnloadLine_Amounts CHECK (
                    BaseQuantity>0 AND CostAmount>=0
                    AND ((RightKind='A' AND ReleasedBQuantity=0)
                      OR (RightKind='B' AND ReleasedBQuantity=BaseQuantity)))
            );
            CREATE INDEX IX_TripUnloadLine_Trip
                ON dbo.TripUnloadLine (TenantId, TripId, SkuId);
            """);

        foreach (string table in new[] {
            "CashierCountEvidence", "CashHandoverOffer",
            "CashHandoverAcceptance", "TripUnload", "TripUnloadLine"
        })
        {
            Execute.Sql($"""
                CREATE TRIGGER dbo.TR_APP01D_{table}_Immutable
                ON dbo.{table} AFTER UPDATE, DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM deleted)
                        THROW 51081, 'APP01D accepted/recorded evidence is immutable', 1;
                END;
                """);
        }
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_APP01D_SourceImmutable
            ON dbo.BusinessSource AFTER UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM deleted WHERE SourceKind IN
                    (N'CASH_HANDOVER_OFFER',N'CASH_HANDOVER_ACCEPT',N'TRIP_UNLOAD'))
                    THROW 51082, 'APP01D business source is immutable', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_APP01D_SourceLineImmutable
            ON dbo.BusinessSourceLine AFTER UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (
                    SELECT 1 FROM deleted d
                    JOIN dbo.BusinessSource s ON s.TenantId=d.TenantId
                        AND s.Id=d.BusinessSourceId
                    WHERE s.SourceKind IN
                        (N'CASH_HANDOVER_OFFER',N'CASH_HANDOVER_ACCEPT',N'TRIP_UNLOAD'))
                    THROW 51083, 'APP01D source line is immutable', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_APP01D_LedgerImmutable
            ON dbo.LedgerEffect AFTER UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (
                    SELECT 1 FROM deleted d
                    JOIN dbo.BusinessSourceLine sl ON sl.TenantId=d.TenantId
                        AND sl.Id=d.BusinessSourceLineId
                    JOIN dbo.BusinessSource s ON s.TenantId=sl.TenantId
                        AND s.Id=sl.BusinessSourceId
                    WHERE s.SourceKind IN
                        (N'CASH_HANDOVER_OFFER',N'CASH_HANDOVER_ACCEPT',N'TRIP_UNLOAD'))
                    THROW 51084, 'APP01D ledger is immutable', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_APP01D_OfferNoLedger
            ON dbo.LedgerEffect AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (
                    SELECT 1 FROM inserted i
                    JOIN dbo.BusinessSourceLine sl ON sl.TenantId=i.TenantId
                        AND sl.Id=i.BusinessSourceLineId
                    JOIN dbo.BusinessSource s ON s.TenantId=sl.TenantId
                        AND s.Id=sl.BusinessSourceId
                    WHERE s.SourceKind=N'CASH_HANDOVER_OFFER')
                    THROW 51085, 'APP01D offer cannot post monetary or stock effects', 1;
            END;
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DROP TRIGGER IF EXISTS dbo.TR_APP01D_OfferNoLedger;
            DROP TRIGGER IF EXISTS dbo.TR_APP01D_LedgerImmutable;
            DROP TRIGGER IF EXISTS dbo.TR_APP01D_SourceLineImmutable;
            DROP TRIGGER IF EXISTS dbo.TR_APP01D_SourceImmutable;
            DROP TRIGGER IF EXISTS dbo.TR_APP01D_TripUnloadLine_Immutable;
            DROP TRIGGER IF EXISTS dbo.TR_APP01D_TripUnload_Immutable;
            DROP TRIGGER IF EXISTS dbo.TR_APP01D_CashHandoverAcceptance_Immutable;
            DROP TRIGGER IF EXISTS dbo.TR_APP01D_CashHandoverOffer_Immutable;
            DROP TRIGGER IF EXISTS dbo.TR_APP01D_CashierCountEvidence_Immutable;
            DROP TABLE dbo.TripUnloadLine;
            DROP TABLE dbo.TripUnload;
            DROP TABLE dbo.CashHandoverAcceptance;
            DROP TABLE dbo.CashHandoverOffer;
            DROP TABLE dbo.CashierCountEvidence;
            ALTER TABLE dbo.BusinessSourceLine DROP CONSTRAINT UQ_APP01D_LineOwner;
            """);
    }
}
