using System.Net;
using System.Net.Http.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FmcgDms.Api.Tests;

public sealed partial class App01e2CrossCommandRaceTests
{
    [Theory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public Task App02a4_A11_01_P_before_C_reconciles_source_two_legs_and_durable_ACK(string tenant) =>
        A4Run(tenant, "A11-01", async s =>
        {
            await A4IntakeP(s);
            Guid c = await A4IntakeC(s);
            Assert.Equal("APPLIED", await A4Process(s, c));
            await A4Capture(s, "applied");
            Assert.Equal("ACKED", await A4Dispatch(s, A4Message(s), false));
            await A4Capture(s, "ack");
            using var db = Open(tenant);
            Assert.Equal(1, db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM dbo.CashHandoverOffer
                WHERE TenantId=@Tenant AND Id=@Offer
                """, new { Tenant = tenant, s.Offer }));
            Assert.Equal(0, db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM dbo.LedgerEffect e
                JOIN dbo.CashHandoverOffer o ON o.TenantId=e.TenantId
                  AND o.SourceLineId=e.BusinessSourceLineId
                WHERE o.TenantId=@Tenant AND o.Id=@Offer
                """, new { Tenant = tenant, s.Offer }));
        });

    [Theory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public Task App02a4_A11_02_C_before_P_never_posts_early_then_recovers(string tenant) =>
        A4Run(tenant, "A11-02", async s =>
        {
            Guid c = await A4IntakeC(s);
            Assert.Equal("PENDING_DEPENDENCY", await A4Process(s, c));
            await A4Capture(s, "pending");
            await A4IntakeP(s);
            Assert.Equal("APPLIED", await A4Process(s, c));
            await A4Capture(s, "applied");
        });

    [Theory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public Task App02a4_A11_03_missing_invalid_or_foreign_P_is_opaque_and_aged_CASE(string tenant) =>
        A4Run(tenant, "A11-03", async s =>
        {
            // C carries a stable absent/foreign prerequisite identity. There
            // is no cross-DB resolver and no phantom offer-acceptance.
            var foreign = s.C with { PrerequisiteEventId = "foreign-P-" + Id() };
            Guid c = await A4IntakeC(s, foreign);
            await A4Capture(s, "pending");
            using (HttpResponseMessage bad = await s.Cashier.PostAsJsonAsync(
                A3 + "intake", foreign with { EventId = "" },
                TestContext.Current.CancellationToken))
                Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Equal("CASE", await A4Process(s, c,
                DateTimeOffset.UtcNow.AddHours(25)));
            await A4Capture(s, "case");
            using var db = Open(tenant);
            var caseWitness = db.QuerySingle<(string ReasonCode, string OwnerQueue, string NextAction)>("""
                SELECT ReasonCode,OwnerQueue,NextAction FROM dbo.App02ExceptionCase
                WHERE TenantId=@Tenant AND ReceiptId=@Receipt
                """, new { Tenant = tenant, Receipt = c });
            Assert.Equal("MISSING_P_24H", caseWitness.ReasonCode);
            Assert.Equal("CASHIER_SUPERVISION", caseWitness.OwnerQueue);
            Assert.False(string.IsNullOrWhiteSpace(caseWitness.NextAction));
        });

    [Theory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public Task App02a4_A11_04_same_identity_replays_without_new_effects_or_second_success(string tenant) =>
        A4Run(tenant, "A11-04", async s =>
        {
            await A4IntakeP(s);
            Guid first = await A4IntakeC(s);
            Assert.Equal("APPLIED", await A4Process(s, first));
            await A4Capture(s, "applied");
            var before = (await FullSql(tenant)).Sha256;
            App02IntakeResult replay = await IntakeA3(s.Cashier, s.C);
            Assert.True(replay.Replayed);
            Assert.Equal(first, replay.ReceiptId);
            Assert.Equal(before, (await FullSql(tenant)).Sha256);
            await A4Capture(s, "replay");
            var second = s.C with { EventId = "event-c-" + Id() };
            Guid next = await A4IntakeC(s, second);
            Assert.Equal("REJECTED", await A4Process(s, next));
            await A4Capture(s, "second-c");
            using var db = Open(tenant);
            Assert.Equal("OFFER_ALREADY_ACCEPTED", db.QuerySingle<string>("""
                SELECT ReasonCode FROM dbo.App02EventProgress
                WHERE TenantId=@Tenant AND ReceiptId=@Receipt
                """, new { Tenant = tenant, Receipt = next }));
        });

    [Theory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public Task App02a4_A11_05_changed_canonical_content_is_409_and_SQL_unchanged(string tenant) =>
        A4Run(tenant, "A11-05", async s =>
        {
            await A4IntakeP(s);
            Guid c = await A4IntakeC(s);
            Assert.Equal("APPLIED", await A4Process(s, c));
            await A4Capture(s, "applied");
            string immutableBefore = (await FullSql(tenant)).Sha256;
            using (HttpResponseMessage changed = await s.Cashier.PostAsJsonAsync(
                A3 + "intake", s.C with { CountEvidenceRef = "mutated-" + Id() },
                TestContext.Current.CancellationToken))
                Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
            Assert.Equal(immutableBefore, (await FullSql(tenant)).Sha256);
            using var db = Open(tenant);
            Assert.Equal(1, db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM dbo.App02EventReceipt
                WHERE TenantId=@Tenant AND ProducerId=@Producer
                  AND EventId=@EventId
                """, new { Tenant = tenant, Producer = s.C.ProducerId,
                    s.C.EventId }));
            await A4Capture(s, "conflict");
        });

    [Theory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public Task App02a4_A11_06_competing_workers_post_exactly_one_cash_acceptance(string tenant) =>
        A4Run(tenant, "A11-06", async s =>
        {
            await A4IntakeP(s);
            Guid first = await A4IntakeC(s);
            Guid second = await A4IntakeC(s,
                s.C with { EventId = "event-c-" + Id() });
            string[] outcomes = await Task.WhenAll(A4Process(s, first),
                A4Process(s, second));
            Assert.Equal(1, outcomes.Count(x => x == "APPLIED"));
            Assert.Equal(1, outcomes.Count(x => x == "REJECTED"));
            await A4Capture(s, "compete");
        });

    [Theory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public Task App02a4_A11_07_precommit_SQL_fault_rolls_back_every_effect_and_retries(string tenant) =>
        A4Run(tenant, "A11-07", async s =>
        {
            await A4IntakeP(s);
            Guid c = await A4IntakeC(s);
            using var db = Open(tenant);
            try
            {
                db.Execute("""
                    EXEC(N'
                      CREATE TRIGGER dbo.APP02A4_LedgerFault
                      ON dbo.LedgerEffect AFTER INSERT AS
                      BEGIN
                        IF EXISTS (
                          SELECT 1 FROM inserted i
                          JOIN dbo.BusinessSourceLine sl
                            ON sl.TenantId=i.TenantId
                            AND sl.Id=i.BusinessSourceLineId
                          JOIN dbo.BusinessSource bs
                            ON bs.TenantId=sl.TenantId AND bs.Id=sl.BusinessSourceId
                          WHERE bs.SourceKind=N''CASH_HANDOVER_ACCEPT''
                            AND bs.SourceNumber LIKE N''APP02:%'')
                          THROW 51260, ''APP02 A4 pre-commit fault'', 1;
                      END
                    ')
                    """);
                Assert.Equal("RETRY_SCHEDULED", await A4Process(s, c));
                await A4Capture(s, "rolled-back");
            }
            finally { db.Execute("DROP TRIGGER IF EXISTS dbo.APP02A4_LedgerFault"); }
            Assert.Equal("APPLIED", await A4Process(s, c,
                DateTimeOffset.UtcNow.AddMinutes(1)));
            await A4Capture(s, "recovered");
        });

    [Theory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public Task App02a4_A11_08_lost_intake_apply_and_outbox_ACK_recovers_same_ID(string tenant) =>
        A4Run(tenant, "A11-08", async s =>
        {
            using var disconnected = Client(s.Factory, tenant, CashierActor,
                "cashier", new AfterServerAckLossHandler(A3 + "intake"));
            await Assert.ThrowsAsync<HttpRequestException>(async () =>
            {
                using var _ = await disconnected.PostAsJsonAsync(
                    A3 + "intake", s.C, TestContext.Current.CancellationToken);
            });
            using var db = Open(tenant);
            Guid receipt = db.QuerySingle<Guid>("""
                SELECT ReceiptId FROM dbo.App02EventReceipt
                WHERE TenantId=@Tenant AND ProducerId=@Producer
                  AND EventId=@EventId
                """, new { Tenant = tenant, Producer = s.C.ProducerId,
                    s.C.EventId });
            var lookup = await s.Cashier.GetFromJsonAsync<App02OutcomeResult>(
                A3 + "outcome?producerId=" + Uri.EscapeDataString(s.C.ProducerId) +
                "&eventId=" + Uri.EscapeDataString(s.C.EventId),
                TestContext.Current.CancellationToken);
            Assert.Equal(receipt, lookup?.ReceiptId);
            Assert.Equal("PENDING_DEPENDENCY", lookup?.Status);
            var replay = await IntakeA3(s.Cashier, s.C);
            Assert.True(replay.Replayed);
            Assert.Equal(receipt, replay.ReceiptId);
            await A4Capture(s, "intake-lost");
            await A4IntakeP(s);
            Assert.Equal("APPLIED", await A4Process(s, receipt));
            Guid message = A4Message(s);
            Assert.Equal("CONSUMER_COMMITTED_ACK_LOST",
                await A4Dispatch(s, message, true));
            await A4Capture(s, "outbound-lost");
            var appliedLookup = await s.Cashier.GetFromJsonAsync<App02OutcomeResult>(
                A3 + "outcome?producerId=" + Uri.EscapeDataString(s.C.ProducerId) +
                "&eventId=" + Uri.EscapeDataString(s.C.EventId),
                TestContext.Current.CancellationToken);
            Assert.Equal("APPLIED", appliedLookup?.Status);
            Assert.Equal("ACKED", await A4Dispatch(s, message,
                false, DateTimeOffset.UtcNow.AddMinutes(3)));
            Assert.Equal("ACKED", db.QuerySingle<string>("""
                SELECT Status FROM dbo.App02OutboxDelivery
                WHERE TenantId=@Tenant AND MessageId=@Message
                """, new { Tenant = tenant, Message = message }));
            await A4Capture(s, "recovered");
        });

    [Theory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public Task App02a4_A11_09_real_OS_restart_recovers_pending_and_outbound(string tenant) =>
        A4Run(tenant, "A11-09", async s =>
        {
            Guid c;
            int beforePid;
            await using (var before = await App01e4ApiProcess.StartAsync(
                fixture, TestContext.Current.CancellationToken,
                app02WorkerEnabled: false))
            {
                beforePid = before.Pid;
                using var cashier = before.Client(tenant, CashierActor, "cashier");
                c = Assert.IsType<Guid>((await IntakeA3(cashier, s.C)).ReceiptId);
                await A4Capture(s, "os-pending");
            }
            await using (var after = await App01e4ApiProcess.StartAsync(
                fixture, TestContext.Current.CancellationToken,
                app02WorkerEnabled: true))
            {
                Assert.NotEqual(beforePid, after.Pid);
                using var sales = after.Client(tenant, SalesActor, "sales");
                await IntakeA3(sales, s.P);
                bool recovered = false;
                DateTimeOffset until = DateTimeOffset.UtcNow.AddSeconds(30);
                while (DateTimeOffset.UtcNow < until)
                {
                    using var db = Open(tenant);
                    recovered = db.ExecuteScalar<int>("""
                        SELECT COUNT(*) FROM dbo.App02AcceptanceApplication a
                        JOIN dbo.App02EventProgress p ON p.TenantId=a.TenantId
                          AND p.ReceiptId=a.CReceiptId
                        JOIN dbo.App02OutboxDelivery d ON d.TenantId=a.TenantId
                          AND d.MessageId=a.MessageId
                        JOIN dbo.App02SyntheticConsumerInbox i
                          ON i.TenantId=d.TenantId AND i.MessageId=d.MessageId
                        WHERE a.TenantId=@Tenant AND a.CReceiptId=@Receipt
                          AND p.Status=N'APPLIED' AND d.Status=N'ACKED'
                        """, new { Tenant = tenant, Receipt = c }) == 1;
                    if (recovered) break;
                    await Task.Delay(150, TestContext.Current.CancellationToken);
                }
                Assert.True(recovered, "New OS PID failed to resume C and ACK outbox");
            }
            await A4Capture(s, "os-recovered");
        });

    [Theory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public Task App02a4_A11_10_revoked_or_wrong_role_and_foreign_tenant_fail_closed(string tenant) =>
        A4Run(tenant, "A11-10", async s =>
        {
            await A4IntakeP(s);
            Guid c = await A4IntakeC(s);
            using var db = Open(tenant);
            GrantRole(db, tenant, CashierActor, "cashier", false);
            using (HttpResponseMessage wrong = await s.Maker.PostAsJsonAsync(
                A3 + "intake", s.C with { ProducerId = "a3-maker", EventId = "wrong-" + Id() },
                TestContext.Current.CancellationToken))
                Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
            using (HttpResponseMessage revoked = await s.Cashier.PostAsJsonAsync(
                A3 + "intake", s.C with { EventId = "revoked-" + Id() },
                TestContext.Current.CancellationToken))
                Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
            Assert.Equal("CASE", await A4Process(s, c));
            string other = tenant == "tenant-a" ? "tenant-b" : "tenant-a";
            using var foreign = Client(s.Factory, other, CashierActor, "cashier");
            using HttpResponseMessage lookup = await foreign.GetAsync(
                A3 + "outcome?producerId=" + Uri.EscapeDataString(s.C.ProducerId) +
                "&eventId=" + Uri.EscapeDataString(s.C.EventId),
                TestContext.Current.CancellationToken);
            Assert.NotEqual(HttpStatusCode.OK, lookup.StatusCode == HttpStatusCode.OK
                ? (await lookup.Content.ReadFromJsonAsync<App02OutcomeResult>(
                    TestContext.Current.CancellationToken))?.Status == "UNKNOWN"
                    ? HttpStatusCode.NotFound : HttpStatusCode.OK
                : lookup.StatusCode);
            await A4Capture(s, "revoked");
        });

    [Theory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public Task App02a4_A11_11_24h_CASE_late_P_independent_resume_correction_and_six_retries(string tenant) =>
        A4Run(tenant, "A11-11", async s =>
        {
            Guid c = await A4IntakeC(s);
            DateTimeOffset expired = DateTimeOffset.UtcNow.AddHours(25);
            Assert.Equal("CASE", await A4Process(s, c, expired));
            await A4Capture(s, "missing-case");
            using var db = Open(tenant);
            Guid caseId = db.QuerySingle<Guid>("""
                SELECT ActiveCaseId FROM dbo.App02EventProgress
                WHERE TenantId=@Tenant AND ReceiptId=@Receipt
                """, new { Tenant = tenant, Receipt = c });
            await A4IntakeP(s);
            Assert.Equal("CASE", await A4Process(s, c, expired));
            await A4Capture(s, "late-case");
            using HttpResponseMessage propose = await s.Maker.PostAsJsonAsync(
                A3 + "cases/propose",
                new App02CaseProposalInput(caseId, "APPROVED_RESUME",
                    "P_ARRIVED", "A4-CASE-" + Id(),
                    "Revalidate named P and independently counted cash"),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, propose.StatusCode);
            Guid proposal = Assert.IsType<App02CaseProposalOutcome>(
                await propose.Content.ReadFromJsonAsync<App02CaseProposalOutcome>(
                    TestContext.Current.CancellationToken)).ProposalId;
            using (HttpResponseMessage self = await s.Maker.PostAsync(
                A3 + "cases/approve/" + proposal, null,
                TestContext.Current.CancellationToken))
                Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
            using HttpResponseMessage approve = await s.Checker.PostAsync(
                A3 + "cases/approve/" + proposal, null,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
            await A4Capture(s, "resumed");
            Assert.Equal("APPLIED", await A4Process(s, c));
            await A4Capture(s, "applied");
            Guid acceptance = db.QuerySingle<Guid>("""
                SELECT AcceptanceId FROM dbo.App02AcceptanceApplication
                WHERE TenantId=@Tenant AND CReceiptId=@Receipt
                """, new { Tenant = tenant, Receipt = c });
            using HttpResponseMessage corr = await s.Maker.PostAsJsonAsync(
                A3 + "corrections/propose",
                new App02CorrectionProposalInput(acceptance, "AUDIT_ANNOTATION",
                    "A4-CORR-" + Id(), "Immutable nonposting annotation"),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, corr.StatusCode);
            Guid correctionProposal = Assert.IsType<App02CorrectionProposalOutcome>(
                await corr.Content.ReadFromJsonAsync<App02CorrectionProposalOutcome>(
                    TestContext.Current.CancellationToken)).ProposalId;
            using (HttpResponseMessage self = await s.Maker.PostAsync(
                A3 + "corrections/approve/" + correctionProposal, null,
                TestContext.Current.CancellationToken))
                Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
            using HttpResponseMessage correctionApproved = await s.Checker.PostAsync(
                A3 + "corrections/approve/" + correctionProposal, null,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, correctionApproved.StatusCode);
            await A4Capture(s, "corrected");

            // Separate scene/Trip: a durable SQL ledger failure advances the
            // technical budget exactly six times; retries are due-fenced.
            var another = await SetupA3(tenant, s.Setup, s.Sales);
            App02EventInput p2 = NewPEvent(another.Scene.Trip, another.Offer);
            App02EventInput c2 = NewCEvent(another.Scene.Trip, p2, another.Count);
            await A4IntakeP(s, p2);
            Guid retryReceipt = await A4IntakeC(s, c2);
            try
            {
                db.Execute("""
                    EXEC(N'
                      CREATE TRIGGER dbo.APP02A4_RetryFault
                      ON dbo.LedgerEffect AFTER INSERT AS
                      BEGIN
                        IF EXISTS (
                          SELECT 1 FROM inserted i
                          JOIN dbo.BusinessSourceLine sl
                            ON sl.TenantId=i.TenantId
                            AND sl.Id=i.BusinessSourceLineId
                          JOIN dbo.BusinessSource bs
                            ON bs.TenantId=sl.TenantId AND bs.Id=sl.BusinessSourceId
                          WHERE bs.SourceKind=N''CASH_HANDOVER_ACCEPT''
                            AND bs.SourceNumber LIKE N''APP02:%'')
                          THROW 51261, ''APP02 A4 six-retry fault'', 1;
                      END
                    ')
                    """);
                int[] minutes = [0, 1, 3, 7, 15, 31];
                DateTimeOffset start = DateTimeOffset.UtcNow.AddHours(25);
                for (int i = 0; i < 6; i++)
                    Assert.Equal(i == 5 ? "CASE" : "RETRY_SCHEDULED",
                        await A4Process(s, retryReceipt, start.AddMinutes(minutes[i])));
                await A4Capture(s, "six-retries", another.Scene.Trip);
                Assert.Equal("TECHNICAL_RETRIES_EXHAUSTED",
                    db.QuerySingle<string>("""
                        SELECT ReasonCode FROM dbo.App02EventProgress
                        WHERE TenantId=@Tenant AND ReceiptId=@Receipt
                        """, new { Tenant = tenant, Receipt = retryReceipt }));
            }
            finally { db.Execute("DROP TRIGGER IF EXISTS dbo.APP02A4_RetryFault"); }
        });

    [Theory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public Task App02a4_A11_12_full_two_tenant_SQL_reconciliation_and_no_peer_writes(string tenant) =>
        A4Run(tenant, "A11-12", async s =>
        {
            SqlSnapshot before = ReadSql(tenant, s.Scene);
            await A4IntakeP(s);
            Guid receipt = await A4IntakeC(s);
            Assert.Equal("APPLIED", await A4Process(s, receipt));
            await A4Capture(s, "applied");
            SqlSnapshot after = ReadSql(tenant, s.Scene);
            Assert.Equal(before.Numbers.ArA, after.Numbers.ArA);
            Assert.Equal(before.Numbers.ArB, after.Numbers.ArB);
            Assert.Equal(before.Numbers.Cogs, after.Numbers.Cogs);
            Assert.Equal(before.Numbers.Fund + 200m, after.Numbers.Fund);
            Assert.Equal(before.Numbers.Custody - 200m, after.Numbers.Custody);
            using var db = Open(tenant);
            Assert.Equal(0, db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM dbo.BusinessSourceLine sl
                JOIN dbo.App02CorrectionLink c
                  ON c.TenantId=sl.TenantId
                  AND c.CorrectionSourceId=sl.BusinessSourceId
                JOIN dbo.LedgerEffect e
                  ON e.TenantId=sl.TenantId AND e.BusinessSourceLineId=sl.Id
                WHERE sl.TenantId=@Tenant
                """, new { Tenant = tenant }));
            Assert.Equal("ACKED", await A4Dispatch(s, A4Message(s), false));
            await A4Capture(s, "reconciled");
        });
}
