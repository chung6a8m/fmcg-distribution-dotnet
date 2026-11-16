using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FmcgDms.Api.Tests;

/// <summary>
/// APP-01A.3 two physical nonce-owned LocalDB tenants, real HTTP authorization
/// and SQL fault/concurrency evidence. All tests are synthetic and isolated.
/// </summary>
public sealed partial class App01a2CommandTests
{
    private const string AuditRoot = "/api/synthetic/app01a/audit/as-of";

    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string AuditUrl(DateTimeOffset cutoff) =>
        AuditRoot + "?asOf=" + Uri.EscapeDataString(cutoff.ToUniversalTime()
            .ToString("O", CultureInfo.InvariantCulture));

    private static async Task<App01aAuditSnapshot> Audit(HttpClient client, DateTimeOffset cutoff) =>
        Assert.IsType<App01aAuditSnapshot>(await client.GetFromJsonAsync<App01aAuditSnapshot>(
            AuditUrl(cutoff), TestContext.Current.CancellationToken));

    private static async Task AssertInjectedFault(HttpClient client, string route, object request)
    {
        try
        {
            using var response = await client.PostAsJsonAsync(
                Root + route, request, TestContext.Current.CancellationToken);
            Assert.False(response.IsSuccessStatusCode);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // TestServer is permitted to propagate an injected SQL exception.
        }
    }

    [Fact]
    public void Approval_migration_is_replay_safe_in_both_tenants_and_rejects_self_approval()
    {
        var a = Seed(A);
        var b = Seed(B);
        ScratchMigrator.Migrate(fixture.TenantAConnectionString, A, fixture.Nonce);
        ScratchMigrator.Migrate(fixture.TenantBConnectionString, B, fixture.Nonce);
        foreach (var tenant in new[] { A, B })
        {
            using var db = Open(fixture, tenant);
            Assert.Equal(1, db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.VersionInfo WHERE Version=202611160003"));
            Assert.Equal(1, db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM sys.tables WHERE name=N'CreditPolicyApproval'"));
        }

        using var dbA = Open(fixture, A);
        Assert.Throws<SqlException>(() => dbA.Execute("""
            INSERT dbo.CreditPolicyApproval(Id,TenantId,TripId,CustomerId,
                PoolCode,CurrencyUnit,CaseNumber,PolicyVersion,ProposedBy,CheckedBy,
                Decision,Reason,EvidenceRef,ApprovedLimit,EffectiveFromUtc,
                EffectiveUntilUtc,ReviewedAtUtc)
            VALUES(@Id,@Tenant,@Trip,@Customer,N'B',N'SYNTHETIC_UNITLESS',
                @Case,N'v1',N'maker',N'maker',N'APPROVED',N'self-approval',
                N'evidence',100,DATEADD(day,-1,SYSUTCDATETIME()),
                DATEADD(day,1,SYSUTCDATETIME()),SYSUTCDATETIME());
            """, new { Id = Guid.NewGuid(), Tenant = A, a.Trip, a.Customer, Case = NewId() }));
        Assert.Throws<SqlException>(() => dbA.Execute("""
            INSERT dbo.CreditPolicyApproval(Id,TenantId,TripId,CustomerId,
                PoolCode,CurrencyUnit,CaseNumber,PolicyVersion,ProposedBy,
                Decision,Reason,ApprovedLimit,EffectiveFromUtc,EffectiveUntilUtc)
            VALUES(@Id,@Tenant,@Trip,@Customer,N'B',N'SYNTHETIC_UNITLESS',
                @Case,N'v1',N'maker',N'PENDING',N'foreign tenant',100,
                DATEADD(day,-1,SYSUTCDATETIME()),DATEADD(day,1,SYSUTCDATETIME()));
            """, new { Id = Guid.NewGuid(), Tenant = B, a.Trip, a.Customer, Case = NewId() }));
        Assert.Equal(1, dbA.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.CreditPolicyApproval WHERE TenantId=@Tenant AND TripId=@Trip",
            new { Tenant = A, a.Trip }));
        _ = b; // Both tenants have distinct approved fixture rows.
    }

    [Fact]
    public async Task Credit_policy_is_fail_closed_when_unset_pending_expired_or_over_limit()
    {
        var s = Seed(A);
        using var db = Open(fixture, A);
        using var factory = fixture.CreateFactory();
        using var maker = SqlScratchFixture.WithTenant(factory.CreateClient(), A);

        db.Execute("UPDATE dbo.CreditPolicyApproval SET Decision=N'PENDING' WHERE TripId=@Trip",
            new { s.Trip });
        Assert.Equal("CREDIT_APPROVAL_REQUIRED",
            (await Send(maker, "grant-b-credit", Credit(s, 10))).Outcome.ReasonCode);
        db.Execute("UPDATE dbo.CreditPolicyApproval SET Decision=N'REJECTED' WHERE TripId=@Trip",
            new { s.Trip });
        Assert.Equal("CREDIT_APPROVAL_REQUIRED",
            (await Send(maker, "grant-b-credit", Credit(s, 10))).Outcome.ReasonCode);
        db.Execute("""
            UPDATE dbo.CreditPolicyApproval SET Decision=N'APPROVED',
                EffectiveFromUtc=DATEADD(day,-2,SYSUTCDATETIME()),
                EffectiveUntilUtc=DATEADD(day,-1,SYSUTCDATETIME())
            WHERE TripId=@Trip
            """, new { s.Trip });
        Assert.Equal("CREDIT_APPROVAL_REQUIRED",
            (await Send(maker, "grant-b-credit", Credit(s, 10))).Outcome.ReasonCode);
        db.Execute("""
            UPDATE dbo.CreditPolicyApproval SET ApprovedLimit=50,
                EffectiveUntilUtc=DATEADD(day,1,SYSUTCDATETIME())
            WHERE TripId=@Trip
            """, new { s.Trip });
        Assert.Equal("CREDIT_POLICY_LIMIT_MISMATCH",
            (await Send(maker, "grant-b-credit", Credit(s, 10))).Outcome.ReasonCode);
        db.Execute("UPDATE dbo.CreditPolicyApproval SET ApprovedLimit=100 WHERE TripId=@Trip",
            new { s.Trip });

        using var checker = SqlScratchFixture.WithTenant(factory.CreateClient(), A,
            actor: "synthetic-finance-checker", roles: ["warehouse"]);
        Assert.Equal("CREDIT_CHECKER_CONFLICT",
            (await Send(checker, "grant-b-credit", Credit(s, 10))).Outcome.ReasonCode);
        Assert.Equal("B_CREDIT_GRANTED",
            (await Send(maker, "grant-b-credit", Credit(s, 10))).Outcome.OutcomeCode);
        Assert.Equal(10m, db.ExecuteScalar<decimal>(
            "SELECT GrantedAmount FROM dbo.BCreditPool WHERE TripId=@Trip", new { s.Trip }));
        Assert.Equal(1, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.BCreditGrant WHERE TripId=@Trip AND ApprovalId IS NOT NULL",
            new { s.Trip }));
    }

