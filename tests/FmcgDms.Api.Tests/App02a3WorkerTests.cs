using System.Net;
using System.Net.Http.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FmcgDms.Api.Tests;

/// <summary>
/// APP-02A.3 runtime regressions only. APP-02A.4 owns the independent
/// A11-01..12 expected/actual SQL oracle; these tests are NOT its substitute.
/// </summary>
public sealed partial class App01e2CrossCommandRaceTests
{
    private const string A3 = "/api/synthetic/app02/events/";

    private static void GrantRole(SqlConnection db, string tenant, string actor,
        string role, bool active = true)
    {
        db.Execute("""
            IF EXISTS(SELECT 1 FROM dbo.App02ActorRoleGrant
                WHERE TenantId=@Tenant AND ActorId=@Actor AND RoleCode=@Role)
                UPDATE dbo.App02ActorRoleGrant SET IsActive=@Active,
                    ChangedBy=N'app02-fixture',ChangedAtUtc=SYSUTCDATETIME()
                WHERE TenantId=@Tenant AND ActorId=@Actor AND RoleCode=@Role
            ELSE
                INSERT dbo.App02ActorRoleGrant(
                    TenantId,ActorId,RoleCode,IsActive,ChangedBy)
                VALUES(@Tenant,@Actor,@Role,@Active,N'app02-fixture')
            """, new { Tenant = tenant, Actor = actor, Role = role, Active = active });
    }

    private async Task<(Scene Scene, Guid Offer, string Count)> SetupA3(
        string tenant, HttpClient setup, HttpClient sales)
    {
        var scene = Seed(tenant);
        await Prepare(setup, tenant, scene, load: true);
        await Apply(sales, C + "post-b-sale-delivery-receipt",
            B20(scene, CashWitness(tenant, scene)), "B_SALE_DELIVERY_POSTED");
        var offer = await Apply(sales, D + "offer-cash-handover",
            new OfferCashHandoverCommand(Producer, Id(), Id(), scene.Trip, 200m),
            "CASH_HANDOVER_OFFERED");
        string count = Id();
        using var db = Open(tenant);
        db.Execute("""
            INSERT dbo.CashierCountEvidence(
                TenantId,EvidenceRef,TripId,CountedAmount,
                CountedBy,VerificationStatus,CountedAtUtc)
            VALUES(@Tenant,@Count,@Trip,200,@Actor,N'VERIFIED',SYSUTCDATETIME())
            """, new { Tenant = tenant, Count = count, scene.Trip,
                Actor = CashierActor });
        GrantRole(db, tenant, SalesActor, "sales");
        GrantRole(db, tenant, CashierActor, "cashier");
        GrantRole(db, tenant, "a3-maker", "cashier_supervisor");
        GrantRole(db, tenant, "a3-checker", "case_checker");
        return (scene, Assert.IsType<Guid>(offer.SourceId), count);
    }

    private static App02EventInput NewPEvent(Guid trip, Guid offer) =>
        new(SalesActor, "event-p-" + Id(),
            App02EventCanonicalV1.OfferKind, trip, offer, 200m,
            null, null, null, DateTimeOffset.UtcNow);

    private static App02EventInput NewCEvent(Guid trip, App02EventInput p, string count) =>
        new(CashierActor, "event-c-" + Id(),
            App02EventCanonicalV1.AcceptanceKind, trip, null, null,
            p.ProducerId, p.EventId, count, DateTimeOffset.UtcNow);

