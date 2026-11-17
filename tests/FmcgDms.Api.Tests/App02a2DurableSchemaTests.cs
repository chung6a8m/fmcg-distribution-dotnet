using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FmcgDms.Api.Tests;

/// <summary>
/// APP-02A.2 ONLY: direct physical SQL constraints, two-tenant migration replay,
/// canonical cross-language fixtures. No event worker, provider or A11 oracle.
/// </summary>
[CollectionDefinition("App02a2Scratch", DisableParallelization = true)]
public sealed class App02a2ScratchCollection : ICollectionFixture<SqlScratchFixture> { }

[Collection("App02a2Scratch")]
public sealed class App02a2DurableSchemaTests(SqlScratchFixture fixture)
{
    private const string A = "tenant-a";
    private const string B = "tenant-b";
    private static readonly DateTimeOffset At =
        new(2026, 11, 17, 3, 0, 0, TimeSpan.Zero);

    private static string Id() => Guid.NewGuid().ToString("N");
    private static SqlConnection Open(string connection)
    {
        var db = new SqlConnection(connection);
        db.Open();
        return db;
    }

    private static void Reject(SqlConnection db, string sql, object args, int[] codes,
        SqlTransaction? tx = null)
    {
        SqlException e = Assert.Throws<SqlException>(() => db.Execute(sql, args, tx));
        Assert.Contains(e.Number, codes);
    }

    [Fact]
    public void Canonical_v1_matches_two_independently_hashed_byte_fixtures()
    {
        Guid trip = Guid.ParseExact(new string('a', 32), "N");
        Guid offer = Guid.ParseExact(new string('b', 32), "N");
        var p = new App02EventEnvelope(A, "sales-terminal-01", "evt-p-0001",
            App02EventCanonicalV1.OfferKind, "sales-01", trip, offer, 250.5000m,
            null, null, null, At);
        var c = new App02EventEnvelope(A, "cashier-terminal-01", "evt-c-0001",
            App02EventCanonicalV1.AcceptanceKind, "cashier-01", trip, null, null,
            "sales-terminal-01", "evt-p-0001", "COUNT-0001",
            At.AddSeconds(1));
        const string expectedP = """
            {"v":1,"tenantId":"tenant-a","producerId":"sales-terminal-01","eventId":"evt-p-0001","kind":"CashHandoverOffered","actorId":"sales-01","tripId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","offerId":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","offeredAmount":"250.5","prerequisiteProducerId":null,"prerequisiteEventId":null,"countEvidenceRef":null,"occurredAtUtc":"2026-11-17T03:00:00.0000000Z"}
            """;
        const string expectedC = """
            {"v":1,"tenantId":"tenant-a","producerId":"cashier-terminal-01","eventId":"evt-c-0001","kind":"CashierAcceptanceRequested","actorId":"cashier-01","tripId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","offerId":null,"offeredAmount":null,"prerequisiteProducerId":"sales-terminal-01","prerequisiteEventId":"evt-p-0001","countEvidenceRef":"COUNT-0001","occurredAtUtc":"2026-11-17T03:00:01.0000000Z"}
            """;
        Assert.Equal(expectedP, Encoding.UTF8.GetString(App02EventCanonicalV1.Encode(p)));
        Assert.Equal(expectedC, Encoding.UTF8.GetString(App02EventCanonicalV1.Encode(c)));
        Assert.Equal(376, App02EventCanonicalV1.Encode(p).Length);
        Assert.Equal(385, App02EventCanonicalV1.Encode(c).Length);
        Assert.Equal("4cbe2696d5997c838e93f419c534bf0c30ee06d975ffc311ded074c6c6c70ad2",
            Convert.ToHexString(App02EventCanonicalV1.Sha256(p)).ToLowerInvariant());
        Assert.Equal("b4d9e353c632f72eb464ab6c2f606b8c1f93fd9821246cb3de74883661d9b9ce",
            Convert.ToHexString(App02EventCanonicalV1.Sha256(c)).ToLowerInvariant());
        Assert.Equal(App02EventCanonicalV1.Sha256(p),
            App02EventCanonicalV1.Sha256(p with
            {
                OfferedAmount = 250.5m,
                OccurredAtUtc = new DateTimeOffset(2026, 11, 17, 10, 0, 0,
                    TimeSpan.FromHours(7))
            }));
        Assert.NotEqual(App02EventCanonicalV1.Sha256(c),
            App02EventCanonicalV1.Sha256(c with { PrerequisiteEventId = "evt-p-0002" }));
        Assert.Throws<ArgumentException>(() =>
            App02EventCanonicalV1.Encode(c with { OfferId = offer }));
        Assert.Throws<ArgumentException>(() =>
            App02EventCanonicalV1.Encode(p with { OfferedAmount = 0m }));
        Assert.Throws<ArgumentException>(() =>
            App02EventCanonicalV1.Encode(c with { EventKind = "UNKNOWN" }));
        Assert.Throws<ArgumentException>(() =>
            App02EventCanonicalV1.Encode(c with { TenantId = "arbitrary-db" }));
    }