    [Fact]
    public async Task Future_dated_checker_review_cannot_authorize_credit_or_change_durable_rejection()
    {
        var s = Seed(A);
        using var db = Open(fixture, A);
        db.Execute("""
            UPDATE dbo.CreditPolicyApproval
            SET ReviewedAtUtc=DATEADD(day,1,SYSUTCDATETIME())
            WHERE TenantId=@Tenant AND TripId=@Trip
            """, new { Tenant = A, s.Trip });
        using var factory = fixture.CreateFactory();
        using var writer = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var request = Credit(s, 10);
        var denied = await Send(writer, "grant-b-credit", request);
        Assert.Equal("REJECTED", denied.Outcome.Status);
        Assert.Equal("CREDIT_APPROVAL_REQUIRED", denied.Outcome.ReasonCode);
        db.Execute("""
            UPDATE dbo.CreditPolicyApproval SET ReviewedAtUtc=DATEADD(minute,-1,SYSUTCDATETIME())
            WHERE TenantId=@Tenant AND TripId=@Trip
            """, new { Tenant = A, s.Trip });
        var replay = await Send(writer, "grant-b-credit", request);
        Assert.True(replay.Outcome.Replayed);
        Assert.Equal("CREDIT_APPROVAL_REQUIRED", replay.Outcome.ReasonCode);
        Assert.Equal("B_CREDIT_GRANTED",
            (await Send(writer, "grant-b-credit", Credit(s, 10))).Outcome.OutcomeCode);
        Assert.Equal(10m, db.ExecuteScalar<decimal>(
            "SELECT GrantedAmount FROM dbo.BCreditPool WHERE TripId=@Trip", new { s.Trip }));
        Assert.Equal(1, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.BCreditGrant WHERE TripId=@Trip", new { s.Trip }));
    }

    [Fact]
    public async Task Noncanonical_checker_and_signed_actor_whitespace_cannot_bypass_separation()
    {
        var s = Seed(A);
        using var factory = fixture.CreateFactory();
        foreach (string actor in new[] { "synthetic-finance-checker ", " synthetic-finance-checker" })
        {
            using var client = SqlScratchFixture.WithTenant(factory.CreateClient(), A,
                actor: actor, roles: ["warehouse"]);
            using var response = await client.PostAsJsonAsync(
                Root + "grant-b-credit", Credit(s, 10), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        using var db = Open(fixture, A);
        Assert.Throws<SqlException>(() => db.Execute("""
            UPDATE dbo.CreditPolicyApproval SET CheckedBy=N'synthetic-finance-checker '
            WHERE TenantId=@Tenant AND TripId=@Trip
            """, new { Tenant = A, s.Trip }));
        using var checker = SqlScratchFixture.WithTenant(factory.CreateClient(), A,
            actor: "synthetic-finance-checker", roles: ["warehouse"]);
        Assert.Equal("CREDIT_CHECKER_CONFLICT",
            (await Send(checker, "grant-b-credit", Credit(s, 10))).Outcome.ReasonCode);
        Assert.Equal(0m, db.ExecuteScalar<decimal>(
            "SELECT GrantedAmount FROM dbo.BCreditPool WHERE TripId=@Trip", new { s.Trip }));
    }

    [Fact]
    public async Task Used_approval_policy_evidence_cannot_be_rewritten_or_deleted()
    {
        var s = Seed(A);
        using var factory = fixture.CreateFactory();
        using var writer = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        Assert.Equal("B_CREDIT_GRANTED",
            (await Send(writer, "grant-b-credit", Credit(s, 10))).Outcome.OutcomeCode);
        using var db = Open(fixture, A);
        Assert.Throws<SqlException>(() => db.Execute("""
            UPDATE dbo.CreditPolicyApproval SET PolicyVersion=N'rewritten'
            WHERE TenantId=@Tenant AND TripId=@Trip
            """, new { Tenant = A, s.Trip }));
        Assert.Throws<SqlException>(() => db.Execute("""
            DELETE dbo.CreditPolicyApproval WHERE TenantId=@Tenant AND TripId=@Trip
            """, new { Tenant = A, s.Trip }));
        using var auditor = SqlScratchFixture.WithTenant(factory.CreateClient(), A,
            actor: "audit-policy", roles: ["auditor"]);
        var report = await Audit(auditor, DateTimeOffset.UtcNow.AddSeconds(1));
        var grant = Assert.Single(report.CreditGrants, g => g.TripId == s.Trip);
        Assert.Equal("SYNTHETIC-POLICY-v1", grant.PolicyVersion);
        Assert.Equal(100m, grant.ApprovedLimit);
        Assert.NotNull(grant.ReviewedAtUtc);
        Assert.Equal("synthetic-case-reviewed", grant.EvidenceRef);
        Assert.Empty(report.ReconciliationIssues);
    }

    [Fact]
    public async Task Multiple_concurrent_credit_approvals_block_grant_instead_of_cherry_picking()
    {
        var s = Seed(A);
        using var db = Open(fixture, A);
        db.Execute("""
            INSERT dbo.CreditPolicyApproval(Id,TenantId,TripId,CustomerId,
                PoolCode,CurrencyUnit,CaseNumber,PolicyVersion,ProposedBy,
                CheckedBy,Decision,Reason,EvidenceRef,ApprovedLimit,
                EffectiveFromUtc,EffectiveUntilUtc,ReviewedAtUtc)
            SELECT @Id,TenantId,TripId,CustomerId,PoolCode,CurrencyUnit,
                @Case,PolicyVersion,ProposedBy,CheckedBy,Decision,Reason,EvidenceRef,
                ApprovedLimit,EffectiveFromUtc,EffectiveUntilUtc,ReviewedAtUtc
            FROM dbo.CreditPolicyApproval WHERE TenantId=@Tenant AND TripId=@Trip
            """, new { Id = Guid.NewGuid(), Tenant = A, s.Trip, Case = NewId() });
        using var factory = fixture.CreateFactory();
        using var writer = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var denied = await Send(writer, "grant-b-credit", Credit(s, 15));
        Assert.Equal("CREDIT_APPROVAL_AMBIGUOUS", denied.Outcome.ReasonCode);
        Assert.Equal(0m, db.ExecuteScalar<decimal>(
            "SELECT GrantedAmount FROM dbo.BCreditPool WHERE TripId=@Trip", new { s.Trip }));
        Assert.Equal(0, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.LedgerEffect WHERE LedgerKind=N'RIGHT_CREDIT_B' AND CustomerId=@Customer",
            new { s.Customer }));
    }

    [Fact]
    public async Task AsOf_exposes_actor_source_approval_effects_and_reconciled_A_B_rights()
    {
        var s = Seed(A);
        using var db = Open(fixture, A);
        var before = Utc(db.ExecuteScalar<DateTime>("SELECT SYSUTCDATETIME()"));
        using var factory = fixture.CreateFactory();
        using var writer = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var reserve = await Send(writer, "reserve-a-stock", Reserve(s, 40));
        var quota = await Send(writer, "grant-b-quota", Quota(s, 30));
        var credit = await Send(writer, "grant-b-credit", Credit(s, 25));
        var confirm = await Send(writer, "confirm-a-order", Confirm(s));
        Assert.All(new[] { reserve, quota, credit, confirm },
            x => Assert.Equal("APPLIED", x.Outcome.Status));
        var after = Utc(db.ExecuteScalar<DateTime>("SELECT DATEADD(second,1,SYSUTCDATETIME())"));
        using var auditor = SqlScratchFixture.WithTenant(factory.CreateClient(), A,
            actor: "synthetic-auditor", roles: ["auditor"]);
        var early = await Audit(auditor, before);
        foreach (var source in new[] { reserve.Outcome.SourceId, quota.Outcome.SourceId,
                     credit.Outcome.SourceId, confirm.Outcome.SourceId })
            Assert.DoesNotContain(early.Sources, x => x.Id == source);

        var report = await Audit(auditor, after);
        Assert.Equal(A, report.TenantId);
        var expectedSources = new[] { reserve.Outcome.SourceId, quota.Outcome.SourceId,
            credit.Outcome.SourceId, confirm.Outcome.SourceId };
        foreach (Guid? source in expectedSources)
        {
            var row = Assert.Single(report.Sources, x => x.Id == source);
            Assert.Equal("synthetic-actor", row.ActorId);
            Assert.True(row.LineCount >= 1);
        }
        var allocation = Assert.Single(report.StockAllocations,
            x => x.StockLocationId == s.Warehouse && x.SkuId == s.Sku);
        Assert.Equal(70m, allocation.RightGranted);
        Assert.Equal(70m, allocation.EffectGranted);
        var grant = Assert.Single(report.CreditGrants,
            x => x.SourceId == credit.Outcome.SourceId);
        Assert.NotNull(grant.ApprovalId);
        Assert.Equal("SYNTHETIC-POLICY-v1", grant.PolicyVersion);
        Assert.Equal("synthetic-finance-checker", grant.CheckedBy);
        Assert.Equal("APPROVED", grant.Decision);
        Assert.Contains(report.Effects, x => x.SourceId == reserve.Outcome.SourceId
            && x.LedgerKind == "RIGHT_A" && x.QuantityDelta == 40m);
        Assert.Contains(report.Effects, x => x.SourceId == quota.Outcome.SourceId
            && x.LedgerKind == "RIGHT_B" && x.QuantityDelta == 30m);
        Assert.Contains(report.Effects, x => x.SourceId == credit.Outcome.SourceId
            && x.LedgerKind == "RIGHT_CREDIT_B" && x.AmountDelta == 25m);
        Assert.DoesNotContain(report.Effects,
            x => x.SourceId == confirm.Outcome.SourceId);
        var confirmLine = Assert.Single(report.SourceLines,
            x => x.SourceId == confirm.Outcome.SourceId);
        Assert.Equal(s.Sku, confirmLine.SkuId);
        Assert.Equal(40m, confirmLine.BaseQuantity);
        Assert.DoesNotContain(report.StockRights, x => x.BusinessSourceLineId == confirmLine.Id);
        Assert.Contains(report.StockRights, x => x.RightKind == "A"
            && x.GrantedBaseQuantity == 40m);
        Assert.Contains(report.StockRights, x => x.RightKind == "B"
            && x.GrantedBaseQuantity == 30m);
        // Prior APP-01C.2 tests may have posted sales elsewhere in the same
        // physical tenant fixture. This A-confirm must have no financial
        // effects of *its own*, rather than assert the entire tenant is empty.
        var thisSceneSourceIds = report.Sources.Where(x => x.TripId == s.Trip)
            .Select(x => x.Id).ToHashSet();
        Assert.DoesNotContain(report.Effects,
            x => thisSceneSourceIds.Contains(x.SourceId)
                && (x.LedgerKind is "AR" or "COGS" or "REVENUE" or "STOCK"));
        Assert.Empty(report.ReconciliationIssues);
    }

    [Fact]
    public async Task Audit_detects_balanced_totals_with_wrong_per_line_effects()
    {
        var s = Seed(A);
        using var factory = fixture.CreateFactory();
        using var writer = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        var reserve = await Send(writer, "reserve-a-stock", Reserve(s, 40));
        var quota = await Send(writer, "grant-b-quota", Quota(s, 30));
        Assert.Equal("APPLIED", reserve.Outcome.Status);
        Assert.Equal("APPLIED", quota.Outcome.Status);
        using var db = Open(fixture, A);
        const string patchEffects = """
            UPDATE e SET QuantityDelta=CASE WHEN s.Id=@Reserve THEN @AQty ELSE @BQty END
            FROM dbo.LedgerEffect e
            JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId
                AND l.Id=e.BusinessSourceLineId
            JOIN dbo.BusinessSource s ON s.TenantId=l.TenantId AND s.Id=l.BusinessSourceId
            WHERE s.Id IN (@Reserve,@Quota) AND e.LedgerKind IN (N'RIGHT_A',N'RIGHT_B')
            """;
        var args = new { Reserve = reserve.Outcome.SourceId, Quota = quota.Outcome.SourceId };
        try
        {
            int changed = db.Execute(patchEffects,
                new { args.Reserve, args.Quota, AQty = 39m, BQty = 31m });
            Assert.Equal(2, changed);
            using var auditor = SqlScratchFixture.WithTenant(factory.CreateClient(), A,
                actor: "audit-corruption", roles: ["auditor"]);
            var report = await Audit(auditor, DateTimeOffset.UtcNow.AddSeconds(1));
            var bucket = Assert.Single(report.StockAllocations,
                x => x.StockLocationId == s.Warehouse && x.SkuId == s.Sku);
            Assert.Equal(70m, bucket.RightGranted);
            Assert.Equal(70m, bucket.EffectGranted); // aggregate comparison alone misses this
            Assert.Equal(2, report.StockRights.Count(r =>
                r.StockLocationId == s.Warehouse && r.SkuId == s.Sku));
            Assert.Equal(2, report.ReconciliationIssues.Count(x =>
                x.StartsWith("STOCK_RIGHT_LINE_EFFECT_MISMATCH:", StringComparison.Ordinal)));
        }
        finally
        {
            // The collection intentionally reuses both physical tenant databases;
            // fault injection must not pollute later audit tests.
            db.Execute(patchEffects,
                new { args.Reserve, args.Quota, AQty = 40m, BQty = 30m });
        }
    }

    [Fact]
    public async Task AsOf_reports_explicit_pending_and_hides_effects_completed_after_cutoff()
    {
        var s = Seed(A);
        using var db = Open(fixture, A);
        string id = NewId();
        Guid pendingId = Guid.NewGuid();
        db.Execute("""
            INSERT dbo.BusinessCommand(Id,TenantId,ProducerId,CommandId,
                PayloadHashVersion,PayloadHash,ActorId,Status,SubmittedAtUtc)
            VALUES(@Id,@Tenant,@Producer,@Command,1,@Hash,N'pending-writer',N'PENDING',
                DATEADD(minute,-1,SYSUTCDATETIME()))
            """, new { Id = pendingId, Tenant = A, Producer, Command = id, Hash = new byte[32] });
        using var factory = fixture.CreateFactory();
        using var writer = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        using var auditor = SqlScratchFixture.WithTenant(factory.CreateClient(), A,
            actor: "synthetic-auditor", roles: ["auditor"]);
        var request = Quota(s, 10);
        try
        {
            var applied = await Send(writer, "grant-b-quota", request);
            Assert.Equal("APPLIED", applied.Outcome.Status);
            var completed = db.ExecuteScalar<DateTime>(
                "SELECT CompletedAtUtc FROM dbo.BusinessCommand WHERE CommandId=@Command",
                new { Command = request.CommandId });
            try
            {
                // Inject a synthetic later completion to validate the historical
                // logical cutoff without changing public command APIs.
                db.Execute("""
                    UPDATE dbo.BusinessCommand
                    SET CompletedAtUtc=DATEADD(day,1,SYSUTCDATETIME())
                    WHERE CommandId=@Command
                    """, new { Command = request.CommandId });
                var report = await Audit(auditor, DateTimeOffset.UtcNow);
                Assert.Contains(report.Pending, x => x.Id == pendingId && x.Status == "PENDING");
                Assert.Contains(report.Pending, x =>
                    x.CommandId == request.CommandId && x.Status == "PENDING_AS_OF");
                Assert.DoesNotContain(report.Sources, x => x.Id == applied.Outcome.SourceId);
                Assert.DoesNotContain(report.Effects, x => x.SourceId == applied.Outcome.SourceId);
            }
            finally
            {
                db.Execute("""
                    UPDATE dbo.BusinessCommand SET CompletedAtUtc=@Completed
                    WHERE CommandId=@Command
                    """, new { Command = request.CommandId, Completed = completed });
            }
        }
        finally
        {
            db.Execute("DELETE dbo.BusinessCommand WHERE Id=@Id", new { Id = pendingId });
        }
    }

    [Fact]
    public async Task Synthetic_role_matrix_enforces_least_privilege_and_rejects_unbound_actors()
    {
        var s = Seed(A);
        using var factory = fixture.CreateFactory();
        using var noRole = SqlScratchFixture.WithTenant(factory.CreateClient(), A,
            actor: "no-role", roles: []);
        foreach (var (route, command) in new (string, object)[]
        {
            ("reserve-a-stock", Reserve(s)), ("confirm-a-order", Confirm(s)),
            ("grant-b-quota", Quota(s)), ("grant-b-credit", Credit(s, 10))
        })
            Assert.Equal(HttpStatusCode.Forbidden,
                (await noRole.PostAsJsonAsync(Root + route, command,
                    TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await noRole.GetAsync(AuditUrl(DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken)).StatusCode);

        using var sales = SqlScratchFixture.WithTenant(factory.CreateClient(), A,
            actor: "sales-writer", roles: ["sales"]);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await sales.PostAsJsonAsync(Root + "grant-b-quota", Quota(s),
                TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(sales, "confirm-a-order", Confirm(s))).Code);

        using var warehouse = SqlScratchFixture.WithTenant(factory.CreateClient(), A,
            actor: "warehouse-writer", roles: ["warehouse"]);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await warehouse.PostAsJsonAsync(Root + "confirm-a-order", Confirm(s),
                TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await warehouse.GetAsync(AuditUrl(DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await Send(warehouse, "grant-b-quota", Quota(s, 10))).Code);

        using var auditor = SqlScratchFixture.WithTenant(factory.CreateClient(), A,
            actor: "read-only", roles: ["auditor"]);
        Assert.Equal(HttpStatusCode.OK,
            (await auditor.GetAsync(AuditUrl(DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await auditor.PostAsJsonAsync(Root + "grant-b-credit", Credit(s, 5),
                TestContext.Current.CancellationToken)).StatusCode);

        using var forgedActor = factory.CreateClient();
        forgedActor.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", SqlScratchFixture.IssueSyntheticToken(A, subject: "other-subject"));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await forgedActor.GetAsync(AuditUrl(DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken)).StatusCode);
        using var db = Open(fixture, A);
        Assert.Equal(0, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.BusinessCommand WHERE ActorId=N'no-role'"));
    }

    [Fact]
    public async Task Audit_cutoff_requires_explicit_timezone_and_normalizes_offsets()
    {
        using var factory = fixture.CreateFactory();
        using var auditor = SqlScratchFixture.WithTenant(factory.CreateClient(), A,
            actor: "cutoff-auditor", roles: ["auditor"]);
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-2).ToOffset(TimeSpan.FromHours(7));
        var withoutOffset = cutoff.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        using var missingZone = await auditor.GetAsync(
            AuditRoot + "?asOf=" + Uri.EscapeDataString(withoutOffset),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, missingZone.StatusCode);
        using var missing = await auditor.GetAsync(
            AuditRoot, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        string explicitOffset = Uri.EscapeDataString(cutoff.ToString("O", CultureInfo.InvariantCulture));
        using var repeated = await auditor.GetAsync(
            AuditRoot + "?asOf=" + explicitOffset + "&asOf=" + explicitOffset,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, repeated.StatusCode);
        using var valid = await auditor.GetAsync(
            AuditRoot + "?asOf=" + explicitOffset, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        var report = await valid.Content.ReadFromJsonAsync<App01aAuditSnapshot>(
            TestContext.Current.CancellationToken);
        Assert.Equal(cutoff.ToUniversalTime(), Assert.IsType<App01aAuditSnapshot>(report).AsOfUtc);
    }

    [Fact]
    public async Task Audit_isolation_ignores_untrusted_tenant_headers_and_fails_closed_for_misroutes()
    {
        var sa = Seed(A);
        var sb = Seed(B);
        using var factory = fixture.CreateFactory();
        using var a = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        using var b = SqlScratchFixture.WithTenant(factory.CreateClient(), B);
        var aOutcome = await Send(a, "grant-b-quota", Quota(sa, 15));
        var bOutcome = await Send(b, "grant-b-quota", Quota(sb, 25));
        Assert.Equal("APPLIED", aOutcome.Outcome.Status);
        Assert.Equal("APPLIED", bOutcome.Outcome.Status);

        using var auditA = SqlScratchFixture.WithTenant(factory.CreateClient(), A,
            actor: "audit-a", roles: ["auditor"]);
        using var auditB = SqlScratchFixture.WithTenant(factory.CreateClient(), B,
            actor: "audit-b", roles: ["auditor"]);
        auditB.DefaultRequestHeaders.Add("X-Tenant-Id", A);
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(1);
        var aSnapshot = await Audit(auditA, cutoff);
        var bSnapshot = await Audit(auditB, cutoff);
        Assert.Contains(aSnapshot.Sources, x => x.Id == aOutcome.Outcome.SourceId);
        Assert.DoesNotContain(aSnapshot.Sources, x => x.Id == bOutcome.Outcome.SourceId);
        Assert.Contains(bSnapshot.Sources, x => x.Id == bOutcome.Outcome.SourceId);
        Assert.DoesNotContain(bSnapshot.Sources, x => x.Id == aOutcome.Outcome.SourceId);

        using var unknown = SqlScratchFixture.WithTenant(factory.CreateClient(), "tenant-unknown",
            actor: "unknown-auditor", roles: ["auditor"]);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await unknown.GetAsync(AuditUrl(cutoff), TestContext.Current.CancellationToken)).StatusCode);
        using var misroutedFactory = fixture.CreateFactory(tenantAOverride: fixture.TenantBConnectionString);
        using var misrouted = SqlScratchFixture.WithTenant(misroutedFactory.CreateClient(), A,
            actor: "audit-a", roles: ["auditor"]);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await misrouted.GetAsync(AuditUrl(cutoff),
                TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Production_or_disabled_host_does_not_register_synthetic_write_or_audit_routes()
    {
        var s = Seed(A);
        using var factory = fixture.CreateFactory(syntheticEnabled: false, environment: "Production");
        using var client = SqlScratchFixture.WithTenant(factory.CreateClient(), A);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync(AuditUrl(DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync(Root + "grant-b-quota", Quota(s),
                TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync("/health/live", TestContext.Current.CancellationToken)).StatusCode);
        using var db = Open(fixture, A);
        Assert.Equal(0, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.BusinessSource WHERE TenantId=@Tenant AND TripId=@Trip",
            new { Tenant = A, s.Trip }));
    }

    [Fact]
    public async Task Four_independent_A_B_clients_cannot_overallocate_or_duplicate_right_effects()
    {
        var s = Seed(B, onHand: 100, orderQuantity: 100);
        using var factory = fixture.CreateFactory();
        using var c1 = SqlScratchFixture.WithTenant(factory.CreateClient(), B);
        using var c2 = SqlScratchFixture.WithTenant(factory.CreateClient(), B);
        using var c3 = SqlScratchFixture.WithTenant(factory.CreateClient(), B);
        using var c4 = SqlScratchFixture.WithTenant(factory.CreateClient(), B);
        var replies = await Task.WhenAll(
            Send(c1, "reserve-a-stock", Reserve(s, 60)),
            Send(c2, "grant-b-quota", Quota(s, 60)),
            Send(c3, "reserve-a-stock", Reserve(s, 60)),
            Send(c4, "grant-b-quota", Quota(s, 60)));
        Assert.Single(replies, r => r.Outcome.Status == "APPLIED");
        Assert.All(replies, r => Assert.True(r.Outcome.Status is "APPLIED" or "REJECTED"));
        using var db = Open(fixture, B);
        Assert.Equal(60m, db.ExecuteScalar<decimal>(
            "SELECT AllocatedBaseQuantity FROM dbo.StockPool WHERE StockLocationId=@Warehouse AND SkuId=@Sku",
            new { s.Warehouse, s.Sku }));
        Assert.Equal(1, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.StockRight WHERE StockLocationId=@Warehouse AND SkuId=@Sku",
            new { s.Warehouse, s.Sku }));
        Assert.Equal(1, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.LedgerEffect WHERE StockLocationId=@Warehouse AND SkuId=@Sku",
            new { s.Warehouse, s.Sku }));
    }

    [Fact]
    public async Task Mixed_payload_race_same_command_identity_has_one_durable_winner()
    {
        var s = Seed(B);
        var first = Quota(s, 10);
        var changed = first with { BaseQuantity = 11 };
        using var factory = fixture.CreateFactory();
        using var c1 = SqlScratchFixture.WithTenant(factory.CreateClient(), B);
        using var c2 = SqlScratchFixture.WithTenant(factory.CreateClient(), B);
        using var c3 = SqlScratchFixture.WithTenant(factory.CreateClient(), B);
        using var c4 = SqlScratchFixture.WithTenant(factory.CreateClient(), B);
        var replies = await Task.WhenAll(
            Send(c1, "grant-b-quota", first),
            Send(c2, "grant-b-quota", changed),
            Send(c3, "grant-b-quota", changed),
            Send(c4, "grant-b-quota", first));
        Assert.Single(replies, r => r.Outcome.Status == "APPLIED" && !r.Outcome.Replayed);
        Assert.Single(replies, r => r.Outcome.Status == "APPLIED" && r.Outcome.Replayed);
        Assert.Equal(2, replies.Count(r => r.Code == HttpStatusCode.Conflict));
        using var db = Open(fixture, B);
        Assert.Equal(1, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.BusinessCommand WHERE TenantId=@Tenant AND CommandId=@Command",
            new { Tenant = B, Command = first.CommandId }));
        Assert.Equal(1, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.StockRight WHERE StockLocationId=@Warehouse AND SkuId=@Sku",
            new { s.Warehouse, s.Sku }));
        Assert.InRange(db.ExecuteScalar<decimal>(
            "SELECT AllocatedBaseQuantity FROM dbo.StockPool WHERE StockLocationId=@Warehouse AND SkuId=@Sku",
            new { s.Warehouse, s.Sku }), 10m, 11m);
    }

    [Fact]
    public async Task Concurrent_checked_credit_grants_cannot_exceed_one_policy_pool()
    {
        var s = Seed(B, creditLimit: 100);
        using var factory = fixture.CreateFactory();
        using var a = SqlScratchFixture.WithTenant(factory.CreateClient(), B);
        using var b = SqlScratchFixture.WithTenant(factory.CreateClient(), B);
        var replies = await Task.WhenAll(
            Send(a, "grant-b-credit", Credit(s, 70)),
            Send(b, "grant-b-credit", Credit(s, 70)));
        Assert.Single(replies, x => x.Outcome.OutcomeCode == "B_CREDIT_GRANTED");
        Assert.Single(replies, x => x.Outcome.ReasonCode == "CREDIT_LIMIT_INSUFFICIENT");
        using var db = Open(fixture, B);
        Assert.Equal(70m, db.ExecuteScalar<decimal>(
            "SELECT GrantedAmount FROM dbo.BCreditPool WHERE TripId=@Trip", new { s.Trip }));
        Assert.Equal(1, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.BCreditGrant WHERE TripId=@Trip AND ApprovalId IS NOT NULL",
            new { s.Trip }));
    }

    [Fact]
    public async Task SQL_effect_fault_rolls_back_pool_source_credit_rights_and_can_retry_same_key()
    {
        var s = Seed(B);
        var quota = Quota(s, 12);
        var credit = Credit(s, 20);
        using var db = Open(fixture, B);
        using var factory = fixture.CreateFactory();
        using var writer = SqlScratchFixture.WithTenant(factory.CreateClient(), B);
        db.Execute("""
            CREATE TRIGGER dbo.APP01A3_TestRollback ON dbo.LedgerEffect AFTER INSERT AS
            BEGIN
                THROW 51003, 'APP01A3 synthetic ledger fault', 1;
            END;
            """);
        try
        {
            await AssertInjectedFault(writer, "grant-b-quota", quota);
            await AssertInjectedFault(writer, "grant-b-credit", credit);
        }
        finally
        {
            db.Execute("DROP TRIGGER dbo.APP01A3_TestRollback");
        }
        Assert.Equal(0m, db.ExecuteScalar<decimal>(
            "SELECT AllocatedBaseQuantity FROM dbo.StockPool WHERE StockLocationId=@Warehouse AND SkuId=@Sku",
            new { s.Warehouse, s.Sku }));
        Assert.Equal(0m, db.ExecuteScalar<decimal>(
            "SELECT GrantedAmount FROM dbo.BCreditPool WHERE TripId=@Trip", new { s.Trip }));
        Assert.Equal(0, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.BusinessCommand WHERE TenantId=@Tenant AND CommandId IN (@Quota,@Credit)",
            new { Tenant = B, Quota = quota.CommandId, Credit = credit.CommandId }));
        Assert.Equal(0, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.BusinessSource WHERE TenantId=@Tenant AND TripId=@Trip",
            new { Tenant = B, s.Trip }));
        Assert.Equal(0, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.StockRight WHERE TenantId=@Tenant AND TripId=@Trip",
            new { Tenant = B, s.Trip }));
        Assert.Equal(0, db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.BCreditGrant WHERE TenantId=@Tenant AND TripId=@Trip",
            new { Tenant = B, s.Trip }));
        Assert.Equal("APPLIED", (await Send(writer, "grant-b-quota", quota)).Outcome.Status);
        Assert.Equal("APPLIED", (await Send(writer, "grant-b-credit", credit)).Outcome.Status);
    }
}