    private static async Task<App02IntakeResult> IntakeA3(
        HttpClient client, App02EventInput input)
    {
        using HttpResponseMessage response =
            await client.PostAsJsonAsync(A3 + "intake", input, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        App02IntakeResult? result =
            await response.Content.ReadFromJsonAsync<App02IntakeResult>(TestContext.Current.CancellationToken);
        Assert.Equal("RECEIVED_COMMITTED", result?.Ack);
        return Assert.IsType<App02IntakeResult>(result);
    }

    [Theory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public async Task App02a3_named_P_then_C_is_atomic_idempotent_and_consumer_ack_recovers(
        string tenant)
    {
        using var factory = fixture.CreateFactory();
        using var setup = SqlScratchFixture.WithTenant(factory.CreateClient(), tenant);
        using var sales = Client(factory, tenant, SalesActor, "sales");
        using var cashier = Client(factory, tenant, CashierActor, "cashier");
        var s = await SetupA3(tenant, setup, sales);
        App02EventInput p = NewPEvent(s.Scene.Trip, s.Offer);
        App02EventInput c = NewCEvent(s.Scene.Trip, p, s.Count);
        var pAck = await IntakeA3(sales, p);
        var cAck = await IntakeA3(cashier, c);
        Assert.Equal("APPLIED", pAck.Status); // P is nonposting offer event.
        Assert.Equal("PENDING_DEPENDENCY", cAck.Status);
        Guid cReceipt = Assert.IsType<Guid>(cAck.ReceiptId);

        using var scope = factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<App02a3ProcessingService>();
        var outbound = scope.ServiceProvider.GetRequiredService<App02a3OutboxService>();
        Assert.Equal("APPLIED", await processor.ProcessAsync(
            tenant, cReceipt, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
        Assert.Equal("APPLIED", await processor.ProcessAsync(
            tenant, cReceipt, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));

        using var db = Open(tenant);
        Guid acceptance = db.QuerySingle<Guid>("""
            SELECT AcceptanceId FROM dbo.App02AcceptanceApplication
            WHERE TenantId=@Tenant AND CReceiptId=@Receipt
            """, new { Tenant = tenant, Receipt = cReceipt });
        Guid sourceLine = db.QuerySingle<Guid>("""
            SELECT SourceLineId FROM dbo.CashHandoverAcceptance
            WHERE TenantId=@Tenant AND Id=@Acceptance
            """, new { Tenant = tenant, Acceptance = acceptance });
        var legs = db.Query<(string EffectKey, decimal AmountDelta)>("""
            SELECT EffectKey,AmountDelta FROM dbo.LedgerEffect
            WHERE TenantId=@Tenant AND BusinessSourceLineId=@SourceLine
            ORDER BY EffectKey
            """, new { Tenant = tenant, SourceLine = sourceLine }).ToArray();
        Assert.Equal(2, legs.Length);
        Assert.Contains(legs, x => x.EffectKey == "CUSTODY-OUT" && x.AmountDelta == -200m);
        Assert.Contains(legs, x => x.EffectKey == "FUND-IN" && x.AmountDelta == 200m);
        Assert.Equal(1, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.App02OutboxMessage
            WHERE TenantId=@Tenant AND CReceiptId=@Receipt
            """, new { Tenant = tenant, Receipt = cReceipt }));

        var replay = await IntakeA3(cashier, c);
        Assert.True(replay.Replayed);
        Assert.Equal(cReceipt, replay.ReceiptId);
        Assert.Equal("APPLIED", replay.Status);
        using (HttpResponseMessage changed = await cashier.PostAsJsonAsync(
            A3 + "intake", c with { CountEvidenceRef = "changed-" + Id() },
            TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal(1, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.CashHandoverAcceptance
            WHERE TenantId=@Tenant AND OfferId=@Offer
            """, new { Tenant = tenant, Offer = s.Offer }));

        // The SQL fixture is shared by prior tests within the same tenant:
        // target THIS committed C message, not the oldest unrelated outbox.
        var message = db.QuerySingle<Guid>("""
            SELECT MessageId FROM dbo.App02AcceptanceApplication
            WHERE TenantId=@Tenant AND CReceiptId=@Receipt
            """, new { Tenant = tenant, Receipt = cReceipt });
        DateTimeOffset at = DateTimeOffset.UtcNow;
        Assert.Equal("CONSUMER_COMMITTED_ACK_LOST",
            await outbound.DispatchOneAsync(tenant, at,
                stopAfterConsumerCommit: true, TestContext.Current.CancellationToken,
                onlyMessageId: message));
        Assert.Equal(1, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.App02SyntheticConsumerInbox
            WHERE TenantId=@Tenant AND MessageId=@Message
            """, new { Tenant = tenant, Message = message }));
        Assert.Equal("LEASED", db.QuerySingle<string>("""
            SELECT Status FROM dbo.App02OutboxDelivery
            WHERE TenantId=@Tenant AND MessageId=@Message
            """, new { Tenant = tenant, Message = message }));
        Assert.Equal("ACKED", await outbound.DispatchOneAsync(tenant,
            at.AddMinutes(3), stopAfterConsumerCommit: false,
            TestContext.Current.CancellationToken, onlyMessageId: message));
        Assert.Equal("ACKED", db.QuerySingle<string>("""
            SELECT Status FROM dbo.App02OutboxDelivery
            WHERE TenantId=@Tenant AND MessageId=@Message
            """, new { Tenant = tenant, Message = message }));
        Assert.Equal(1, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.App02SyntheticConsumerInbox
            WHERE TenantId=@Tenant AND MessageId=@Message
            """, new { Tenant = tenant, Message = message }));
    }

    [Theory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public async Task App02a3_C_before_P_waits_then_CASE_requires_independent_checker_resume(
        string tenant)
    {
        using var factory = fixture.CreateFactory();
        using var setup = SqlScratchFixture.WithTenant(factory.CreateClient(), tenant);
        using var sales = Client(factory, tenant, SalesActor, "sales");
        using var cashier = Client(factory, tenant, CashierActor, "cashier");
        using var maker = Client(factory, tenant, "a3-maker", "cashier_supervisor");
        using var checker = Client(factory, tenant, "a3-checker", "case_checker");
        var s = await SetupA3(tenant, setup, sales);
        App02EventInput p = NewPEvent(s.Scene.Trip, s.Offer);
        App02EventInput c = NewCEvent(s.Scene.Trip, p, s.Count);
        var cAck = await IntakeA3(cashier, c);
        Guid receipt = Assert.IsType<Guid>(cAck.ReceiptId);

        using var scope = factory.Services.CreateScope();
        var worker = scope.ServiceProvider.GetRequiredService<App02a3ProcessingService>();
        Assert.Equal("PENDING_DEPENDENCY", await worker.ProcessAsync(
            tenant, receipt, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
        using var db = Open(tenant);
        Assert.Equal(0, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.App02AcceptanceApplication
            WHERE TenantId=@Tenant AND CReceiptId=@Receipt
            """, new { Tenant = tenant, Receipt = receipt }));
        Assert.Equal("CASE", await worker.ProcessAsync(tenant, receipt,
            DateTimeOffset.UtcNow.AddHours(25), TestContext.Current.CancellationToken));
        Guid caseId = db.QuerySingle<Guid>("""
            SELECT ActiveCaseId FROM dbo.App02EventProgress
            WHERE TenantId=@Tenant AND ReceiptId=@Receipt
            """, new { Tenant = tenant, Receipt = receipt });
        await IntakeA3(sales, p);
        Assert.Equal("CASE", await worker.ProcessAsync(
            tenant, receipt, DateTimeOffset.UtcNow.AddHours(25),
            TestContext.Current.CancellationToken));

        using (HttpResponseMessage proposed = await maker.PostAsJsonAsync(
            A3 + "cases/propose", new App02CaseProposalInput(
                caseId, "APPROVED_RESUME", "P_ARRIVED",
                "CASE-COUNT-" + Id(), "Revalidate exact named P and count"),
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, proposed.StatusCode);
            var proposal = await proposed.Content.ReadFromJsonAsync<App02CaseProposalOutcome>(TestContext.Current.CancellationToken);
            Guid proposalId = Assert.IsType<App02CaseProposalOutcome>(proposal).ProposalId;
            using HttpResponseMessage denied = await maker.PostAsync(
                A3 + "cases/approve/" + proposalId, content: null,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using HttpResponseMessage approved = await checker.PostAsync(
                A3 + "cases/approve/" + proposalId, content: null,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
            var disposition = await approved.Content.ReadFromJsonAsync<App02CaseApprovalOutcome>(TestContext.Current.CancellationToken);
            Assert.Equal("RECEIVED", disposition?.Status);
        }
        Assert.Equal("APPLIED", await worker.ProcessAsync(
            tenant, receipt, DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.App02AcceptanceApplication
            WHERE TenantId=@Tenant AND CReceiptId=@Receipt
            """, new { Tenant = tenant, Receipt = receipt }));
        Assert.Equal(1, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.App02CaseDisposition
            WHERE TenantId=@Tenant AND CaseId=@Case AND Decision=N'APPROVED_RESUME'
            """, new { Tenant = tenant, Case = caseId }));
    }

    [Fact]
    public async Task App02a3_two_workers_and_tenant_role_revocation_fail_closed()
    {
        const string tenant = "tenant-a";
        using var factory = fixture.CreateFactory();
        using var setup = SqlScratchFixture.WithTenant(factory.CreateClient(), tenant);
        using var sales = Client(factory, tenant, SalesActor, "sales");
        using var cashier = Client(factory, tenant, CashierActor, "cashier");
        var s = await SetupA3(tenant, setup, sales);
        App02EventInput p = NewPEvent(s.Scene.Trip, s.Offer);
        App02EventInput c = NewCEvent(s.Scene.Trip, p, s.Count);
        await IntakeA3(sales, p);
        Guid receipt = Assert.IsType<Guid>((await IntakeA3(cashier, c)).ReceiptId);
        using var scope = factory.Services.CreateScope();
        var worker = scope.ServiceProvider.GetRequiredService<App02a3ProcessingService>();
        string[] states = await Task.WhenAll(
            worker.ProcessAsync(tenant, receipt, DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken),
            worker.ProcessAsync(tenant, receipt, DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken));
        Assert.All(states, x => Assert.Equal("APPLIED", x));
        using var db = Open(tenant);
        Assert.Equal(1, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.CashHandoverAcceptance
            WHERE TenantId=@Tenant AND OfferId=@Offer
            """, new { Tenant = tenant, Offer = s.Offer }));

        // New C, revoked current grant (old signed cashier JWT still valid).
        var p2 = NewPEvent(s.Scene.Trip, s.Offer);
        var c2 = NewCEvent(s.Scene.Trip, p2, "never-used-" + Id());
        Guid receipt2 = Assert.IsType<Guid>((await IntakeA3(cashier, c2)).ReceiptId);
        GrantRole(db, tenant, CashierActor, "cashier", false);
        Assert.Equal("CASE", await worker.ProcessAsync(
            tenant, receipt2, DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken));
        using HttpResponseMessage denied = await cashier.PostAsJsonAsync(A3 + "intake",
            c2 with { EventId = "new-" + Id() }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(0, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.App02AcceptanceApplication
            WHERE TenantId=@Tenant AND CReceiptId=@Receipt
            """, new { Tenant = tenant, Receipt = receipt2 }));
        GrantRole(db, tenant, CashierActor, "cashier", true);
    }

    [Fact]
    public async Task App02a3_precommit_ledger_fault_rolls_back_and_retry_posts_once()
    {
        const string tenant = "tenant-b";
        using var factory = fixture.CreateFactory();
        using var setup = SqlScratchFixture.WithTenant(factory.CreateClient(), tenant);
        using var sales = Client(factory, tenant, SalesActor, "sales");
        using var cashier = Client(factory, tenant, CashierActor, "cashier");
        var s = await SetupA3(tenant, setup, sales);
        App02EventInput p = NewPEvent(s.Scene.Trip, s.Offer);
        App02EventInput c = NewCEvent(s.Scene.Trip, p, s.Count);
        await IntakeA3(sales, p);
        Guid receipt = Assert.IsType<Guid>((await IntakeA3(cashier, c)).ReceiptId);
        using var scope = factory.Services.CreateScope();
        var worker = scope.ServiceProvider.GetRequiredService<App02a3ProcessingService>();
        using var db = Open(tenant);
        try
        {
            db.Execute("""
                EXEC(N'
                  CREATE TRIGGER dbo.APP02A3_TestFault ON dbo.LedgerEffect AFTER INSERT AS
                  BEGIN
                    IF EXISTS (
                      SELECT 1 FROM inserted i
                      JOIN dbo.BusinessSourceLine sl ON sl.TenantId=i.TenantId
                        AND sl.Id=i.BusinessSourceLineId
                      JOIN dbo.BusinessSource bs ON bs.TenantId=sl.TenantId
                        AND bs.Id=sl.BusinessSourceId
                      WHERE bs.SourceKind=N''CASH_HANDOVER_ACCEPT''
                        AND bs.SourceNumber LIKE N''APP02:%'')
                    THROW 51220, ''APP02 synthetic precommit ledger fault'', 1;
                  END
                ')
                """);
            Assert.Equal("RETRY_SCHEDULED", await worker.ProcessAsync(
                tenant, receipt, DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken));
            Assert.Equal(0, db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM dbo.App02AcceptanceApplication
                WHERE TenantId=@Tenant AND CReceiptId=@Receipt
                """, new { Tenant = tenant, Receipt = receipt }));
            Assert.Equal(0, db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM dbo.App02OutboxMessage
                WHERE TenantId=@Tenant AND CReceiptId=@Receipt
                """, new { Tenant = tenant, Receipt = receipt }));
            Assert.Equal(1, db.ExecuteScalar<int>("""
                SELECT TechnicalRetryCount FROM dbo.App02EventProgress
                WHERE TenantId=@Tenant AND ReceiptId=@Receipt
                """, new { Tenant = tenant, Receipt = receipt }));
        }
        finally
        {
            db.Execute("DROP TRIGGER IF EXISTS dbo.APP02A3_TestFault");
        }
        Assert.Equal("APPLIED", await worker.ProcessAsync(
            tenant, receipt, DateTimeOffset.UtcNow.AddMinutes(1),
            TestContext.Current.CancellationToken));
        Assert.Equal(1, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.App02AcceptanceApplication
            WHERE TenantId=@Tenant AND CReceiptId=@Receipt
            """, new { Tenant = tenant, Receipt = receipt }));
    }

    [Fact]
    public async Task App02a3_correction_requires_two_actors_and_never_mutates_posted_money()
    {
        const string tenant = "tenant-a";
        using var factory = fixture.CreateFactory();
        using var setup = SqlScratchFixture.WithTenant(factory.CreateClient(), tenant);
        using var sales = Client(factory, tenant, SalesActor, "sales");
        using var cashier = Client(factory, tenant, CashierActor, "cashier");
        using var maker = Client(factory, tenant, "a3-maker", "cashier_supervisor");
        using var checker = Client(factory, tenant, "a3-checker", "case_checker");
        var scene = await SetupA3(tenant, setup, sales);
        var p = NewPEvent(scene.Scene.Trip, scene.Offer);
        var c = NewCEvent(scene.Scene.Trip, p, scene.Count);
        await IntakeA3(sales, p);
        Guid receipt = Assert.IsType<Guid>((await IntakeA3(cashier, c)).ReceiptId);
        using var scope = factory.Services.CreateScope();
        var worker = scope.ServiceProvider.GetRequiredService<App02a3ProcessingService>();
        Assert.Equal("APPLIED", await worker.ProcessAsync(tenant, receipt,
            DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
        using var db = Open(tenant);
        Guid acceptance = db.QuerySingle<Guid>("""
            SELECT AcceptanceId FROM dbo.App02AcceptanceApplication
            WHERE TenantId=@Tenant AND CReceiptId=@Receipt
            """, new { Tenant = tenant, Receipt = receipt });
        int effectsBefore = db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.LedgerEffect e
            JOIN dbo.CashHandoverAcceptance a
                ON a.TenantId=e.TenantId AND a.SourceLineId=e.BusinessSourceLineId
            WHERE a.TenantId=@Tenant AND a.Id=@Acceptance
            """, new { Tenant = tenant, Acceptance = acceptance });
        Assert.Equal(2, effectsBefore);

        using HttpResponseMessage proposed = await maker.PostAsJsonAsync(
            A3 + "corrections/propose",
            new App02CorrectionProposalInput(acceptance, "AUDIT_ANNOTATION",
                "CORR-EVIDENCE-" + Id(), "Nonfinancial evidence correction"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, proposed.StatusCode);
        Guid proposal = Assert.IsType<App02CorrectionProposalOutcome>(
            await proposed.Content.ReadFromJsonAsync<App02CorrectionProposalOutcome>(
                TestContext.Current.CancellationToken)).ProposalId;
        using HttpResponseMessage selfApproval = await maker.PostAsync(
            A3 + "corrections/approve/" + proposal, content: null,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, selfApproval.StatusCode);
        using HttpResponseMessage approved = await checker.PostAsync(
            A3 + "corrections/approve/" + proposal, content: null,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var outcome = Assert.IsType<App02CorrectionOutcome>(
            await approved.Content.ReadFromJsonAsync<App02CorrectionOutcome>(
                TestContext.Current.CancellationToken));
        Assert.Equal("CORRECTION_AUDIT_RECORDED", outcome.Status);
        Assert.Equal(1, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.App02CorrectionLink
            WHERE TenantId=@Tenant AND OriginalAcceptanceId=@Acceptance
              AND CorrectionSourceId=@Source
            """, new { Tenant = tenant, Acceptance = acceptance,
                Source = outcome.CorrectionSourceId }));
        Assert.Equal(0, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.LedgerEffect e
            JOIN dbo.BusinessSourceLine sl ON sl.TenantId=e.TenantId
                AND sl.Id=e.BusinessSourceLineId
            WHERE sl.TenantId=@Tenant AND sl.BusinessSourceId=@Source
            """, new { Tenant = tenant, Source = outcome.CorrectionSourceId }));
        Assert.Equal(effectsBefore, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.LedgerEffect e
            JOIN dbo.CashHandoverAcceptance a
                ON a.TenantId=e.TenantId AND a.SourceLineId=e.BusinessSourceLineId
            WHERE a.TenantId=@Tenant AND a.Id=@Acceptance
            """, new { Tenant = tenant, Acceptance = acceptance }));
        Assert.Throws<SqlException>(() => db.Execute("""
            UPDATE dbo.BusinessSource SET SourceNumber=N'FORBIDDEN'
            WHERE TenantId=@Tenant AND Id=@Source
            """, new { Tenant = tenant, Source = outcome.CorrectionSourceId }));
        Assert.Equal(1, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.App02CorrectionApproval
            WHERE TenantId=@Tenant AND ProposalId=@Proposal
            """, new { Tenant = tenant, Proposal = proposal }));
    }

    [Fact]
    public async Task App02a3_actual_OS_restart_recovers_SQL_pending_C_after_late_P_wakeup()
    {
        const string tenant = "tenant-a";
        using var setupHost = fixture.CreateFactory();
        using var setup = SqlScratchFixture.WithTenant(setupHost.CreateClient(), tenant);
        using var setupSales = Client(setupHost, tenant, SalesActor, "sales");
        var scene = await SetupA3(tenant, setup, setupSales);
        var p = NewPEvent(scene.Scene.Trip, scene.Offer);
        var c = NewCEvent(scene.Scene.Trip, p, scene.Count);
        using var peer = Open("tenant-b");
        int peerBefore = peer.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.App02EventReceipt");
        int originalPid;
        Guid cReceipt;
        await using (var before = await App01e4ApiProcess.StartAsync(
            fixture, TestContext.Current.CancellationToken, app02WorkerEnabled: false))
        {
            originalPid = before.Pid;
            using var cashier = before.Client(tenant, CashierActor, "cashier");
            cReceipt = Assert.IsType<Guid>((await IntakeA3(cashier, c)).ReceiptId);
            using var db = Open(tenant);
            Assert.Equal("PENDING_DEPENDENCY", db.QuerySingle<string>("""
                SELECT Status FROM dbo.App02EventProgress
                WHERE TenantId=@Tenant AND ReceiptId=@Receipt
                """, new { Tenant = tenant, Receipt = cReceipt }));
        }
        // A new executable process uses the same nonce-owned SQL database,
        // not an in-memory TestServer state or static C# fixture.
        await using (var after = await App01e4ApiProcess.StartAsync(
            fixture, TestContext.Current.CancellationToken, app02WorkerEnabled: true))
        {
            Assert.NotEqual(originalPid, after.Pid);
            using var sales = after.Client(tenant, SalesActor, "sales");
            await IntakeA3(sales, p); // P receipt COMMIT wakes new worker.
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(25);
            bool appliedAndAcked = false;
            while (DateTimeOffset.UtcNow < deadline)
            {
                using var db = Open(tenant);
                appliedAndAcked = db.ExecuteScalar<int>("""
                    SELECT COUNT(*) FROM dbo.App02EventProgress progress
                    JOIN dbo.App02AcceptanceApplication app
                        ON app.TenantId=progress.TenantId
                        AND app.CReceiptId=progress.ReceiptId
                    JOIN dbo.App02OutboxDelivery delivery
                        ON delivery.TenantId=app.TenantId
                        AND delivery.MessageId=app.MessageId
                    JOIN dbo.App02SyntheticConsumerInbox consumer
                        ON consumer.TenantId=delivery.TenantId
                        AND consumer.MessageId=delivery.MessageId
                    WHERE progress.TenantId=@Tenant AND progress.ReceiptId=@Receipt
                        AND progress.Status=N'APPLIED' AND delivery.Status=N'ACKED'
                    """, new { Tenant = tenant, Receipt = cReceipt }) == 1;
                if (appliedAndAcked) break;
                await Task.Delay(150, TestContext.Current.CancellationToken);
            }
            Assert.True(appliedAndAcked,
                "A fresh real Kestrel PID must recover durable C+P into exact APP01D posting and consumer ACK.");
        }
        Assert.Equal(peerBefore, peer.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.App02EventReceipt"));
    }

    [Fact]
    public async Task App02a3_six_SQL_rollback_retries_end_in_durable_CASE_without_partial_posting()
    {
        const string tenant = "tenant-b";
        using var factory = fixture.CreateFactory();
        using var setup = SqlScratchFixture.WithTenant(factory.CreateClient(), tenant);
        using var sales = Client(factory, tenant, SalesActor, "sales");
        using var cashier = Client(factory, tenant, CashierActor, "cashier");
        var scene = await SetupA3(tenant, setup, sales);
        var p = NewPEvent(scene.Scene.Trip, scene.Offer);
        var c = NewCEvent(scene.Scene.Trip, p, scene.Count);
        await IntakeA3(sales, p);
        Guid receipt = Assert.IsType<Guid>((await IntakeA3(cashier, c)).ReceiptId);
        using var scope = factory.Services.CreateScope();
        var worker = scope.ServiceProvider.GetRequiredService<App02a3ProcessingService>();
        using var db = Open(tenant);
        try
        {
            db.Execute("""
                EXEC(N'
                  CREATE TRIGGER dbo.APP02A3_TestFaultSix ON dbo.LedgerEffect AFTER INSERT AS
                  BEGIN
                    IF EXISTS (
                      SELECT 1 FROM inserted i
                      JOIN dbo.BusinessSourceLine sl ON sl.TenantId=i.TenantId
                        AND sl.Id=i.BusinessSourceLineId
                      JOIN dbo.BusinessSource bs ON bs.TenantId=sl.TenantId
                        AND bs.Id=sl.BusinessSourceId
                      WHERE bs.SourceKind=N''CASH_HANDOVER_ACCEPT''
                        AND bs.SourceNumber LIKE N''APP02:%'')
                      THROW 51224, ''APP02 synthetic six-retry fault'', 1;
                  END
                ')
                """);
            DateTimeOffset t = DateTimeOffset.UtcNow;
            int[] minutes = [0, 1, 3, 7, 15, 31];
            for (int i = 1; i <= 6; i++)
            {
                string state = await worker.ProcessAsync(tenant, receipt,
                    t.AddMinutes(minutes[i - 1]), TestContext.Current.CancellationToken);
                Assert.Equal(i == 6 ? "CASE" : "RETRY_SCHEDULED", state);
            }
            var progress = db.QuerySingle<(string Status, byte Retries, string Reason)>("""
                SELECT Status,TechnicalRetryCount,ReasonCode
                FROM dbo.App02EventProgress
                WHERE TenantId=@Tenant AND ReceiptId=@Receipt
                """, new { Tenant = tenant, Receipt = receipt });
            Assert.Equal("CASE", progress.Status);
            Assert.Equal(6, progress.Retries);
            Assert.Equal("TECHNICAL_RETRIES_EXHAUSTED", progress.Reason);
            Assert.Equal(0, db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM dbo.App02AcceptanceApplication
                WHERE TenantId=@Tenant AND CReceiptId=@Receipt
                """, new { Tenant = tenant, Receipt = receipt }));
            Assert.Equal(0, db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM dbo.App02OutboxMessage
                WHERE TenantId=@Tenant AND CReceiptId=@Receipt
                """, new { Tenant = tenant, Receipt = receipt }));
        }
        finally
        {
            db.Execute("DROP TRIGGER IF EXISTS dbo.APP02A3_TestFaultSix");
        }
    }

    [Fact]
    public async Task App02a3_six_consumer_failures_end_in_delivery_CASE_without_ACK()
    {
        const string tenant = "tenant-a";
        using var factory = fixture.CreateFactory();
        using var setup = SqlScratchFixture.WithTenant(factory.CreateClient(), tenant);
        using var sales = Client(factory, tenant, SalesActor, "sales");
        using var cashier = Client(factory, tenant, CashierActor, "cashier");
        var scene = await SetupA3(tenant, setup, sales);
        var p = NewPEvent(scene.Scene.Trip, scene.Offer);
        var c = NewCEvent(scene.Scene.Trip, p, scene.Count);
        await IntakeA3(sales, p);
        Guid receipt = Assert.IsType<Guid>((await IntakeA3(cashier, c)).ReceiptId);
        using var scope = factory.Services.CreateScope();
        var worker = scope.ServiceProvider.GetRequiredService<App02a3ProcessingService>();
        var outbound = scope.ServiceProvider.GetRequiredService<App02a3OutboxService>();
        Assert.Equal("APPLIED", await worker.ProcessAsync(tenant, receipt,
            DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
        using var db = Open(tenant);
        Guid message = db.QuerySingle<Guid>("""
            SELECT MessageId FROM dbo.App02AcceptanceApplication
            WHERE TenantId=@Tenant AND CReceiptId=@Receipt
            """, new { Tenant = tenant, Receipt = receipt });
        try
        {
            db.Execute("""
                EXEC(N'
                  CREATE TRIGGER dbo.APP02A3_TestConsumerFaultSix
                  ON dbo.App02SyntheticConsumerInbox AFTER INSERT AS
                  BEGIN
                    THROW 51225, ''APP02 synthetic consumer unavailable'', 1;
                  END
                ')
                """);
            DateTimeOffset t = DateTimeOffset.UtcNow;
            int[] minutes = [0, 1, 3, 7, 15, 31];
            for (int i = 1; i <= 6; i++)
            {
                string state = await outbound.DispatchOneAsync(
                    tenant, t.AddMinutes(minutes[i - 1]),
                    stopAfterConsumerCommit: false,
                    TestContext.Current.CancellationToken, onlyMessageId: message);
                Assert.Equal(i == 6 ? "CASE" : "RETRY_SCHEDULED", state);
            }
            var delivery = db.QuerySingle<(string Status, byte Attempts)>("""
                SELECT Status,AttemptCount FROM dbo.App02OutboxDelivery
                WHERE TenantId=@Tenant AND MessageId=@Message
                """, new { Tenant = tenant, Message = message });
            Assert.Equal("CASE", delivery.Status);
            Assert.Equal(6, delivery.Attempts);
            Assert.Equal(0, db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM dbo.App02SyntheticConsumerInbox
                WHERE TenantId=@Tenant AND MessageId=@Message
                """, new { Tenant = tenant, Message = message }));
        }
        finally
        {
            db.Execute("DROP TRIGGER IF EXISTS dbo.APP02A3_TestConsumerFaultSix");
        }
    }
}