    [Fact]
    public void Both_nonce_owned_tenants_have_repeatable_schema_without_app02_posting()
    {
        Assert.NotEqual(fixture.TenantADb, fixture.TenantBDb);
        foreach (var (tenant, connection) in new[]
            { (A, fixture.TenantAConnectionString), (B, fixture.TenantBConnectionString) })
        {
            using var db = Open(connection);
            foreach (string table in new[]
            {
                "App02EventReceipt", "App02EventProgress", "App02ResolvedDependency",
                "App02AcceptanceApplication", "App02OutboxMessage",
                "App02OutboxDelivery", "App02SyntheticConsumerInbox",
                "App02ExceptionCase", "App02CaseDisposition", "App02CorrectionLink"
            })
                Assert.Equal(1, db.ExecuteScalar<int>("""
                    SELECT COUNT(*) FROM sys.tables
                    WHERE schema_id=SCHEMA_ID(N'dbo') AND name=@table
                    """, new { table }));

            Assert.Equal(1, db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.VersionInfo WHERE Version=202611170001"));
            Assert.Equal(1, db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM sys.foreign_keys
                WHERE name=N'FK_App02Resolution_P'
                """));
            Assert.Equal(1, db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM sys.triggers
                WHERE name=N'TR_APP02_ProgressGuard'
                """));

            int receipts = db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.App02EventReceipt");
            int ledger = db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.LedgerEffect");
            ScratchMigrator.Migrate(connection, tenant, fixture.Nonce);
            ScratchMigrator.Migrate(connection, tenant, fixture.Nonce);
            Assert.Equal(1, db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.VersionInfo WHERE Version=202611170001"));
            Assert.Equal(receipts, db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.App02EventReceipt"));
            Assert.Equal(ledger, db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.LedgerEffect"));
            Assert.Equal(0, db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.App02AcceptanceApplication"));
            Assert.Equal(0, db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.App02OutboxMessage"));
            Assert.Equal(tenant, db.ExecuteScalar<string>(
                "SELECT TenantId FROM dbo.AppTenantBinding WHERE Slot=1"));
        }
    }

    [Fact]
    public void C_before_P_survives_without_phantom_offer_and_resolves_only_named_P()
    {
        using var db = Open(fixture.TenantAConnectionString);
        Guid trip = NewTrip(db, A);
        var p = NewP(A, trip, Guid.NewGuid(), 25m);
        var c = NewC(A, trip, p);
        Guid cReceipt = AddReceipt(db, c);
        db.Execute("UPDATE dbo.App02EventProgress SET Status=N'PENDING_DEPENDENCY' WHERE ReceiptId=@cReceipt",
            new { cReceipt });
        Assert.Equal("PENDING_DEPENDENCY", db.ExecuteScalar<string>(
            "SELECT Status FROM dbo.App02EventProgress WHERE ReceiptId=@cReceipt",
            new { cReceipt }));
        Assert.Equal(0, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.App02ResolvedDependency WHERE CReceiptId=@cReceipt",
            new { cReceipt }));
        Reject(db, InsertResolution, Resolution(A, c, cReceipt, p, Guid.NewGuid()),
            [547]); // P reference not yet committed (even with same trip).

        Guid realOffer = NewOffer(db, A, trip, 25m);
        p = p with { OfferId = realOffer };
        Guid pReceipt = AddReceipt(db, p);
        Reject(db, InsertResolution, Resolution(A, c, cReceipt,
            p with { EventId = "wrong-prerequisite-id" }, pReceipt),
            [547]); // Exact named P identity, not just same trip/offer.
        Reject(db, InsertResolution, Resolution(A, c with
            { CountEvidenceRef = "wrong-count" }, cReceipt, p, pReceipt),
            [547]); // C original witness reference cannot be changed.
        db.Execute(InsertResolution, Resolution(A, c, cReceipt, p, pReceipt));
        Reject(db, InsertResolution, Resolution(A, c, cReceipt, p, pReceipt),
            [2627, 2601]);
        Assert.Equal(0, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.App02AcceptanceApplication WHERE CReceiptId=@cReceipt",
            new { cReceipt }));
        Assert.Equal(0, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.LedgerEffect e JOIN dbo.BusinessSourceLine sl "
            + "ON sl.TenantId=e.TenantId AND sl.Id=e.BusinessSourceLineId "
            + "JOIN dbo.BusinessSource s ON s.TenantId=sl.TenantId "
            + "AND s.Id=sl.BusinessSourceId WHERE s.TripId=@trip",
            new { trip }));
        Assert.Equal(0, db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.App02OutboxMessage"));
        Reject(db, "UPDATE dbo.App02EventProgress SET Status=N'APPLIED' WHERE ReceiptId=@cReceipt",
            new { cReceipt }, [51146]); // Cannot skip APP-01D legs + transactional outbox.
        Assert.Equal("PENDING_DEPENDENCY", db.ExecuteScalar<string>(
            "SELECT Status FROM dbo.App02EventProgress WHERE ReceiptId=@cReceipt",
            new { cReceipt }));
    }

    [Fact]
    public void Event_identity_hash_immutability_and_peer_tenant_boundaries_are_enforced()
    {
        using var dbA = Open(fixture.TenantAConnectionString);
        using var dbB = Open(fixture.TenantBConnectionString);
        Guid trip = NewTrip(dbA, A);
        Guid offer = NewOffer(dbA, A, trip, 25m);
        var p = NewP(A, trip, offer, 25m);
        var c = NewC(A, trip, p);
        Guid pReceipt = AddReceipt(dbA, p);
        Guid cReceipt = AddReceipt(dbA, c);
        var ex = Assert.Throws<SqlException>(() =>
            AddReceipt(dbA, c with { ActorId = "cashier-02" }));
        Assert.Contains(ex.Number, new[] { 2627, 2601 }); // Kind/hash not in identity key.

        ex = Assert.Throws<SqlException>(() =>
            AddReceipt(dbA, c with { EventId = Id() }, wrongHash: true));
        Assert.Equal(547, ex.Number); // SQL validates stored SHA256 over exact bytes.

        Reject(dbA, """
            UPDATE dbo.App02EventReceipt SET ActorId=N'forged'
            WHERE ReceiptId=@cReceipt
            """, new { cReceipt }, [51120]);
        Reject(dbA, """
            UPDATE dbo.App02EventProgress SET Status=N'UNKNOWN'
            WHERE ReceiptId=@cReceipt
            """, new { cReceipt }, [547]); // UNKNOWN is observer-only.

        int peerBefore = dbB.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.App02EventReceipt");
        ex = Assert.Throws<SqlException>(() => AddReceipt(dbA,
            c with { TenantId = B, EventId = Id() }));
        Assert.Equal(547, ex.Number); // DB A cannot own a row for tenant B.
        Assert.Equal(peerBefore, dbB.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.App02EventReceipt"));
        Assert.Equal(0, dbB.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.App02ResolvedDependency"));
        Assert.Equal(A, dbA.ExecuteScalar<string>(
            "SELECT TenantId FROM dbo.App02EventReceipt WHERE ReceiptId=@pReceipt",
            new { pReceipt }));
        Assert.NotEqual(pReceipt, cReceipt);
    }

    [Fact]
    public void CASE_requires_owned_history_independent_checker_and_approved_same_ID_resume()
    {
        using var db = Open(fixture.TenantAConnectionString);
        Guid trip = NewTrip(db, A);
        var p = NewP(A, trip, Guid.NewGuid(), 25m);
        var c = NewC(A, trip, p);
        Guid receipt = AddReceipt(db, c);
        db.Execute("""
            UPDATE dbo.App02EventProgress SET Status=N'PENDING_DEPENDENCY'
            WHERE ReceiptId=@receipt
            """, new { receipt });
        db.Execute("""
            UPDATE dbo.App02EventProgress SET TechnicalRetryCount=6
            WHERE ReceiptId=@receipt
            """, new { receipt });
        Reject(db, """
            UPDATE dbo.App02EventProgress SET TechnicalRetryCount=7
            WHERE ReceiptId=@receipt
            """, new { receipt }, [547]);
        Reject(db, """
            UPDATE dbo.App02EventProgress SET TechnicalRetryCount=5
            WHERE ReceiptId=@receipt
            """, new { receipt }, [51142]);
        Reject(db, """
            UPDATE dbo.App02EventProgress SET Status=N'CASE', ReasonCode=N'MISSING_P_24H'
            WHERE ReceiptId=@receipt
            """, new { receipt }, [51144]); // No silent/ownerless CASE.

        Guid caseId = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.App02ExceptionCase(CaseId,TenantId,ReceiptId,AsOfUtc,
                ReasonCode,NextAction)
            VALUES(@caseId,@tenant,@receipt,SYSUTCDATETIME(),
                N'MISSING_P_24H',N'Cashier supervisor review')
            """, new { caseId, tenant = A, receipt });
        db.Execute("""
            UPDATE dbo.App02EventProgress SET Status=N'CASE',
                ReasonCode=N'MISSING_P_24H'
            WHERE ReceiptId=@receipt
            """, new { receipt });
        Reject(db, DispositionInsert, DispositionArgs(caseId, "maker-01", "maker-01",
            "APPROVED_RESUME"), [547]);
        Reject(db, DispositionInsert, DispositionArgs(caseId, "maker-01", "cashier-01",
            "APPROVED_RESUME"), [51131]);
        Guid hold = Guid.NewGuid();
        db.Execute(DispositionInsert, DispositionArgs(caseId, "maker-01", "checker-01",
            "HOLD", hold));
        Reject(db, """
            UPDATE dbo.App02EventProgress SET Status=N'RECEIVED'
            WHERE ReceiptId=@receipt
            """, new { receipt }, [51145]);
        Guid approval = Guid.NewGuid();
        db.Execute(DispositionInsert, DispositionArgs(caseId, "maker-01", "checker-01",
            "APPROVED_RESUME", approval));
        db.Execute("""
            UPDATE dbo.App02EventProgress SET Status=N'RECEIVED',ReasonCode=NULL,
                NextAttemptAtUtc=NULL
            WHERE ReceiptId=@receipt
            """, new { receipt });
        Reject(db, """
            UPDATE dbo.App02CaseDisposition SET CheckerActorId=N'forged'
            WHERE DispositionId=@approval
            """, new { approval }, [51120]);
        Reject(db, """
            UPDATE dbo.App02ExceptionCase SET NextAction=N'drop'
            WHERE CaseId=@caseId
            """, new { caseId }, [51120]);
        Assert.Equal(6, db.ExecuteScalar<byte>(
            "SELECT TechnicalRetryCount FROM dbo.App02EventProgress WHERE ReceiptId=@receipt",
            new { receipt }));
        Assert.Equal("RECEIVED", db.ExecuteScalar<string>(
            "SELECT Status FROM dbo.App02EventProgress WHERE ReceiptId=@receipt",
            new { receipt }));
    }

    [Fact]
    public void Transactional_source_outbox_and_consumer_ack_are_distinct_sql_witnesses()
    {
        using var db = Open(fixture.TenantAConnectionString);
        Guid rolledBackTrip;
        using (var tx = db.BeginTransaction())
        {
            var scene = StageAcceptance(db, tx);
            rolledBackTrip = scene.Trip;
            Assert.Equal(2, db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM dbo.LedgerEffect e
                JOIN dbo.BusinessSourceLine sl ON sl.TenantId=e.TenantId
                    AND sl.Id=e.BusinessSourceLineId
                WHERE sl.BusinessSourceId=@acceptance
                """, new { acceptance = scene.Acceptance }, tx));
            Assert.Equal("APPLIED", db.ExecuteScalar<string>(
                "SELECT Status FROM dbo.App02EventProgress WHERE ReceiptId=@receipt",
                new { receipt = scene.CReceipt }, tx));
            Assert.Equal("PENDING", db.ExecuteScalar<string>(
                "SELECT Status FROM dbo.App02OutboxDelivery WHERE MessageId=@message",
                new { message = scene.Message }, tx)); // Apply ≠ outbound ACK.
            db.Execute("""
                UPDATE dbo.App02OutboxDelivery SET Status=N'LEASED',AttemptCount=1,
                    LeaseOwner=N'synthetic-worker',LeaseExpiresAtUtc=DATEADD(minute,1,SYSUTCDATETIME())
                WHERE MessageId=@message
                """, new { message = scene.Message }, tx);
            db.Execute("""
                INSERT dbo.App02SyntheticConsumerInbox
                    (TenantId,MessageId,ConsumerReceiptId,PayloadVersion,PayloadHash)
                VALUES(@tenant,@message,@consumerReceipt,1,@hash)
                """, new { tenant = A, message = scene.Message,
                    consumerReceipt = Guid.NewGuid(), hash = scene.Hash }, tx);
            db.Execute("""
                UPDATE dbo.App02OutboxDelivery SET Status=N'ACKED',
                    LeaseOwner=NULL,LeaseExpiresAtUtc=NULL,AckAtUtc=SYSUTCDATETIME()
                WHERE MessageId=@message
                """, new { message = scene.Message }, tx);
            Assert.Equal("ACKED", db.ExecuteScalar<string>(
                "SELECT Status FROM dbo.App02OutboxDelivery WHERE MessageId=@message",
                new { message = scene.Message }, tx));
            Assert.Equal(1, db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.App02SyntheticConsumerInbox WHERE MessageId=@message",
                new { message = scene.Message }, tx));
            tx.Rollback(); // Direct SQL probe must not leave a fake business posting.
        }
        Assert.Equal(0, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.BusinessSource WHERE TripId=@trip",
            new { trip = rolledBackTrip }));
        Assert.Equal(0, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.App02AcceptanceApplication"));
        Assert.Equal(0, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.App02SyntheticConsumerInbox"));
    }

    [Fact]
    public void Outbound_ack_without_consumer_commit_is_forbidden()
    {
        using var db = Open(fixture.TenantAConnectionString);
        using var tx = db.BeginTransaction();
        var scene = StageAcceptance(db, tx);
        db.Execute("""
            UPDATE dbo.App02OutboxDelivery SET Status=N'LEASED',
                LeaseOwner=N'worker',LeaseExpiresAtUtc=DATEADD(minute,1,SYSUTCDATETIME())
            WHERE MessageId=@message
            """, new { message = scene.Message }, tx);
        Reject(db, """
            UPDATE dbo.App02OutboxDelivery SET Status=N'ACKED',
                LeaseOwner=NULL,LeaseExpiresAtUtc=NULL,AckAtUtc=SYSUTCDATETIME()
            WHERE MessageId=@message
            """, new { message = scene.Message }, [51153], tx);
        tx.Rollback(); // A failing trigger may already have aborted the transaction.
    }

    private static Guid NewTrip(SqlConnection db, string tenant,
        SqlTransaction? tx = null)
    {
        Guid trip = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.Trip(Id,TenantId,Code,BusinessDate)
            VALUES(@trip,@tenant,@code,'2026-11-17')
            """, new { trip, tenant, code = Id() }, tx);
        return trip;
    }

    private static Guid NewCommand(SqlConnection db, string tenant,
        SqlTransaction? tx = null)
    {
        Guid command = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.BusinessCommand(Id,TenantId,ProducerId,CommandId,
                PayloadHashVersion,PayloadHash,ActorId,Status)
            VALUES(@command,@tenant,N'app02-schema-tests',@code,1,@hash,N'schema-fixture',N'RECEIVED')
            """, new { command, tenant, code = Id(),
                hash = SHA256.HashData(Guid.NewGuid().ToByteArray()) }, tx);
        return command;
    }

    private static Guid NewSource(SqlConnection db, string tenant, Guid trip,
        string kind, string actor, SqlTransaction? tx = null)
    {
        Guid source = Guid.NewGuid(), command = NewCommand(db, tenant, tx);
        db.Execute("""
            INSERT dbo.BusinessSource(Id,TenantId,BusinessCommandId,SourceKind,
                SourceNumber,ActorId,TripId,OccurredAtUtc)
            VALUES(@source,@tenant,@command,@kind,@number,@actor,@trip,SYSUTCDATETIME())
            """, new { source, tenant, command, kind, number = Id(), actor, trip }, tx);
        return source;
    }

    private static Guid NewSourceLine(SqlConnection db, string tenant,
        Guid source, decimal amount, SqlTransaction? tx = null)
    {
        Guid line = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.BusinessSourceLine
                (Id,TenantId,BusinessSourceId,LineNumber,Amount)
            VALUES(@line,@tenant,@source,1,@amount)
            """, new { line, tenant, source, amount }, tx);
        return line;
    }

    private static Guid NewOffer(SqlConnection db, string tenant, Guid trip,
        decimal amount, SqlTransaction? tx = null)
    {
        Guid offer = NewSource(db, tenant, trip, "CASH_HANDOVER_OFFER", "sales-01", tx);
        Guid line = NewSourceLine(db, tenant, offer, amount, tx);
        db.Execute("""
            INSERT dbo.CashHandoverOffer
                (Id,TenantId,TripId,SourceLineId,OfferedAmount,OfferedBy)
            VALUES(@offer,@tenant,@trip,@line,@amount,N'sales-01')
            """, new { offer, tenant, trip, line, amount }, tx);
        return offer;
    }

    private static App02EventEnvelope NewP(string tenant, Guid trip, Guid offer,
        decimal amount) => new(tenant, "sales-" + Id(), "p-" + Id(),
        App02EventCanonicalV1.OfferKind, "sales-01", trip, offer, amount,
        null, null, null, At);

    private static App02EventEnvelope NewC(string tenant, Guid trip,
        App02EventEnvelope p) => new(tenant, "cashier-" + Id(), "c-" + Id(),
        App02EventCanonicalV1.AcceptanceKind, "cashier-01", trip, null, null,
        p.ProducerId, p.EventId, "count-" + Id(), At.AddSeconds(1));

    private const string InsertReceipt = """
        INSERT dbo.App02EventReceipt
            (ReceiptId,TenantId,ProducerId,EventId,EventKind,ActorId,TripId,
             OfferId,OfferedAmount,PrerequisiteProducerId,PrerequisiteEventId,
             CountEvidenceRef,OccurredAtUtc,PayloadVersion,CanonicalBytes,PayloadHash)
        VALUES(@receipt,@tenant,@producer,@eventId,@kind,@actor,@trip,
            @offer,@amount,@pProducer,@pEvent,@count,@occurred,1,@bytes,@hash)
        """;

    private static Guid AddReceipt(SqlConnection db, App02EventEnvelope e,
        SqlTransaction? tx = null, bool wrongHash = false)
    {
        Guid receipt = Guid.NewGuid();
        byte[] bytes = App02EventCanonicalV1.Encode(e);
        byte[] hash = SHA256.HashData(bytes);
        if (wrongHash) hash[0] ^= 0xff;
        db.Execute(InsertReceipt, new
        {
            receipt, tenant = e.TenantId, producer = e.ProducerId,
            eventId = e.EventId, kind = e.EventKind, actor = e.ActorId,
            trip = e.TripId, offer = e.OfferId, amount = e.OfferedAmount,
            pProducer = e.PrerequisiteProducerId, pEvent = e.PrerequisiteEventId,
            count = e.CountEvidenceRef, occurred = e.OccurredAtUtc.UtcDateTime,
            bytes, hash
        }, tx);
        db.Execute("""
            INSERT dbo.App02EventProgress(TenantId,ReceiptId)
            VALUES(@tenant,@receipt)
            """, new { tenant = e.TenantId, receipt }, tx);
        return receipt;
    }

    private const string InsertResolution = """
        INSERT dbo.App02ResolvedDependency
            (TenantId,CReceiptId,TripId,CountEvidenceRef,
             PProducerId,PEventId,CActorId,PReceiptId,OfferId)
        VALUES(@tenant,@cReceipt,@trip,@count,@pProducer,@pEvent,
            @cActor,@pReceipt,@offer)
        """;

    private static object Resolution(string tenant, App02EventEnvelope c,
        Guid cReceipt, App02EventEnvelope p, Guid pReceipt) => new
    {
        tenant, cReceipt, trip = c.TripId, count = c.CountEvidenceRef,
        pProducer = p.ProducerId, pEvent = p.EventId, cActor = c.ActorId,
        pReceipt, offer = p.OfferId
    };

    private const string DispositionInsert = """
        INSERT dbo.App02CaseDisposition
            (DispositionId,TenantId,CaseId,MakerActorId,CheckerActorId,
             Decision,ReasonCode,EvidenceRef,ProposedResolution,ApprovedAtUtc)
        VALUES(@id,@tenant,@caseId,@maker,@checker,@decision,N'CASE_REVIEW',
            N'SYNTHETIC-EVIDENCE',N'Revalidate exact P and count on resume',SYSUTCDATETIME())
        """;
    private static object DispositionArgs(Guid caseId, string maker,
        string checker, string decision, Guid? id = null) =>
        new { id = id ?? Guid.NewGuid(), tenant = A, caseId, maker, checker, decision };

    private sealed record Stage(Guid Trip, Guid CReceipt, Guid Acceptance,
        Guid Message, byte[] Hash);

    // A SQL-only dry-run transaction. A.3 will own actual transactional posting.
    private static Stage StageAcceptance(SqlConnection db, SqlTransaction tx)
    {
        Guid trip = NewTrip(db, A, tx);
        Guid offer = NewOffer(db, A, trip, 25m, tx);
        var p = NewP(A, trip, offer, 25m);
        var c = NewC(A, trip, p);
        Guid pReceipt = AddReceipt(db, p, tx);
        Guid cReceipt = AddReceipt(db, c, tx);
        db.Execute(InsertResolution, Resolution(A, c, cReceipt, p, pReceipt), tx);
        db.Execute("""
            INSERT dbo.CashierCountEvidence
                (TenantId,EvidenceRef,TripId,CountedAmount,CountedBy,
                 VerificationStatus,CountedAtUtc)
            VALUES(@tenant,@evidence,@trip,25,N'cashier-01',
                   N'VERIFIED',SYSUTCDATETIME())
            """, new { tenant = A, evidence = c.CountEvidenceRef, trip }, tx);
        Guid acceptance = NewSource(db, A, trip, "CASH_HANDOVER_ACCEPT", "cashier-01", tx);
        Guid line = NewSourceLine(db, A, acceptance, 25m, tx);
        db.Execute("""
            INSERT dbo.CashHandoverAcceptance
                (Id,TenantId,TripId,OfferId,SourceLineId,EvidenceRef,
                 AcceptedBy,AcceptedAmount)
            VALUES(@acceptance,@tenant,@trip,@offer,@line,@evidence,
                   N'cashier-01',25)
            """, new { acceptance, tenant = A, trip, offer, line,
                evidence = c.CountEvidenceRef }, tx);
        foreach (var (key, kind, amount) in new[]
        {
            ("CUSTODY-OUT", "CUSTODY", -25m), ("FUND-IN", "FUND", 25m)
        })
        {
            db.Execute("""
                INSERT dbo.LedgerEffect
                    (Id,TenantId,BusinessSourceLineId,EffectKey,
                     LedgerKind,QuantityDelta,AmountDelta)
                VALUES(@id,@tenant,@line,@key,@kind,0,@amount)
                """, new { id = Guid.NewGuid(), tenant = A, line, key, kind, amount }, tx);
        }

        Guid message = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.App02AcceptanceApplication
                (TenantId,CReceiptId,PReceiptId,TripId,OfferId,CountEvidenceRef,
                 CActorId,AcceptanceId,MessageId)
            VALUES(@tenant,@cReceipt,@pReceipt,@trip,@offer,@evidence,
                   N'cashier-01',@acceptance,@message)
            """, new { tenant = A, cReceipt, pReceipt, trip, offer,
                evidence = c.CountEvidenceRef, acceptance, message }, tx);
        byte[] bytes = Encoding.UTF8.GetBytes(
            "{\"v\":1,\"kind\":\"CashHandoverAcceptanceApplied\"}");
        byte[] hash = SHA256.HashData(bytes);
        db.Execute("""
            INSERT dbo.App02OutboxMessage
                (TenantId,MessageId,CReceiptId,AcceptanceId,
                 PayloadVersion,CanonicalBytes,PayloadHash)
            VALUES(@tenant,@message,@cReceipt,@acceptance,1,@bytes,@hash)
            """, new { tenant = A, message, cReceipt, acceptance, bytes, hash }, tx);
        db.Execute("""
            INSERT dbo.App02OutboxDelivery(TenantId,MessageId)
            VALUES(@tenant,@message)
            """, new { tenant = A, message }, tx);
        db.Execute("""
            UPDATE dbo.App02EventProgress SET Status=N'APPLIED',
                NextAttemptAtUtc=NULL,ReasonCode=NULL
            WHERE ReceiptId=@cReceipt
            """, new { cReceipt }, tx);
        return new Stage(trip, cReceipt, acceptance, message, hash);
    }
}
