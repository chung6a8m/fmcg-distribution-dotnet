using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class DevF08Evidence
{
    internal const string Maker = "f08-maker";
    internal const string Checker = "f08-checker";
    internal const string Cashier = "f08-cashier";
    internal const string Witness = "f08-witness";
    private const long OpeningTreasury = 100000;
    private static readonly string[] Phases = ["delivered", "quarantined", "settled", "credited", "proposed", "approved", "disbursed"];
    internal sealed record State(decimal Ar, long Treasury, decimal Qa, int Credits, long CreditAvailable,
        int Proposals, int Approvals, int Payments, int Settlements, int PaymentWitnesses, int PayoutWitnesses);
    internal sealed record Observation(string Phase, State Actual, string SourceSha256);
    private sealed record Pricing(string Sku, long Base, long Discount, long Net, long Tax, long Gross);
    private sealed record Credit(Guid Id, Guid CaseId, Guid DeliveryId, decimal Quantity, long Net, long Tax, long Gross,
        long Discount, string Actor, string Evidence, string Reason, string Lot, string ReleaseActor);
    private sealed record Ledger(Guid SourceId, string Kind, string Key, string LedgerKind, decimal Quantity, decimal Amount);
    private sealed record Refund(Guid ProposalId, Guid CreditId, long Proposed, string Maker, string ProposalEvidence,
        Guid ApprovalId, string Checker, string ApprovalEvidence, Guid DisbursementId, string Cashier, long Disbursed,
        string PaymentEvidence);

    internal static string CheckpointDirectory(string root, DemoRun run) =>
        Path.Combine(root, "TestResults", "dev-f08", run.Id, "checkpoints");

    internal static async Task Seed(string connection, string tenant, Guid quarantine, CancellationToken ct)
    {
        await using var db = new SqlConnection(connection);
        await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(ct);
        await db.ExecuteAsync(new CommandDefinition("""
            DECLARE @customer uniqueidentifier=NEWID(), @p uniqueidentifier=NEWID(), @q uniqueidentifier=NEWID(),
                @plan uniqueidentifier=NEWID(), @quoteP uniqueidentifier=NEWID(), @quoteQ uniqueidentifier=NEWID(),
                @lotP uniqueidentifier=NEWID(), @lotQ uniqueidentifier=NEWID(), @main uniqueidentifier,@truck uniqueidentifier;
            SELECT @main=Id FROM dbo.StockLocation WHERE TenantId=@Tenant AND Code=N'C2-MAIN';
            SELECT @truck=Id FROM dbo.StockLocation WHERE TenantId=@Tenant AND Code=N'C2-TRUCK';
            INSERT dbo.App02ActorRoleGrant(TenantId,ActorId,RoleCode,IsActive,ChangedBy)
                VALUES(@Tenant,@Maker,'finance',1,N'f08-dev-launcher'),(@Tenant,@Checker,'finance',1,N'f08-dev-launcher'),
                      (@Tenant,@Cashier,'cashier',1,N'f08-dev-launcher'),(@Tenant,@Witness,'case_checker',1,N'f08-dev-launcher');
            INSERT dbo.F08Scope(TenantId,ActorId,RoleCode)
                VALUES(@Tenant,@Maker,'finance'),(@Tenant,@Checker,'finance'),(@Tenant,@Cashier,'cashier'),(@Tenant,@Witness,'case_checker');
            INSERT dbo.F08Treasury(TenantId,OpeningAmount,AvailableAmount) VALUES(@Tenant,@Treasury,@Treasury);
            INSERT dbo.Customer(Id,TenantId,Code,Name)
                VALUES(@customer,@Tenant,N'F08-SOURCE',N'Khách đối soát và hoàn tiền DEV');
            INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom)
                VALUES(@p,@Tenant,N'P',N'Sản phẩm P DEV',N'EA'),(@q,@Tenant,N'Q',N'Sản phẩm Q DEV',N'EA');
            INSERT dbo.StockPool(TenantId,StockLocationId,SkuId,OnHandBaseQuantity)
                VALUES(@Tenant,@main,@p,3),(@Tenant,@main,@q,1);
            INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,SnapshotCode,BaseQuantity,CostAmount)
                VALUES(@Tenant,@main,@p,N'F08-v1',3,3000),(@Tenant,@main,@q,N'F08-v1',1,1000),
                    (@Tenant,@truck,@p,N'F08-v1',0,0),(@Tenant,@truck,@q,N'F08-v1',0,0),(@Tenant,@Quarantine,@p,N'F08-v1',0,0);
            INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,OnHandBaseQuantity,OnHandCostAmount)
                VALUES(@Tenant,@main,@p,3,3000),(@Tenant,@main,@q,1,1000),
                    (@Tenant,@truck,@p,0,0),(@Tenant,@truck,@q,0,0),(@Tenant,@Quarantine,@p,0,0);
            INSERT dbo.F03Unit(TenantId,SkuId,UomCode,Factor,[Step]) VALUES(@Tenant,@p,N'EA',1,1),(@Tenant,@q,N'EA',1,1);
            INSERT dbo.F03Lot(TenantId,Id,SkuId,Code,ExpiresOn)
                VALUES(@Tenant,@lotP,@p,N'F08-P-ORIGIN','20271231'),(@Tenant,@lotQ,@q,N'F08-Q-ORIGIN','20271231');
            INSERT dbo.F03LotOpening(TenantId,LotId,LocationId,Quantity) VALUES(@Tenant,@lotP,@main,3),(@Tenant,@lotQ,@main,1);
            INSERT dbo.C2CommercialPolicy(TenantId,Id,CustomerId,SkuId,Version,Decision,EffectiveFromUtc,EffectiveUntilUtc,
                Maker,Checker,ApprovedAtUtc,EvidenceRef,UnitPrice,DiscountPerUnit,TaxRate,TermsDays,CreditCap,SellerTimeZone)
                VALUES(@Tenant,@quoteP,@customer,@p,1,'APPROVED',@From,@Until,N'f08-quote-maker',N'f08-quote-checker',
                        @From,N'F08-quote-P-v1',66666.6667,0,0.08,7,400000,'Asia/Ho_Chi_Minh'),
                      (@Tenant,@quoteQ,@customer,@q,1,'APPROVED',@From,@Until,N'f08-quote-maker',N'f08-quote-checker',
                        @From,N'F08-quote-Q-v1',100000,0,0.10,7,400000,'Asia/Ho_Chi_Minh');
            INSERT dbo.F04PricePlan(TenantId,Id,CustomerId,Version,TaxMode,OrderDiscount,
                EffectiveFromUtc,EffectiveUntilUtc,Maker,Checker,ApprovedAtUtc,EvidenceRef)
                VALUES(@Tenant,@plan,@customer,1,'EXCLUSIVE',10001,@From,@Until,N'f08-quote-maker',N'f08-quote-checker',@From,N'F08-plan-v1');
            INSERT dbo.F04PlanQuote(TenantId,PlanId,PolicyId) VALUES(@Tenant,@plan,@quoteP),(@Tenant,@plan,@quoteQ);
            """, new { Tenant = tenant, Quarantine = quarantine, Maker, Checker, Cashier, Witness, Treasury = OpeningTreasury,
                From = DevF05Evidence.FixtureUtc.AddHours(-1), Until = DevF05Evidence.FixtureUtc.AddDays(1) }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    internal static async Task<Observation[]> ServeCheckpoints(DemoRun run, string root, Process browser, CancellationToken ct)
    {
        string dir = CheckpointDirectory(root, run);
        Directory.CreateDirectory(dir);
        var observed = new List<Observation>();
        foreach (string phase in Phases)
        {
            string request = Path.Combine(dir, phase + ".request");
            while (!File.Exists(request))
            {
                if (browser.HasExited) return [.. observed];
                await Task.Delay(75, ct);
            }
            if (await File.ReadAllTextAsync(request, ct) != run.Id)
                throw new InvalidDataException("DEV-F08 checkpoint has the wrong run ID.");
            var snapshot = await Read(run, "tenant-a", phase, ct);
            observed.Add(snapshot);
            await File.WriteAllTextAsync(Path.Combine(dir, phase + ".json"), JsonSerializer.Serialize(snapshot), ct);
            string ready = Path.Combine(dir, phase + ".ready");
            await File.WriteAllTextAsync(ready + ".partial", run.Id, ct);
            File.Move(ready + ".partial", ready);
        }
        return [.. observed];
    }

    private static async Task<Observation> Read(DemoRun run, string tenant, string phase, CancellationToken ct)
    {
        await using var db = new SqlConnection(run.Connection(tenant));
        using var rows = await db.QueryMultipleAsync(new CommandDefinition("""
            SELECT COALESCE((SELECT SUM(OutstandingAmount) FROM dbo.ReceivableOpenItem),0) AS Ar,
                (SELECT AvailableAmount FROM dbo.F08Treasury WHERE TenantId=@Tenant) AS Treasury,
                (SELECT b.OnHandBaseQuantity FROM dbo.PhysicalStockBalance b
                    JOIN dbo.Sku k ON k.TenantId=b.TenantId AND k.Id=b.SkuId AND k.Code='P'
                    JOIN dbo.StockLocation l ON l.TenantId=b.TenantId AND l.Id=b.StockLocationId AND l.Code='F05-QA') AS Qa,
                (SELECT COUNT(*) FROM dbo.F08ReturnCredit) AS Credits,
                (SELECT COALESCE(SUM(GrossAmount),0) FROM dbo.F08ReturnCredit)
                    -(SELECT COALESCE(SUM(Amount),0) FROM dbo.F08Disbursement) AS CreditAvailable,
                (SELECT COUNT(*) FROM dbo.F08Refund) AS Proposals,
                (SELECT COUNT(*) FROM dbo.F08RefundApproval) AS Approvals,
                (SELECT COUNT(*) FROM dbo.F08Disbursement) AS Payments,
                (SELECT COUNT(*) FROM dbo.F08Settlement) AS Settlements,
                (SELECT COUNT(*) FROM dbo.F08PaymentWitness) AS PaymentWitnesses,
                (SELECT COUNT(*) FROM dbo.F08PayoutWitness) AS PayoutWitnesses;
            SELECT (SELECT d.* FROM dbo.SaleDelivery d ORDER BY d.Id FOR JSON PATH,INCLUDE_NULL_VALUES) AS Delivery,
                (SELECT p.* FROM dbo.F04DeliveryPricing p ORDER BY p.DeliveryId FOR JSON PATH,INCLUDE_NULL_VALUES) AS Price,
                (SELECT p.* FROM dbo.F04PriceLine p ORDER BY p.OrderLineId FOR JSON PATH,INCLUDE_NULL_VALUES) AS Lines,
                (SELECT e.* FROM dbo.LedgerEffect e JOIN dbo.SaleDelivery d
                    ON d.TenantId=e.TenantId AND d.BusinessSourceLineId=e.BusinessSourceLineId
                    ORDER BY e.Id FOR JSON PATH,INCLUDE_NULL_VALUES) AS Ledger,
                (SELECT i.* FROM dbo.F03LotIssue i ORDER BY i.DeliveryId,i.LotId FOR JSON PATH,INCLUDE_NULL_VALUES) AS Lot
                FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES;
            """, new { Tenant = tenant }, cancellationToken: ct));
        State state = await rows.ReadSingleAsync<State>();
        string contents = string.Concat(await rows.ReadAsync<string>());
        return new(phase, state, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contents))));
    }

    internal static async Task Verify(DemoRun run, string root, string source, string apiSha256,
        Observation[]? snapshots, CancellationToken ct)
    {
        await run.Verify("tenant-a");
        await run.Verify("tenant-b");
        byte[] oracleBytes = await File.ReadAllBytesAsync(Path.Combine(root, "business", "reference-data",
            "expected-results", "golden-oracles.json"), ct);
        using var locked = JsonDocument.Parse(oracleBytes);
        var oracle = locked.RootElement.GetProperty("AC-19");
        var expectedPricing = oracle.GetProperty("EXCLUSIVE").EnumerateArray().Select(row =>
            new Pricing(row[0].GetString()!, row[1].GetInt64(), row[2].GetInt64(), row[3].GetInt64(),
                row[4].GetInt64(), row[5].GetInt64())).ToArray();
        long gross = expectedPricing[0].Gross;
        long credit = oracle.GetProperty("creditBeforeRefund").GetInt64();
        long payment = oracle.GetProperty("refundDisbursed").GetInt64();
        long remaining = oracle.GetProperty("creditAfterRefund").GetInt64();
        (string Phase, State Expected)[] expectations = [
            ("delivered", new(gross, OpeningTreasury, 0, 0, 0, 0, 0, 0, 0, 0, 0)),
            ("quarantined", new(gross, OpeningTreasury, 1, 0, 0, 0, 0, 0, 0, 1, 0)),
            ("settled", new(0, OpeningTreasury, 0, 0, 0, 0, 0, 0, 1, 1, 0)),
            ("credited", new(0, OpeningTreasury, 0, 1, credit, 0, 0, 0, 1, 1, 0)),
            ("proposed", new(0, OpeningTreasury, 0, 1, credit, 1, 0, 0, 1, 1, 0)),
            ("approved", new(0, OpeningTreasury, 0, 1, credit, 1, 1, 0, 1, 1, 1)),
            ("disbursed", new(0, OpeningTreasury-payment, 0, 1, remaining, 1, 1, 1, 1, 1, 1)),
            ("final", new(0, OpeningTreasury-payment, 0, 3, gross-payment, 1, 1, 1, 1, 1, 1)),
            ("peer", new(0, OpeningTreasury, 0, 0, 0, 0, 0, 0, 0, 0, 0))
        ];
        var observed = (snapshots ?? []).ToDictionary(row => row.Phase, StringComparer.Ordinal);
        observed.Add("final", await Read(run, "tenant-a", "final", ct));
        observed.Add("peer", await Read(run, "tenant-b", "peer", ct));
        string? sourceDigest = observed.GetValueOrDefault("delivered")?.SourceSha256;
        var phases = expectations.Select(row =>
        {
            observed.TryGetValue(row.Phase, out var actual);
            return new { row.Phase, row.Expected, Actual = actual, Matches = actual is not null && actual.Actual == row.Expected
                && (row.Phase == "peer" || actual.SourceSha256 == sourceDigest) };
        }).ToArray();

        await using var a = new SqlConnection(run.Connection("tenant-a"));
        await using var b = new SqlConnection(run.Connection("tenant-b"));
        Pricing[] pricing = (await a.QueryAsync<Pricing>(new CommandDefinition("""
            SELECT s.Code AS Sku,f.SourceBase AS [Base],f.AllocatedDiscount AS Discount,f.NetAmount AS Net,
                f.TaxAmount AS Tax,f.GrossAmount AS Gross
            FROM dbo.F04PriceLine f JOIN dbo.AOrderLine o ON o.TenantId=f.TenantId AND o.Id=f.OrderLineId
            JOIN dbo.Sku s ON s.TenantId=o.TenantId AND s.Id=o.SkuId WHERE f.Currency='VND' ORDER BY s.Code;
            """, cancellationToken: ct))).ToArray();
        var sale = await a.QuerySingleAsync<(Guid Id, decimal Quantity, decimal Cost, decimal Gross, bool InvoiceEligible)>(
            new CommandDefinition("""
                SELECT d.Id,d.BaseQuantity AS Quantity,d.CostAmount AS Cost,d.PayableAmount AS Gross,d.InvoiceEligible
                FROM dbo.SaleDelivery d JOIN dbo.F04DeliveryPricing p ON p.TenantId=d.TenantId AND p.DeliveryId=d.Id
                WHERE p.Currency='VND' AND p.GrossAmount=d.PayableAmount;
                """, cancellationToken: ct));
        Credit[] credits = (await a.QueryAsync<Credit>(new CommandDefinition("""
            SELECT x.Id,x.CaseId,x.DeliveryId,x.Quantity,x.NetAmount AS Net,x.TaxAmount AS Tax,x.GrossAmount AS Gross,
                x.DiscountAmount AS Discount,x.ActorId AS Actor,x.Evidence,x.Reason,l.Code AS Lot,r.ActorId AS ReleaseActor
            FROM dbo.F08ReturnCredit x JOIN dbo.F05ReturnCase c ON c.TenantId=x.TenantId AND c.Id=x.CaseId AND c.DeliveryId=x.DeliveryId
            JOIN dbo.F05ReturnStage r ON r.TenantId=c.TenantId AND r.CaseId=c.Id AND r.Stage='RELEASE'
            JOIN dbo.F03Lot l ON l.TenantId=c.TenantId AND l.Id=c.LotId
            WHERE x.Quantity=c.Quantity ORDER BY x.Evidence;
            """, cancellationToken: ct))).ToArray();
        long[][] expectedParts = oracle.GetProperty("returnParts").EnumerateArray()
            .Select(row => row.EnumerateArray().Select(value => value.GetInt64()).ToArray()).ToArray();
        long[] expectedDiscount = [2222, 2223, 2222];
        bool creditsMatch = credits.Length == expectedParts.Length && credits.Select((row, index) =>
            new[] { row.Net, row.Tax, row.Gross }.SequenceEqual(expectedParts[index])
            && row.Discount == expectedDiscount[index] && row.DeliveryId == sale.Id && row.Quantity == 1 && row.Actor == Maker
            && row.Evidence == $"F08-credit-{index + 1}" && row.Reason.Length > 0 && row.Lot == "F08-P-ORIGIN"
            && row.ReleaseActor == DevF05Evidence.Qa).All(match => match) && credits.Sum(row => row.Discount) == expectedPricing[0].Discount;
        Refund refund = await a.QuerySingleAsync<Refund>(new CommandDefinition("""
            SELECT r.Id AS ProposalId,r.CreditId,r.Amount AS Proposed,r.ActorId AS Maker,r.Evidence AS ProposalEvidence,
                a.Id AS ApprovalId,a.ActorId AS Checker,a.Evidence AS ApprovalEvidence,
                p.Id AS DisbursementId,p.ActorId AS Cashier,p.Amount AS Disbursed,p.Evidence AS PaymentEvidence
            FROM dbo.F08Refund r JOIN dbo.F08RefundApproval a ON a.TenantId=r.TenantId AND a.RefundId=r.Id
            JOIN dbo.F08Disbursement p ON p.TenantId=r.TenantId AND p.RefundId=r.Id
            WHERE LEN(r.Reason)>0 AND LEN(a.Reason)>0 AND LEN(p.Reason)>0;
            """, cancellationToken: ct));
        var settlement = await a.QuerySingleAsync<(Guid Id, Guid Delivery, long Amount, string Actor, string Evidence)>(
            new CommandDefinition("SELECT Id,DeliveryId,Amount,ActorId,Evidence FROM dbo.F08Settlement WHERE LEN(Reason)>0;",
                cancellationToken: ct));
        int verifiedSources = await a.QuerySingleAsync<int>(new CommandDefinition("""
            SELECT (SELECT COUNT(*) FROM dbo.F08PaymentWitness w JOIN dbo.F08Settlement p
                       ON p.TenantId=w.TenantId AND p.WitnessId=w.Id AND p.DeliveryId=w.DeliveryId AND p.Amount=w.Amount AND p.Evidence=w.Evidence
                       WHERE w.ActorId=@Witness AND w.ActorId<>p.ActorId AND LEN(w.Reason)>0)
                 + (SELECT COUNT(*) FROM dbo.F08PayoutWitness w JOIN dbo.F08Disbursement p
                       ON p.TenantId=w.TenantId AND p.WitnessId=w.Id AND p.RefundId=w.RefundId AND p.Amount=w.Amount AND p.Evidence=w.Evidence
                       JOIN dbo.F08Refund r ON r.TenantId=w.TenantId AND r.Id=w.RefundId
                       JOIN dbo.F08RefundApproval a ON a.TenantId=w.TenantId AND a.RefundId=w.RefundId
                       WHERE w.ActorId=@Witness AND w.ActorId NOT IN (p.ActorId,r.ActorId,a.ActorId) AND LEN(w.Reason)>0);
            """, new { Witness }, cancellationToken: ct));
        bool sourcesMatch = verifiedSources == 2 && settlement.Delivery == sale.Id && settlement.Amount == gross && settlement.Actor == Maker &&
            settlement.Evidence == "F08-payment-verified" && refund.CreditId == credits.FirstOrDefault()?.Id &&
            refund.Proposed == payment && refund.Disbursed == payment && refund.Maker == Maker && refund.Checker == Checker &&
            refund.Cashier == Cashier && refund.ProposalEvidence == "F08-refund-proposal" &&
            refund.ApprovalEvidence == "F08-refund-approved" && refund.PaymentEvidence == "F08-cashier-proof";

        Ledger[] ledger = (await a.QueryAsync<Ledger>(new CommandDefinition("""
            SELECT s.Id AS SourceId,s.SourceKind AS Kind,e.EffectKey AS [Key],e.LedgerKind,e.QuantityDelta AS Quantity,
                e.AmountDelta AS Amount
            FROM dbo.BusinessSource s JOIN dbo.BusinessSourceLine l ON l.TenantId=s.TenantId AND l.BusinessSourceId=s.Id
            JOIN dbo.LedgerEffect e ON e.TenantId=l.TenantId AND e.BusinessSourceLineId=l.Id
            WHERE s.SourceKind LIKE 'F08[_]%' ORDER BY s.Id,e.EffectKey;
            """, cancellationToken: ct))).ToArray();
        Ledger[] expectedLedger =
        [
            new(settlement.Id, "F08_SETTLE", "F08-PAID-AR", "AR", 0, -gross),
            new(settlement.Id, "F08_SETTLE", "F08-VERIFIED-PAYMENT", "BANK_CUSTOMER_RECEIPT", 0, gross),
            .. credits.Select(row => new Ledger(row.Id, "F08_CREDIT", "F08-CREDIT-ISSUED", "CUSTOMER_CREDIT", 0, row.Gross)),
            new(refund.DisbursementId, "F08_DISBURSE", "F08-CREDIT-PAID", "CUSTOMER_CREDIT", 0, -payment),
            new(refund.DisbursementId, "F08_DISBURSE", "F08-TREASURY-PAID", "REFUND_TREASURY", 0, -payment)
        ];
        bool ledgerMatches = ledger.Length == expectedLedger.Length && expectedLedger.All(ledger.Contains);
        int sourceCount = await a.QuerySingleAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM dbo.BusinessSource s JOIN dbo.BusinessCommand b ON b.TenantId=s.TenantId AND b.Id=s.BusinessCommandId
            WHERE s.SourceKind LIKE 'F08[_]%' AND b.Status='APPLIED';
            """, cancellationToken: ct));
        int peerEffects = await b.QuerySingleAsync<int>(new CommandDefinition("""
            SELECT (SELECT COUNT(*) FROM dbo.BusinessSource)+(SELECT COUNT(*) FROM dbo.LedgerEffect)
                +(SELECT COUNT(*) FROM dbo.SaleDelivery)+(SELECT COUNT(*) FROM dbo.ReceivableOpenItem);
            """, cancellationToken: ct));
        bool matches = phases.All(row => row.Matches) && pricing.SequenceEqual(expectedPricing) && sale.Quantity == 3 &&
            sale.Cost == 3000 && sale.Gross == gross && !sale.InvoiceEligible && creditsMatch && sourcesMatch &&
            ledgerMatches && sourceCount == 9 && peerEffects == 0;
        string path = Path.Combine(root, "TestResults", "dev-f08", run.Id, "sql-result.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            runId = run.Id, source, apiSha256, oracleSha256 = Convert.ToHexString(SHA256.HashData(oracleBytes)),
            matches, phases, pricing, expectedPricing, delivery = new { sale.Id, sale.Quantity, sale.Cost, sale.Gross, sale.InvoiceEligible },
            credits, expectedParts, expectedDiscount, creditsMatch, settlement = new { settlement.Id, settlement.Delivery,
                settlement.Amount, settlement.Actor, settlement.Evidence }, refund, sourcesMatch, ledger, expectedLedger,
            ledgerMatches, sourceCount, verifiedSources, peerEffects
        }, new JsonSerializerOptions { WriteIndented = true }), ct);
        if (!matches) throw new InvalidOperationException($"DEV-F08 AC-19 SQL reconciliation failed. See {path}.");
        Console.WriteLine($"DEV-F08 AC-19 SQL PASS {path}");
    }
}
