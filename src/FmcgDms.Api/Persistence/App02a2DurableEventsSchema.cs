using FluentMigrator;

namespace FmcgDms.Api.Persistence;

/// <summary>
/// APP-02A.2 synthetic, nonproduction-only durable event envelope, dependency,
/// CASE, outbox and synthetic consumer SQL model. No event endpoints, workers,
/// dispatch, automatic financial posting or production integration.
/// </summary>
[Migration(202611170001, "APP-02A.2 durable synthetic P/C inbox, outbox and CASE")]
public sealed class App02a2DurableEventsSchema : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            -- Existing APP-01D acceptance stays authoritative. This composite
            -- witness lets A.2 link the exact source/offer/trip/count/actor.
            ALTER TABLE dbo.CashHandoverAcceptance ADD
                CONSTRAINT UQ_APP02_AcceptanceBound
                UNIQUE (TenantId,Id,OfferId,TripId,EvidenceRef,AcceptedBy);

            CREATE TABLE dbo.App02EventReceipt (
                ReceiptId uniqueidentifier NOT NULL
                    CONSTRAINT PK_App02EventReceipt PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                ProducerId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                EventId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                EventKind nvarchar(48) COLLATE Latin1_General_100_BIN2 NOT NULL,
                ActorId nvarchar(128) NOT NULL,
                TripId uniqueidentifier NOT NULL,
                OfferId uniqueidentifier NULL,
                OfferedAmount decimal(19,4) NULL,
                PrerequisiteProducerId nvarchar(128)
                    COLLATE Latin1_General_100_BIN2 NULL,
                PrerequisiteEventId nvarchar(128)
                    COLLATE Latin1_General_100_BIN2 NULL,
                CountEvidenceRef nvarchar(128) NULL,
                OccurredAtUtc datetime2(7) NOT NULL,
                ReceivedAtUtc datetime2(7) NOT NULL
                    CONSTRAINT DF_App02Receipt_Received DEFAULT (SYSUTCDATETIME()),
                PayloadVersion smallint NOT NULL,
                CanonicalBytes varbinary(max) NOT NULL,
                PayloadHash binary(32) NOT NULL,
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_App02Receipt_TenantId UNIQUE (TenantId,ReceiptId),
                -- Hash and EventKind deliberately NOT in idempotency identity.
                CONSTRAINT UQ_App02Receipt_Identity UNIQUE (TenantId,ProducerId,EventId),
                CONSTRAINT UQ_App02Receipt_CReference UNIQUE (
                    TenantId,ReceiptId,EventKind,TripId,CountEvidenceRef,
                    PrerequisiteProducerId,PrerequisiteEventId,ActorId),
                CONSTRAINT UQ_App02Receipt_PReference UNIQUE (
                    TenantId,ProducerId,EventId,EventKind,TripId,ReceiptId,OfferId),
                CONSTRAINT FK_App02Receipt_Tenant FOREIGN KEY (TenantId)
                    REFERENCES dbo.AppTenantBinding (TenantId),
                CONSTRAINT FK_App02Receipt_Trip FOREIGN KEY (TenantId,TripId)
                    REFERENCES dbo.Trip (TenantId,Id),
                -- NULL composite FK on C: no phantom P is manufactured.
                CONSTRAINT FK_App02Receipt_RealOffer FOREIGN KEY
                    (TenantId,OfferId,TripId,OfferedAmount)
                    REFERENCES dbo.CashHandoverOffer
                        (TenantId,Id,TripId,OfferedAmount),
                CONSTRAINT CK_App02Receipt_Identity CHECK (
                    LEN(LTRIM(RTRIM(ProducerId)))>0
                    AND LEN(LTRIM(RTRIM(EventId)))>0
                    AND LEN(LTRIM(RTRIM(ActorId)))>0),
                CONSTRAINT CK_App02Receipt_KindShape CHECK (
                    (EventKind=N'CashHandoverOffered'
                        AND OfferId IS NOT NULL AND OfferedAmount>0
                        AND PrerequisiteProducerId IS NULL
                        AND PrerequisiteEventId IS NULL
                        AND CountEvidenceRef IS NULL)
                    OR
                    (EventKind=N'CashierAcceptanceRequested'
                        AND OfferId IS NULL AND OfferedAmount IS NULL
                        AND PrerequisiteProducerId IS NOT NULL
                        AND PrerequisiteEventId IS NOT NULL
                        AND CountEvidenceRef IS NOT NULL
                        AND LEN(LTRIM(RTRIM(PrerequisiteProducerId)))>0
                        AND LEN(LTRIM(RTRIM(PrerequisiteEventId)))>0
                        AND LEN(LTRIM(RTRIM(CountEvidenceRef)))>0)),
                CONSTRAINT CK_App02Receipt_Hash CHECK (
                    PayloadVersion=1
                    AND DATALENGTH(CanonicalBytes) BETWEEN 1 AND 4096
                    AND PayloadHash=CONVERT(binary(32),
                        HASHBYTES('SHA2_256',CanonicalBytes)))
            );
            CREATE UNIQUE INDEX UX_App02Receipt_Offer
                ON dbo.App02EventReceipt(TenantId,OfferId)
                WHERE OfferId IS NOT NULL;

            CREATE TABLE dbo.App02EventProgress (
                TenantId nvarchar(64) NOT NULL,
                ReceiptId uniqueidentifier NOT NULL,
                Status nvarchar(24) NOT NULL
                    CONSTRAINT DF_App02Progress_Status DEFAULT (N'RECEIVED'),
                TechnicalRetryCount tinyint NOT NULL
                    CONSTRAINT DF_App02Progress_Retry DEFAULT (0),
                NextAttemptAtUtc datetime2(7) NULL,
                LastAttemptAtUtc datetime2(7) NULL,
                ReasonCode nvarchar(64) NULL,
                StateChangedAtUtc datetime2(7) NOT NULL
                    CONSTRAINT DF_App02Progress_Changed DEFAULT (SYSUTCDATETIME()),
                RowVersion rowversion NOT NULL,
                CONSTRAINT PK_App02EventProgress PRIMARY KEY (TenantId,ReceiptId),
                CONSTRAINT FK_App02Progress_Receipt FOREIGN KEY (TenantId,ReceiptId)
                    REFERENCES dbo.App02EventReceipt (TenantId,ReceiptId),
                CONSTRAINT CK_App02Progress_Status CHECK (
                    Status IN (N'RECEIVED',N'PENDING_DEPENDENCY',
                               N'APPLIED',N'REJECTED',N'CASE')),
                CONSTRAINT CK_App02Progress_Retry CHECK (TechnicalRetryCount<=6),
                CONSTRAINT CK_App02Progress_Reason CHECK (
                    (Status IN (N'CASE',N'REJECTED') AND ReasonCode IS NOT NULL)
                    OR (Status NOT IN (N'CASE',N'REJECTED'))),
                CONSTRAINT CK_App02Progress_Applied CHECK (
                    Status<>N'APPLIED' OR
                    (ReasonCode IS NULL AND NextAttemptAtUtc IS NULL))
            );
            CREATE INDEX IX_App02Progress_Due
                ON dbo.App02EventProgress(TenantId,Status,NextAttemptAtUtc);

            -- P/C event identities are resolved only after the real committed
            -- APP-01D offer exists. A pending C has no prerequisite FK.
            CREATE TABLE dbo.App02ResolvedDependency (
                TenantId nvarchar(64) NOT NULL,
                CReceiptId uniqueidentifier NOT NULL,
                CEventKind nvarchar(48) COLLATE Latin1_General_100_BIN2
                    NOT NULL CONSTRAINT DF_App02Resolution_CKind
                    DEFAULT (N'CashierAcceptanceRequested'),
                TripId uniqueidentifier NOT NULL,
                CountEvidenceRef nvarchar(128) NOT NULL,
                PProducerId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                PEventId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
                CActorId nvarchar(128) NOT NULL,
                PReceiptId uniqueidentifier NOT NULL,
                PEventKind nvarchar(48) COLLATE Latin1_General_100_BIN2
                    NOT NULL CONSTRAINT DF_App02Resolution_PKind
                    DEFAULT (N'CashHandoverOffered'),
                OfferId uniqueidentifier NOT NULL,
                ResolvedAtUtc datetime2(7) NOT NULL
                    CONSTRAINT DF_App02Resolution_At DEFAULT (SYSUTCDATETIME()),
                RowVersion rowversion NOT NULL,
                CONSTRAINT PK_App02ResolvedDependency PRIMARY KEY (TenantId,CReceiptId),
                CONSTRAINT UQ_App02Resolution_AppReference UNIQUE
                    (TenantId,CReceiptId,PReceiptId,TripId,
                     OfferId,CountEvidenceRef,CActorId),
                CONSTRAINT FK_App02Resolution_C FOREIGN KEY
                    (TenantId,CReceiptId,CEventKind,TripId,CountEvidenceRef,
                     PProducerId,PEventId,CActorId)
                    REFERENCES dbo.App02EventReceipt
                        (TenantId,ReceiptId,EventKind,TripId,CountEvidenceRef,
                         PrerequisiteProducerId,PrerequisiteEventId,ActorId),
                CONSTRAINT FK_App02Resolution_P FOREIGN KEY
                    (TenantId,PProducerId,PEventId,PEventKind,TripId,PReceiptId,OfferId)
                    REFERENCES dbo.App02EventReceipt
                        (TenantId,ProducerId,EventId,EventKind,TripId,ReceiptId,OfferId),
                CONSTRAINT CK_App02Resolution_Kind CHECK (
                    CEventKind=N'CashierAcceptanceRequested'
                    AND PEventKind=N'CashHandoverOffered'
                    AND CReceiptId<>PReceiptId)
            );
            CREATE INDEX IX_App02Resolution_P
                ON dbo.App02ResolvedDependency(TenantId,PReceiptId);

            -- An application cannot exist before the real APP-01D acceptance.
            -- Two different C ids may be pending but cannot apply one P twice.
            CREATE TABLE dbo.App02AcceptanceApplication (
                TenantId nvarchar(64) NOT NULL,
                CReceiptId uniqueidentifier NOT NULL,
                PReceiptId uniqueidentifier NOT NULL,
                TripId uniqueidentifier NOT NULL,
                OfferId uniqueidentifier NOT NULL,
                CountEvidenceRef nvarchar(128) NOT NULL,
                CActorId nvarchar(128) NOT NULL,
                AcceptanceId uniqueidentifier NOT NULL,
                MessageId uniqueidentifier NOT NULL,
                AppliedAtUtc datetime2(7) NOT NULL
                    CONSTRAINT DF_App02Application_At DEFAULT (SYSUTCDATETIME()),
                RowVersion rowversion NOT NULL,
                CONSTRAINT PK_App02AcceptanceApplication
                    PRIMARY KEY (TenantId,CReceiptId),
                CONSTRAINT UQ_App02Application_P UNIQUE (TenantId,PReceiptId),
                CONSTRAINT UQ_App02Application_Offer UNIQUE (TenantId,OfferId),
                CONSTRAINT UQ_App02Application_Acceptance UNIQUE (TenantId,AcceptanceId),
                CONSTRAINT UQ_App02Application_Message UNIQUE (TenantId,MessageId),
                CONSTRAINT UQ_App02Application_OutboxReference UNIQUE
                    (TenantId,CReceiptId,MessageId,AcceptanceId),
                CONSTRAINT FK_App02Application_Resolution FOREIGN KEY
                    (TenantId,CReceiptId,PReceiptId,TripId,OfferId,
                     CountEvidenceRef,CActorId)
                    REFERENCES dbo.App02ResolvedDependency
                        (TenantId,CReceiptId,PReceiptId,TripId,OfferId,
                         CountEvidenceRef,CActorId),
                CONSTRAINT FK_App02Application_App01dAcceptance FOREIGN KEY
                    (TenantId,AcceptanceId,OfferId,TripId,CountEvidenceRef,CActorId)
                    REFERENCES dbo.CashHandoverAcceptance
                        (TenantId,Id,OfferId,TripId,EvidenceRef,AcceptedBy)
            );

            CREATE TABLE dbo.App02OutboxMessage (
                TenantId nvarchar(64) NOT NULL,
                MessageId uniqueidentifier NOT NULL,
                CReceiptId uniqueidentifier NOT NULL,
                AcceptanceId uniqueidentifier NOT NULL,
                MessageKind nvarchar(64) NOT NULL
                    CONSTRAINT DF_App02Outbox_Kind
                    DEFAULT (N'CashHandoverAcceptanceApplied'),
                PayloadVersion smallint NOT NULL,
                CanonicalBytes varbinary(max) NOT NULL,
                PayloadHash binary(32) NOT NULL,
                CreatedAtUtc datetime2(7) NOT NULL
                    CONSTRAINT DF_App02Outbox_Created DEFAULT (SYSUTCDATETIME()),
                RowVersion rowversion NOT NULL,
                CONSTRAINT PK_App02OutboxMessage PRIMARY KEY (TenantId,MessageId),
                CONSTRAINT UQ_App02Outbox_C UNIQUE (TenantId,CReceiptId),
                CONSTRAINT UQ_App02Outbox_DedupReference
                    UNIQUE (TenantId,MessageId,PayloadVersion,PayloadHash),
                CONSTRAINT FK_App02Outbox_Application FOREIGN KEY
                    (TenantId,CReceiptId,MessageId,AcceptanceId)
                    REFERENCES dbo.App02AcceptanceApplication
                        (TenantId,CReceiptId,MessageId,AcceptanceId),
                CONSTRAINT CK_App02Outbox_Kind CHECK (
                    MessageKind=N'CashHandoverAcceptanceApplied'),
                CONSTRAINT CK_App02Outbox_Hash CHECK (
                    PayloadVersion=1 AND DATALENGTH(CanonicalBytes) BETWEEN 1 AND 4096
                    AND PayloadHash=CONVERT(binary(32),
                        HASHBYTES('SHA2_256',CanonicalBytes)))
            );

            CREATE TABLE dbo.App02OutboxDelivery (
                TenantId nvarchar(64) NOT NULL,
                MessageId uniqueidentifier NOT NULL,
                Status nvarchar(16) NOT NULL
                    CONSTRAINT DF_App02Delivery_Status DEFAULT (N'PENDING'),
                AttemptCount tinyint NOT NULL
                    CONSTRAINT DF_App02Delivery_Attempts DEFAULT (0),
                NextAttemptAtUtc datetime2(7) NULL,
                LastAttemptAtUtc datetime2(7) NULL,
                LeaseOwner nvarchar(128) NULL,
                LeaseExpiresAtUtc datetime2(7) NULL,
                AckAtUtc datetime2(7) NULL,
                LastErrorCode nvarchar(64) NULL,
                RowVersion rowversion NOT NULL,
                CONSTRAINT PK_App02OutboxDelivery PRIMARY KEY (TenantId,MessageId),
                CONSTRAINT FK_App02Delivery_Message FOREIGN KEY (TenantId,MessageId)
                    REFERENCES dbo.App02OutboxMessage (TenantId,MessageId),
                CONSTRAINT CK_App02Delivery_Attempts CHECK (AttemptCount<=6),
                CONSTRAINT CK_App02Delivery_State CHECK (
                    (Status=N'LEASED' AND LeaseOwner IS NOT NULL
                        AND LeaseExpiresAtUtc IS NOT NULL AND AckAtUtc IS NULL)
                    OR (Status=N'ACKED' AND LeaseOwner IS NULL
                        AND LeaseExpiresAtUtc IS NULL AND AckAtUtc IS NOT NULL)
                    OR (Status IN (N'PENDING',N'CASE') AND LeaseOwner IS NULL
                        AND LeaseExpiresAtUtc IS NULL AND AckAtUtc IS NULL))
            );
            CREATE INDEX IX_App02Delivery_Due
                ON dbo.App02OutboxDelivery(TenantId,Status,NextAttemptAtUtc);

            -- Durable recipient ACK is a separate committed SQL fact. The
            -- hash+version FK rejects forged same-ID, changed-content receipts.
            CREATE TABLE dbo.App02SyntheticConsumerInbox (
                TenantId nvarchar(64) NOT NULL,
                MessageId uniqueidentifier NOT NULL,
                ConsumerReceiptId uniqueidentifier NOT NULL,
                ConsumerId nvarchar(64) NOT NULL
                    CONSTRAINT DF_App02Consumer_Id DEFAULT (N'SYNTHETIC_CONSUMER'),
                PayloadVersion smallint NOT NULL,
                PayloadHash binary(32) NOT NULL,
                ReceivedAtUtc datetime2(7) NOT NULL
                    CONSTRAINT DF_App02Consumer_Received DEFAULT (SYSUTCDATETIME()),
                RowVersion rowversion NOT NULL,
                CONSTRAINT PK_App02SyntheticConsumerInbox PRIMARY KEY
                    (TenantId,MessageId),
                CONSTRAINT UQ_App02Consumer_Receipt UNIQUE (TenantId,ConsumerReceiptId),
                CONSTRAINT FK_App02Consumer_Message FOREIGN KEY
                    (TenantId,MessageId,PayloadVersion,PayloadHash)
                    REFERENCES dbo.App02OutboxMessage
                        (TenantId,MessageId,PayloadVersion,PayloadHash),
                CONSTRAINT CK_App02Consumer_Id CHECK (
                    ConsumerId=N'SYNTHETIC_CONSUMER')
            );

            CREATE TABLE dbo.App02ExceptionCase (
                CaseId uniqueidentifier NOT NULL
                    CONSTRAINT PK_App02ExceptionCase PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                ReceiptId uniqueidentifier NOT NULL,
                AsOfUtc datetime2(7) NOT NULL,
                ReasonCode nvarchar(64) NOT NULL,
                OwnerQueue nvarchar(64) NOT NULL
                    CONSTRAINT DF_App02Case_Owner DEFAULT (N'CASHIER_SUPERVISION'),
                NextAction nvarchar(256) NOT NULL,
                OpenedAtUtc datetime2(7) NOT NULL
                    CONSTRAINT DF_App02Case_Opened DEFAULT (SYSUTCDATETIME()),
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_App02Case_TenantId UNIQUE (TenantId,CaseId),
                CONSTRAINT FK_App02Case_CReceipt FOREIGN KEY (TenantId,ReceiptId)
                    REFERENCES dbo.App02EventReceipt (TenantId,ReceiptId),
                CONSTRAINT CK_App02Case_Owner CHECK (
                    OwnerQueue=N'CASHIER_SUPERVISION'
                    AND LEN(LTRIM(RTRIM(ReasonCode)))>0
                    AND LEN(LTRIM(RTRIM(NextAction)))>0)
            );
            CREATE INDEX IX_App02Case_Queue
                ON dbo.App02ExceptionCase(TenantId,ReceiptId,OpenedAtUtc);

            CREATE TABLE dbo.App02CaseDisposition (
                DispositionId uniqueidentifier NOT NULL
                    CONSTRAINT PK_App02CaseDisposition PRIMARY KEY,
                TenantId nvarchar(64) NOT NULL,
                CaseId uniqueidentifier NOT NULL,
                MakerActorId nvarchar(128) NOT NULL,
                CheckerActorId nvarchar(128) NOT NULL,
                Decision nvarchar(24) NOT NULL,
                ReasonCode nvarchar(64) NOT NULL,
                EvidenceRef nvarchar(256) NOT NULL,
                ProposedResolution nvarchar(512) NOT NULL,
                ApprovedAtUtc datetime2(7) NOT NULL,
                RowVersion rowversion NOT NULL,
                CONSTRAINT UQ_App02Disposition_TenantId
                    UNIQUE (TenantId,DispositionId),
                CONSTRAINT FK_App02Disposition_Case FOREIGN KEY (TenantId,CaseId)
                    REFERENCES dbo.App02ExceptionCase (TenantId,CaseId),
                CONSTRAINT CK_App02Disposition_Decision CHECK (
                    Decision IN (N'HOLD',N'CLOSE',N'APPROVED_RESUME')),
                CONSTRAINT CK_App02Disposition_Distinct CHECK (
                    MakerActorId<>CheckerActorId
                    AND LEN(LTRIM(RTRIM(MakerActorId)))>0
                    AND LEN(LTRIM(RTRIM(CheckerActorId)))>0
                    AND LEN(LTRIM(RTRIM(ReasonCode)))>0
                    AND LEN(LTRIM(RTRIM(EvidenceRef)))>0
                    AND LEN(LTRIM(RTRIM(ProposedResolution)))>0)
            );

            -- A correction is a NEW BusinessSource, never a rewritten original.
            -- Its independent authorization/ledger execution belongs to A.3+.
            CREATE TABLE dbo.App02CorrectionLink (
                TenantId nvarchar(64) NOT NULL,
                CorrectionSourceId uniqueidentifier NOT NULL,
                OriginalAcceptanceId uniqueidentifier NOT NULL,
                MakerActorId nvarchar(128) NOT NULL,
                CheckerActorId nvarchar(128) NOT NULL,
                ReasonCode nvarchar(64) NOT NULL,
                EvidenceRef nvarchar(256) NOT NULL,
                LinkedAtUtc datetime2(7) NOT NULL
                    CONSTRAINT DF_App02Correction_Linked DEFAULT (SYSUTCDATETIME()),
                RowVersion rowversion NOT NULL,
                CONSTRAINT PK_App02CorrectionLink
                    PRIMARY KEY (TenantId,CorrectionSourceId),
                CONSTRAINT FK_App02Correction_NewSource FOREIGN KEY
                    (TenantId,CorrectionSourceId)
                    REFERENCES dbo.BusinessSource (TenantId,Id),
                CONSTRAINT FK_App02Correction_Original FOREIGN KEY
                    (TenantId,OriginalAcceptanceId)
                    REFERENCES dbo.App02AcceptanceApplication (TenantId,AcceptanceId),
                CONSTRAINT CK_App02Correction_Independent CHECK (
                    CorrectionSourceId<>OriginalAcceptanceId
                    AND MakerActorId<>CheckerActorId
                    AND LEN(LTRIM(RTRIM(ReasonCode)))>0
                    AND LEN(LTRIM(RTRIM(EvidenceRef)))>0)
            );
            """);

        // Envelope, identity, receipt lineage and case/consumer history
        // are never mutable. Progress/lease state lives in separate tables.
        foreach (string table in new[]
        {
            "App02EventReceipt", "App02ResolvedDependency",
            "App02AcceptanceApplication", "App02OutboxMessage",
            "App02SyntheticConsumerInbox", "App02ExceptionCase",
            "App02CaseDisposition", "App02CorrectionLink"
        })
        {
            Execute.Sql($"""
                CREATE TRIGGER dbo.TR_APP02_{table}_Immutable
                ON dbo.{table} AFTER UPDATE, DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM deleted)
                        THROW 51120, 'APP02 immutable source/receipt/evidence', 1;
                END;
                """);
        }

        Execute.Sql("""
            CREATE TRIGGER dbo.TR_APP02_CaseKind
            ON dbo.App02ExceptionCase AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (
                    SELECT 1 FROM inserted i
                    JOIN dbo.App02EventReceipt r ON r.TenantId=i.TenantId
                        AND r.ReceiptId=i.ReceiptId
                    WHERE r.EventKind<>N'CashierAcceptanceRequested')
                    THROW 51130, 'APP02 only C dependency creates CASE', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_APP02_CaseChecker
            ON dbo.App02CaseDisposition AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (
                    SELECT 1 FROM inserted i
                    JOIN dbo.App02ExceptionCase c ON c.TenantId=i.TenantId
                        AND c.CaseId=i.CaseId
                    JOIN dbo.App02EventReceipt r ON r.TenantId=c.TenantId
                        AND r.ReceiptId=c.ReceiptId
                    WHERE i.CheckerActorId=r.ActorId)
                    THROW 51131, 'APP02 Checker must differ from C actor', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_APP02_CorrectionGuard
            ON dbo.App02CorrectionLink AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (
                    SELECT 1 FROM inserted i
                    JOIN dbo.BusinessSource s ON s.TenantId=i.TenantId
                        AND s.Id=i.CorrectionSourceId
                    JOIN dbo.App02AcceptanceApplication a ON a.TenantId=i.TenantId
                        AND a.AcceptanceId=i.OriginalAcceptanceId
                    JOIN dbo.App02EventReceipt r ON r.TenantId=a.TenantId
                        AND r.ReceiptId=a.CReceiptId
                    WHERE s.SourceKind<>N'CASH_HANDOVER_CORRECTION'
                        OR i.CheckerActorId=r.ActorId)
                    THROW 51132, 'APP02 correction requires new source and independent Checker', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_APP02_CorrectionSourceImmutable
            ON dbo.BusinessSource AFTER UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (
                    SELECT 1 FROM deleted d
                    LEFT JOIN inserted i ON i.Id=d.Id
                    WHERE d.SourceKind=N'CASH_HANDOVER_CORRECTION'
                        OR i.SourceKind=N'CASH_HANDOVER_CORRECTION')
                    THROW 51133, 'APP02 correction source is immutable', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_APP02_CorrectionSourceLineImmutable
            ON dbo.BusinessSourceLine AFTER UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (
                    SELECT 1 FROM deleted d
                    JOIN dbo.BusinessSource s ON s.TenantId=d.TenantId
                        AND s.Id=d.BusinessSourceId
                    WHERE s.SourceKind=N'CASH_HANDOVER_CORRECTION')
                    THROW 51134, 'APP02 correction source line is immutable', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_APP02_CorrectionLedgerImmutable
            ON dbo.LedgerEffect AFTER UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (
                    SELECT 1 FROM deleted d
                    JOIN dbo.BusinessSourceLine sl ON sl.TenantId=d.TenantId
                        AND sl.Id=d.BusinessSourceLineId
                    JOIN dbo.BusinessSource s ON s.TenantId=sl.TenantId
                        AND s.Id=sl.BusinessSourceId
                    WHERE s.SourceKind=N'CASH_HANDOVER_CORRECTION')
                    THROW 51135, 'APP02 correction ledger is immutable', 1;
            END;
            """);

        Execute.Sql("""
            CREATE TRIGGER dbo.TR_APP02_ProgressGuard
            ON dbo.App02EventProgress AFTER INSERT, UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (
                    SELECT 1 FROM deleted d
                    WHERE NOT EXISTS (
                        SELECT 1 FROM inserted i
                        WHERE i.TenantId=d.TenantId AND i.ReceiptId=d.ReceiptId))
                    THROW 51140, 'APP02 progress cannot be deleted/rekeyed', 1;
                IF EXISTS (
                    SELECT 1 FROM inserted i LEFT JOIN deleted d
                        ON d.TenantId=i.TenantId AND d.ReceiptId=i.ReceiptId
                    WHERE d.ReceiptId IS NULL AND i.Status<>N'RECEIVED')
                    THROW 51141, 'APP02 initial progress must be RECEIVED', 1;
                IF EXISTS (
                    SELECT 1 FROM inserted i
                    JOIN deleted d ON d.TenantId=i.TenantId
                        AND d.ReceiptId=i.ReceiptId
                    WHERE i.TechnicalRetryCount<d.TechnicalRetryCount
                        OR i.StateChangedAtUtc<d.StateChangedAtUtc
                        OR (i.Status<>d.Status AND NOT (
                          (d.Status IN (N'RECEIVED',N'PENDING_DEPENDENCY')
                            AND i.Status IN (N'RECEIVED',N'PENDING_DEPENDENCY',
                                             N'APPLIED',N'REJECTED',N'CASE'))
                          OR (d.Status=N'CASE' AND i.Status=N'RECEIVED'))))
                    THROW 51142, 'APP02 illegal progress transition', 1;
                IF EXISTS (
                    SELECT 1 FROM inserted i
                    JOIN dbo.App02EventReceipt r ON r.TenantId=i.TenantId
                        AND r.ReceiptId=i.ReceiptId
                    WHERE i.Status=N'PENDING_DEPENDENCY'
                        AND r.EventKind<>N'CashierAcceptanceRequested')
                    THROW 51143, 'APP02 P cannot be pending dependency', 1;
                IF EXISTS (
                    SELECT 1 FROM inserted i
                    WHERE i.Status=N'CASE' AND NOT EXISTS (
                        SELECT 1 FROM dbo.App02ExceptionCase c
                        WHERE c.TenantId=i.TenantId AND c.ReceiptId=i.ReceiptId))
                    THROW 51144, 'APP02 CASE requires durable owned case', 1;
                IF EXISTS (
                    SELECT 1 FROM inserted i JOIN deleted d
                      ON d.TenantId=i.TenantId AND d.ReceiptId=i.ReceiptId
                    WHERE d.Status=N'CASE' AND i.Status=N'RECEIVED'
                        AND NOT EXISTS (
                            SELECT 1 FROM dbo.App02CaseDisposition z
                            JOIN dbo.App02ExceptionCase c ON c.TenantId=z.TenantId
                                AND c.CaseId=z.CaseId
                            WHERE c.TenantId=i.TenantId AND c.ReceiptId=i.ReceiptId
                                AND z.Decision=N'APPROVED_RESUME'))
                    THROW 51145, 'APP02 CASE resume requires independent approval', 1;
                IF EXISTS (
                    SELECT 1 FROM inserted i
                    JOIN dbo.App02EventReceipt r ON r.TenantId=i.TenantId
                        AND r.ReceiptId=i.ReceiptId
                    WHERE i.Status=N'APPLIED'
                        AND r.EventKind=N'CashierAcceptanceRequested'
                        AND NOT EXISTS (
                            SELECT 1 FROM dbo.App02AcceptanceApplication a
                            JOIN dbo.App02OutboxMessage o ON o.TenantId=a.TenantId
                                AND o.CReceiptId=a.CReceiptId
                                AND o.MessageId=a.MessageId
                            JOIN dbo.App02OutboxDelivery delivery
                                ON delivery.TenantId=o.TenantId
                                AND delivery.MessageId=o.MessageId
                            JOIN dbo.CashHandoverAcceptance ca
                                ON ca.TenantId=a.TenantId
                                AND ca.Id=a.AcceptanceId
                            JOIN dbo.LedgerEffect custody
                                ON custody.TenantId=ca.TenantId
                                AND custody.BusinessSourceLineId=ca.SourceLineId
                                AND custody.EffectKey=N'CUSTODY-OUT'
                                AND custody.LedgerKind=N'CUSTODY'
                                AND custody.AmountDelta=-ca.AcceptedAmount
                                AND custody.QuantityDelta=0
                            JOIN dbo.LedgerEffect fund
                                ON fund.TenantId=ca.TenantId
                                AND fund.BusinessSourceLineId=ca.SourceLineId
                                AND fund.EffectKey=N'FUND-IN'
                                AND fund.LedgerKind=N'FUND'
                                AND fund.AmountDelta=ca.AcceptedAmount
                                AND fund.QuantityDelta=0
                            WHERE a.TenantId=i.TenantId AND a.CReceiptId=i.ReceiptId
                                AND (SELECT COUNT(*) FROM dbo.LedgerEffect e
                                     WHERE e.TenantId=ca.TenantId
                                       AND e.BusinessSourceLineId=ca.SourceLineId)=2))
                    THROW 51146, 'APP02 C APPLIED requires balanced APP01D ledger, acceptance and outbox', 1;
            END;
            """);
        Execute.Sql("""
            CREATE TRIGGER dbo.TR_APP02_DeliveryGuard
            ON dbo.App02OutboxDelivery AFTER INSERT, UPDATE, DELETE AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (
                    SELECT 1 FROM deleted d WHERE NOT EXISTS (
                        SELECT 1 FROM inserted i
                        WHERE i.TenantId=d.TenantId AND i.MessageId=d.MessageId))
                    THROW 51150, 'APP02 delivery cannot be deleted/rekeyed', 1;
                IF EXISTS (
                    SELECT 1 FROM inserted i LEFT JOIN deleted d
                        ON d.TenantId=i.TenantId AND d.MessageId=i.MessageId
                    WHERE d.MessageId IS NULL AND (i.Status<>N'PENDING'
                        OR i.AttemptCount<>0))
                    THROW 51151, 'APP02 delivery starts pending, zero attempts', 1;
                IF EXISTS (
                    SELECT 1 FROM inserted i JOIN deleted d
                        ON d.TenantId=i.TenantId AND d.MessageId=i.MessageId
                    WHERE i.AttemptCount<d.AttemptCount
                        OR (i.Status<>d.Status AND NOT (
                            (d.Status=N'PENDING'
                                AND i.Status IN (N'LEASED',N'CASE'))
                            OR (d.Status=N'LEASED'
                                AND i.Status IN (N'PENDING',N'ACKED',N'CASE'))
                            OR (d.Status=N'CASE' AND i.Status=N'PENDING'))))
                    THROW 51152, 'APP02 illegal delivery transition', 1;
                IF EXISTS (
                    SELECT 1 FROM inserted i
                    WHERE i.Status=N'ACKED' AND NOT EXISTS (
                        SELECT 1 FROM dbo.App02SyntheticConsumerInbox c
                        WHERE c.TenantId=i.TenantId AND c.MessageId=i.MessageId))
                    THROW 51153, 'APP02 ACK requires committed synthetic inbox', 1;
            END;
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DROP TRIGGER IF EXISTS dbo.TR_APP02_DeliveryGuard;
            DROP TRIGGER IF EXISTS dbo.TR_APP02_ProgressGuard;
            DROP TRIGGER IF EXISTS dbo.TR_APP02_CorrectionLedgerImmutable;
            DROP TRIGGER IF EXISTS dbo.TR_APP02_CorrectionSourceLineImmutable;
            DROP TRIGGER IF EXISTS dbo.TR_APP02_CorrectionSourceImmutable;
            DROP TRIGGER IF EXISTS dbo.TR_APP02_CorrectionGuard;
            DROP TRIGGER IF EXISTS dbo.TR_APP02_CaseChecker;
            DROP TRIGGER IF EXISTS dbo.TR_APP02_CaseKind;
            """);
        foreach (string table in new[]
        {
            "App02CorrectionLink", "App02CaseDisposition", "App02ExceptionCase",
            "App02SyntheticConsumerInbox", "App02OutboxMessage",
            "App02AcceptanceApplication", "App02ResolvedDependency",
            "App02EventReceipt"
        })
            Execute.Sql($"DROP TRIGGER IF EXISTS dbo.TR_APP02_{table}_Immutable;");
        Execute.Sql("""
            DROP TABLE dbo.App02CorrectionLink;
            DROP TABLE dbo.App02CaseDisposition;
            DROP TABLE dbo.App02ExceptionCase;
            DROP TABLE dbo.App02SyntheticConsumerInbox;
            DROP TABLE dbo.App02OutboxDelivery;
            DROP TABLE dbo.App02OutboxMessage;
            DROP TABLE dbo.App02AcceptanceApplication;
            DROP TABLE dbo.App02ResolvedDependency;
            DROP TABLE dbo.App02EventProgress;
            DROP TABLE dbo.App02EventReceipt;
            ALTER TABLE dbo.CashHandoverAcceptance
                DROP CONSTRAINT UQ_APP02_AcceptanceBound;
            """);
    }
}
