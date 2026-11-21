using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Xunit;

namespace FmcgDms.Api.Tests;

public sealed partial class App01e2CrossCommandRaceTests
{
    private sealed record LocalPaymentTotals(decimal CashReceived, decimal CreditApplied, decimal CreditIssued,
        int PendingLinked, int ReceiptCount, decimal ArOutstanding, decimal CreditIssuedLedger,
        decimal CreditAppliedLedger);

    [Theory]
    [InlineData("tenant-a", "CASH_60_CREDIT_20")]
    [InlineData("tenant-a", "BANK_PENDING")]
    [InlineData("tenant-a", "CASH_OVERPAY_120")]
    [InlineData("tenant-b", "CASH_60_CREDIT_20")]
    [InlineData("tenant-b", "BANK_PENDING")]
    [InlineData("tenant-b", "CASH_OVERPAY_120")]
    public async Task AC09_additional_payment_matches_oracle(string tenant, string caseId)
    {
        byte[] oracleBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "additional-oracles.json"));
        using var oracle = JsonDocument.Parse(oracleBytes);
        var spec = oracle.RootElement.GetProperty("AC-09").GetProperty("cases").EnumerateArray()
            .Single(s => s.GetProperty("caseId").GetString() == caseId);
        decimal Read(string key) => spec.GetProperty(key).GetDecimal();
        decimal Optional(string key, decimal fallback) =>
            spec.TryGetProperty(key, out var value) ? value.GetDecimal() : fallback;
        decimal cashApplied = Read("verifiedReceiptApplied");
        decimal cashReceived = Optional("actualCashReceived", cashApplied);
        decimal expectedCredit = Read("reservedCustomerCreditApplied");
        decimal issued = Optional("expectedNewCustomerCredit", 0m);
        bool bankPending = caseId == "BANK_PENDING";
        const string route = C + "post-b-sale-delivery-receipt";
        string peer = tenant == "tenant-a" ? "tenant-b" : "tenant-a";
        var peerBefore = await FullSql(peer);
        using var factory = fixture.CreateFactory();
        using var setup = SqlScratchFixture.WithTenant(factory.CreateClient(), tenant);
        Scene scene = Seed(tenant);
        await Prepare(setup, tenant, scene, load: true);
        using var sales = Client(factory, tenant, SalesActor, "sales");

        PostBSaleDeliveryReceiptCommand Command(decimal cash, string evidence, string? bank = null, Guid? hold = null) =>
            new("ac09-local-payment", Id(), Id(), scene.Trip, scene.Customer, scene.ShipTo,
                scene.BManifestLine, scene.Sku, 10m, Read("P") / 10m, cash, evidence, bank, hold);

        Guid? reservation = null;
        Guid? fundingSource = null;
        if (expectedCredit > 0)
        {
            // Create proven credit via a separate confirmed cash overpayment for this customer.
            var funding = await Send(sales, route, Command(120m, CashWitness(tenant, scene, 120m)));
            Assert.Equal("B_SALE_DELIVERY_POSTED", funding.Outcome.OutcomeCode);
            fundingSource = funding.Outcome.SourceId;
            reservation = Guid.NewGuid();
            using var db = Open(tenant);
            Assert.Equal(1, db.Execute("""
                INSERT dbo.BLocalCreditReservation(Id,TenantId,CreditSaleId,CustomerId,TripId,ShipToSnapshotId,Amount)
                SELECT @Id,d.TenantId,d.Id,d.CustomerId,d.TripId,d.ShipToSnapshotId,@Amount
                FROM dbo.SaleDelivery d JOIN dbo.BLocalSalePayment p ON p.TenantId=d.TenantId AND p.SaleDeliveryId=d.Id
                WHERE d.TenantId=@Tenant AND d.BusinessSourceId=@Source AND p.CreditIssued=@Amount
                """, new { Id = reservation, Tenant = tenant, Source = funding.Outcome.SourceId, Amount = expectedCredit }));
        }

        string? bank = bankPending ? Id() : null;
        if (bank is not null)
        {
            using var db = Open(tenant);
            db.Execute("""
                INSERT dbo.BLocalPendingBankEvidence(TenantId,EvidenceRef,TripId,CustomerId,ShipToSnapshotId,ReportedAmount)
                VALUES(@Tenant,@Evidence,@Trip,@Customer,@ShipTo,@Amount)
                """, new { Tenant = tenant, Evidence = bank, scene.Trip, scene.Customer, scene.ShipTo, Amount = Read("P") });
        }

        var before = ReadSql(tenant, scene);
        var opening = ReadAc09Grant(tenant, scene);
        var command = Command(cashReceived, bankPending ? "" : CashWitness(tenant, scene, cashReceived), bank, reservation);
        var reply = await Send(sales, route, command);
        Assert.Equal("B_SALE_DELIVERY_POSTED", reply.Outcome.OutcomeCode);
        int expectedEffects = bankPending ? 5 : expectedCredit > 0 ? 9 : 7;
        var audit = Audit(tenant, command);
        AssertCommandFromSql(tenant, scene,
            Req(route, command, "sales", SalesActor, "B_SALE_DELIVERY", 1, expectedEffects), reply, audit);

        var after = ReadSql(tenant, scene);
        var grant = ReadAc09Grant(tenant, scene);
        using var connection = Open(tenant);
        var observed = connection.QuerySingle<LocalPaymentTotals>("""
            SELECT COALESCE(r.ReceivedAmount,0) AS CashReceived,p.CreditApplied,p.CreditIssued,
                CASE WHEN p.PendingBankEvidenceRef IS NULL THEN 0 ELSE 1 END AS PendingLinked,
                CASE WHEN r.Id IS NULL THEN 0 ELSE 1 END AS ReceiptCount,i.OutstandingAmount AS ArOutstanding,
                (SELECT COALESCE(SUM(e.AmountDelta),0) FROM dbo.LedgerEffect e WHERE e.TenantId=d.TenantId
                    AND e.BusinessSourceLineId=d.BusinessSourceLineId AND e.EffectKey=N'CUSTOMER-CREDIT-ISSUED') AS CreditIssuedLedger,
                (SELECT COALESCE(SUM(e.AmountDelta),0) FROM dbo.LedgerEffect e WHERE e.TenantId=d.TenantId
                    AND e.BusinessSourceLineId=d.BusinessSourceLineId AND e.EffectKey=N'CUSTOMER-CREDIT-USED') AS CreditAppliedLedger
            FROM dbo.SaleDelivery d
            JOIN dbo.BLocalSalePayment p ON p.TenantId=d.TenantId AND p.SaleDeliveryId=d.Id
            JOIN dbo.ReceivableOpenItem i ON i.TenantId=d.TenantId AND i.SaleDeliveryId=d.Id
            LEFT JOIN dbo.CashReceipt r ON r.TenantId=d.TenantId AND r.SaleDeliveryId=d.Id
            WHERE d.TenantId=@Tenant AND d.BusinessSourceId=@Source
            """, new { Tenant = tenant, Source = reply.Outcome.SourceId });
        decimal expectedAr = Read("expectedAR");
        decimal expectedRemaining = Read("expectedGrantRemaining");
        Cell[] cells =
        [
            new("OpeningGrant", Read("openingGrant"), opening.Granted),
            new("GrantConsumed", expectedAr, grant.Recorded - opening.Recorded),
            new("GrantRemaining", expectedRemaining, grant.Granted - grant.Consumed - grant.Released),
            new("AccountsReceivable", expectedAr, observed.ArOutstanding),
            new("AccountsReceivableLedgerDelta", expectedAr, after.Numbers.ArB - before.Numbers.ArB),
            new("CashCustodyDelta", Read("expectedCashCustodyDelta"), after.Numbers.Custody - before.Numbers.Custody),
            new("CashReceived", cashReceived, observed.CashReceived),
            new("ReceiptAppliedDelta", cashApplied, after.Numbers.ReceiptApplied - before.Numbers.ReceiptApplied),
            new("CustomerCreditApplied", expectedCredit, observed.CreditApplied),
            new("CustomerCreditIssued", issued, observed.CreditIssued),
            new("CustomerCreditIssuedLedger", issued, observed.CreditIssuedLedger),
            new("CustomerCreditAppliedLedger", -expectedCredit, observed.CreditAppliedLedger),
            new("PendingLinked", bankPending ? 1 : 0, observed.PendingLinked),
            new("ReceiptCount", bankPending ? 0 : 1, observed.ReceiptCount),
            new("FundDelta", 0, after.Numbers.Fund - before.Numbers.Fund)
        ];
        Assert.All(cells, c => Assert.True(c.Match, $"{c.Field}: {c.Expected} != {c.Actual}"));
        var applied = await FullSql(tenant);
        var replay = await Send(sales, route, command);
        Assert.Equal(reply.Outcome with { Replayed = true }, replay.Outcome);
        var afterReplay = await FullSql(tenant);
        Assert.Equal(applied, afterReplay);
        var peerAfter = await FullSql(peer);
        Assert.Equal(peerBefore, peerAfter);

        var reused = command with { CommandId = Id(), SourceNumber = Id(),
            VerificationEvidenceRef = reservation.HasValue
                ? CashWitness(tenant, scene, cashReceived) : command.VerificationEvidenceRef };
        var rejected = await Send(sales, route, reused);
        string reusedReason = bankPending ? "BANK_PENDING_EVIDENCE_ALREADY_USED" :
            reservation.HasValue ? "CUSTOMER_CREDIT_RESERVATION_INVALID" : "CASH_EVIDENCE_ALREADY_USED";
        Assert.Equal(reusedReason, rejected.Outcome.ReasonCode);
        AssertCommandFromSql(tenant, scene,
            Req(route, reused, "sales", SalesActor, "B_SALE_DELIVERY", 0, 0), rejected, Audit(tenant, reused));

        using var auditor = Client(factory, tenant, "ac09-audit", "auditor");
        var asOf = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddSeconds(2).ToString("O"));
        var ledgerAudit = await auditor.GetFromJsonAsync<App01aAuditSnapshot>(
            "/api/synthetic/app01a/audit/as-of?asOf=" + asOf, TestContext.Current.CancellationToken);
        Assert.NotNull(ledgerAudit);
        var auditedSources = ledgerAudit.Sources.Where(s => s.Id == reply.Outcome.SourceId || s.Id == fundingSource).ToArray();
        Assert.Equal(fundingSource is null ? 1 : 2, auditedSources.Length);
        var auditedDeliveries = connection.Query<Guid>("""
            SELECT Id FROM dbo.SaleDelivery WHERE TenantId=@Tenant AND BusinessSourceId IN @Sources;
            """, new { Tenant = tenant, Sources = auditedSources.Select(s => s.Id).ToArray() }).ToArray();
        Assert.Equal(auditedSources.Length, auditedDeliveries.Length);
        Assert.All(auditedDeliveries, id => Assert.DoesNotContain(
            $"B_POSTING_SOURCE_LEDGER_MISMATCH:{id:N}", ledgerAudit.ReconciliationIssues));
        var report = new
        {
            Case = "AC-09", CaseId = caseId, Tenant = tenant, Database = after.Database,
            Environment = "SYNTHETIC_NONPRODUCTION", PhysicalPreprod = "NOT_VERIFIED",
            EngineVersion = ReadAc09EngineVersion(tenant), CapturedAtUtc = DateTimeOffset.UtcNow,
            TestRunId = Environment.GetEnvironmentVariable("AC09_TEST_RUN_ID"),
            Source = Environment.GetEnvironmentVariable("AC09_SOURCE"),
            OracleSha256 = Convert.ToHexString(SHA256.HashData(oracleBytes)),
            Command = command, reply.Outcome, Audit = audit, Cells = cells,
            Replay = new { replay.Outcome, Before = applied, After = afterReplay },
            Peer = new { Before = peerBefore, After = peerAfter }, ReusedWitnessRejection = reusedReason,
            AuditedDeliveryIds = auditedDeliveries
        };
        var directory = Path.Combine(AppContext.BaseDirectory, "TestResults", "AC09");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{tenant}-{caseId}.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
