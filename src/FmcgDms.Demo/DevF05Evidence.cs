using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class DevF05Evidence
{
    internal static readonly DateTime FixtureUtc = new(2026, 11, 23, 2, 0, 0, DateTimeKind.Utc);
    internal const string Receiver = "f05-receiver";
    internal const string Finance = "f05-finance";
    internal const string Qa = "f05-qa";
    private const string Sales = "c1-dev-sales";

    internal sealed record Snapshot(decimal Main, decimal MainCost, decimal Truck, decimal TruckCost, decimal Quarantine,
        decimal QuarantineCost, decimal Ar, decimal Cogs, int ReturnCases, int Receipts, int Approvals, int Releases);
    internal sealed record Checkpoint(string Phase, Snapshot Actual);
    private sealed record StageRow(string Stage, Guid SourceId, string ActorId, string Evidence, string Reason,
        decimal? AcceptedCost, string? Destination, string CommandStatus);
    private sealed record ReturnCase(Guid Id, Guid DeliveryId, Guid LotId, string LotCode, decimal Quantity,
        string RequestedBy, string Reason, string CommandStatus);
    private sealed record Ledger(string Stage, string EffectKey, string LedgerKind, string? Location,
        decimal Quantity, decimal Amount);

    private static readonly (string Phase, Snapshot Expected)[] Phases = [
        ("delivered", new(0, 0, 0, 0, 0, 0, 20000, 6667, 0, 0, 0, 0)),
        ("requested", new(0, 0, 0, 0, 0, 0, 20000, 6667, 1, 0, 0, 0)),
        ("received", new(0, 0, 0, 0, 1, 0, 20000, 6667, 1, 1, 0, 0)),
        ("approved", new(0, 0, 0, 0, 1, 0, 20000, 6667, 1, 1, 1, 0)),
        ("released-first", new(1, 3334, 0, 0, 0, 0, 20000, 3333, 1, 1, 1, 1))
    ];

    internal static string CheckpointDirectory(string root, DemoRun run) =>
        Path.Combine(root, "TestResults", "dev-f05", run.Id, "checkpoints");

    internal static async Task<Guid> Seed(SqlConnection db, string tenant, CancellationToken ct)
    {
        await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(ct);
        Guid quarantine = await db.QuerySingleAsync<Guid>(new CommandDefinition("""
            DECLARE @sku uniqueidentifier=NEWID(), @lot uniqueidentifier=NEWID(), @plan uniqueidentifier=NEWID(),
                @quote uniqueidentifier=NEWID(), @quarantine uniqueidentifier=NEWID(),
                @customer uniqueidentifier, @main uniqueidentifier, @truck uniqueidentifier;
            SELECT @customer=Id FROM dbo.Customer WHERE TenantId=@Tenant AND Code=N'C2-FIXTURE';
            SELECT @main=Id FROM dbo.StockLocation WHERE TenantId=@Tenant AND Code=N'C2-MAIN';
            SELECT @truck=Id FROM dbo.StockLocation WHERE TenantId=@Tenant AND Code=N'C2-TRUCK';

            INSERT dbo.App02ActorRoleGrant(TenantId,ActorId,RoleCode,IsActive,ChangedBy)
            VALUES(@Tenant,@Receiver,N'warehouse',1,N'f05-dev-launcher'),
                  (@Tenant,@Finance,N'finance',1,N'f05-dev-launcher'),
                  (@Tenant,@Qa,N'case_checker',1,N'f05-dev-launcher');
            INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom)
            VALUES(@sku,@Tenant,N'R-05',N'F05 returned product',N'EA');
            INSERT dbo.StockLocation(Id,TenantId,Code,Kind)
            VALUES(@quarantine,@Tenant,N'F05-QA',N'QUARANTINE');
            INSERT dbo.StockPool(TenantId,StockLocationId,SkuId,OnHandBaseQuantity)
            VALUES(@Tenant,@main,@sku,2);
            INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,SnapshotCode,BaseQuantity,CostAmount)
            VALUES(@Tenant,@main,@sku,N'F05-v1',2,6667),(@Tenant,@truck,@sku,N'F05-v1',0,0),
                (@Tenant,@quarantine,@sku,N'F05-v1',0,0);
            INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,OnHandBaseQuantity,OnHandCostAmount)
            VALUES(@Tenant,@main,@sku,2,6667),(@Tenant,@truck,@sku,0,0),(@Tenant,@quarantine,@sku,0,0);
            INSERT dbo.F03Unit(TenantId,SkuId,UomCode,Factor,[Step]) VALUES(@Tenant,@sku,N'EA',1,1);
            INSERT dbo.F03Lot(TenantId,Id,SkuId,Code,ExpiresOn)
            VALUES(@Tenant,@lot,@sku,N'RET-ORIGIN','20271231');
            INSERT dbo.F03LotOpening(TenantId,LotId,LocationId,Quantity) VALUES(@Tenant,@lot,@main,2);

            INSERT dbo.C2CommercialPolicy(TenantId,Id,CustomerId,SkuId,Version,Decision,EffectiveFromUtc,EffectiveUntilUtc,
                Maker,Checker,ApprovedAtUtc,EvidenceRef,UnitPrice,DiscountPerUnit,TaxRate,TermsDays,CreditCap,SellerTimeZone)
            VALUES(@Tenant,@quote,@customer,@sku,1,'APPROVED',@From,@Until,N'f05-maker',N'f05-checker',
                @From,N'F05-quote-R05-v1',10000,0,0,7,100000,'Asia/Ho_Chi_Minh');
            INSERT dbo.F04PricePlan(TenantId,Id,CustomerId,Version,TaxMode,OrderDiscount,
                EffectiveFromUtc,EffectiveUntilUtc,Maker,Checker,ApprovedAtUtc,EvidenceRef)
            VALUES(@Tenant,@plan,@customer,1,'EXCLUSIVE',0,@From,@Until,N'f05-maker',N'f05-checker',@From,N'F05-plan-v1');
            INSERT dbo.F04PlanQuote(TenantId,PlanId,PolicyId) VALUES(@Tenant,@plan,@quote);
            SELECT @quarantine;
            """, new { Tenant = tenant, Receiver, Finance, Qa, From = FixtureUtc.AddHours(-1), Until = FixtureUtc.AddDays(1) },
            tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return quarantine;
    }

    internal static async Task<Checkpoint[]> ServeCheckpoints(DemoRun run, string root, Process browser, CancellationToken ct)
    {
        string dir = CheckpointDirectory(root, run);
        Directory.CreateDirectory(dir);
        var observed = new List<Checkpoint>();
        foreach (var (phase, _) in Phases)
        {
            string request = Path.Combine(dir, phase + ".request");
            while (!File.Exists(request))
            {
                if (browser.HasExited) return observed.ToArray();
                await Task.Delay(75, ct);
            }
            if ((await File.ReadAllTextAsync(request, ct)) != run.Id)
                throw new InvalidDataException("DEV-F05 checkpoint has the wrong run ID.");
            var snapshot = new Checkpoint(phase, await ReadSnapshot(run, "tenant-a", ct));
            observed.Add(snapshot);
            await File.WriteAllTextAsync(Path.Combine(dir, phase + ".json"), JsonSerializer.Serialize(snapshot), ct);
            string ready = Path.Combine(dir, phase + ".ready");
            await File.WriteAllTextAsync(ready + ".partial", run.Id, ct);
            File.Move(ready + ".partial", ready);
        }
        return observed.ToArray();
    }

    internal static async Task Verify(DemoRun run, string root, string source, string apiSha256,
        Checkpoint[]? checkpoints, CancellationToken ct)
    {
        await run.Verify("tenant-a");
        await run.Verify("tenant-b");
        await using var a = new SqlConnection(run.Connection("tenant-a"));
        await using var b = new SqlConnection(run.Connection("tenant-b"));
        Snapshot after = await ReadSnapshot(run, "tenant-a", ct);
        Snapshot peer = await ReadSnapshot(run, "tenant-b", ct);
        var expectedAfter = new Snapshot(2, 6667, 0, 0, 0, 0, 20000, 0, 2, 2, 2, 2);
        var expectedPeer = new Snapshot(2, 6667, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var byPhase = (checkpoints ?? []).ToDictionary(entry => entry.Phase, entry => entry.Actual, StringComparer.Ordinal);
        var phaseDiff = Phases.Select(entry =>
        {
            Snapshot? actual = byPhase.GetValueOrDefault(entry.Phase);
            return new { entry.Phase, entry.Expected, Actual = actual, Matches = actual == entry.Expected };
        }).ToArray();

        var outbound = await a.QuerySingleAsync<(Guid DeliveryId, Guid LotId, decimal Quantity, decimal Cost,
            decimal Gross, long PricingGross, bool InvoiceEligible)>(new CommandDefinition("""
            SELECT d.Id AS DeliveryId,i.LotId,d.BaseQuantity AS Quantity,d.CostAmount AS Cost,
                d.PayableAmount AS Gross,p.GrossAmount AS PricingGross,d.InvoiceEligible
            FROM dbo.SaleDelivery d
            JOIN dbo.F03LotIssue i ON i.TenantId=d.TenantId AND i.DeliveryId=d.Id
            JOIN dbo.F04DeliveryPricing p ON p.TenantId=d.TenantId AND p.DeliveryId=d.Id
            JOIN dbo.Sku k ON k.TenantId=d.TenantId AND k.Id=d.SkuId AND k.Code=N'R-05'
            WHERE i.Quantity=d.BaseQuantity AND p.Currency='VND'
            """, cancellationToken: ct));
        var cases = (await a.QueryAsync<ReturnCase>(new CommandDefinition("""
            SELECT r.Id,r.DeliveryId,r.LotId,l.Code AS LotCode,r.Quantity,r.RequestedBy,r.Reason,c.Status AS CommandStatus
            FROM dbo.F05ReturnCase r
            JOIN dbo.F03Lot l ON l.TenantId=r.TenantId AND l.Id=r.LotId
            JOIN dbo.BusinessSource s ON s.TenantId=r.TenantId AND s.Id=r.Id
            JOIN dbo.BusinessCommand c ON c.TenantId=s.TenantId AND c.Id=s.BusinessCommandId
            ORDER BY r.Id
            """, cancellationToken: ct))).ToArray();
        bool caseMatches = cases.Length == 2 && cases.All(item =>
            item.DeliveryId == outbound.DeliveryId && item.LotId == outbound.LotId && item.LotCode == "RET-ORIGIN"
            && item.Quantity == 1 && item.RequestedBy == Sales && item.Reason.Length > 0 && item.CommandStatus == "APPLIED");
        var stageEvidence = new List<object>();
        bool stagesMatch = true;
        foreach (var item in cases)
        {
            StageRow[] stages = (await a.QueryAsync<StageRow>(new CommandDefinition("""
                SELECT r.Stage,r.SourceId,r.ActorId,r.Evidence,r.Reason,r.AcceptedCost,l.Code AS Destination,
                    c.Status AS CommandStatus
                FROM dbo.F05ReturnStage r
                JOIN dbo.BusinessSource s ON s.TenantId=r.TenantId AND s.Id=r.SourceId
                JOIN dbo.BusinessCommand c ON c.TenantId=s.TenantId AND c.Id=s.BusinessCommandId
                LEFT JOIN dbo.StockLocation l ON l.TenantId=r.TenantId AND l.Id=r.DestinationId
                WHERE r.CaseId=@CaseId ORDER BY r.Stage
                """, new { CaseId = item.Id }, cancellationToken: ct))).ToArray();
            var release = stages.SingleOrDefault(stage => stage.Stage == "RELEASE");
            decimal expectedCost = release?.Evidence switch { "F05-release-first" => 3334, "F05-release-second" => 3333, _ => -1 };
            bool stageMatches = stages.Length == 3
                && stages.SingleOrDefault(stage => stage.Stage == "RECEIVE")
                    is { ActorId: Receiver, Destination: "F05-QA", AcceptedCost: null }
                && stages.SingleOrDefault(stage => stage.Stage == "APPROVE")
                    is { ActorId: Finance, Destination: null, AcceptedCost: null }
                && release is { ActorId: Qa, Destination: "C2-MAIN" }
                && expectedCost >= 0 && release.AcceptedCost == expectedCost
                && stages.All(stage => stage.CommandStatus == "APPLIED" && stage.SourceId != Guid.Empty
                    && stage.Evidence.Length > 0 && stage.Reason.Length > 0);
            Ledger[] ledger = (await a.QueryAsync<Ledger>(new CommandDefinition("""
                SELECT r.Stage,e.EffectKey,e.LedgerKind,l.Code AS Location,e.QuantityDelta AS Quantity,e.AmountDelta AS Amount
                FROM dbo.F05ReturnStage r
                JOIN dbo.BusinessSourceLine s ON s.TenantId=r.TenantId AND s.BusinessSourceId=r.SourceId
                JOIN dbo.LedgerEffect e ON e.TenantId=s.TenantId AND e.BusinessSourceLineId=s.Id
                LEFT JOIN dbo.StockLocation l ON l.TenantId=e.TenantId AND l.Id=e.StockLocationId
                WHERE r.CaseId=@CaseId ORDER BY r.Stage,e.EffectKey
                """, new { CaseId = item.Id }, cancellationToken: ct))).ToArray();
            var expectedLedger = new Ledger[]
            {
                new("RECEIVE", "RETURN-QA-IN", "PHYSICAL_STOCK", "F05-QA", 1, 0),
                new("RELEASE", "RETURN-COGS", "COGS", null, 0, -expectedCost),
                new("RELEASE", "RETURN-MAIN-IN", "PHYSICAL_STOCK", "C2-MAIN", 1, expectedCost),
                new("RELEASE", "RETURN-QA-OUT", "PHYSICAL_STOCK", "F05-QA", -1, 0)
            };
            bool ledgerMatches = ledger.SequenceEqual(expectedLedger);
            stagesMatch &= stageMatches && ledgerMatches;
            stageEvidence.Add(new { CaseId = item.Id, stages, expectedCost, stageMatches, ledger, expectedLedger, ledgerMatches });
        }

        int peerEffects = await b.QuerySingleAsync<int>(new CommandDefinition("""
            SELECT (SELECT COUNT(*) FROM dbo.BusinessSource)+(SELECT COUNT(*) FROM dbo.LedgerEffect)
                +(SELECT COUNT(*) FROM dbo.SaleDelivery)+(SELECT COUNT(*) FROM dbo.ReceivableOpenItem)
            """, cancellationToken: ct));
        bool outboundMatches = outbound.Quantity == 2 && outbound.Cost == 6667 && outbound.Gross == 20000
            && outbound.PricingGross == 20000 && !outbound.InvoiceEligible;
        bool matches = phaseDiff.All(point => point.Matches) && after == expectedAfter && peer == expectedPeer
            && peerEffects == 0 && outboundMatches && caseMatches && stagesMatch;
        string output = Path.Combine(root, "TestResults", "dev-f05", run.Id, "sql-result.json");
        static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            runId = run.Id, source, apiSha256, environment = "Synthetic Windows LocalDB", instance = DemoRun.Instance,
            observedAtUtc = DateTime.UtcNow, fixtureUtc = FixtureUtc, acceptance = new[] { "AC-05", "AC-06" }, fixtureVersion = "v1",
            browserFixtureSha256 = Hash(Path.Combine(root, "web", "e2e-dev-f05", "returns.spec.ts")),
            browserReportSha256 = Hash(Path.Combine(root, "web", "test-results-app02c5", run.Id, "junit.xml")),
            outputExpected = new { deliveredQuantity = 2, deliveredCost = 6667, ar = 20000, acceptedCosts = new[] { 3334, 3333 } },
            outbound = new { outbound.DeliveryId,outbound.LotId,outbound.Quantity,outbound.Cost,outbound.Gross,
                outbound.PricingGross,outbound.InvoiceEligible },
            outboundMatches, checkpoints = phaseDiff, expectedAfter, after, expectedPeer, peer, peerEffects,
            cases, caseMatches, stageEvidence, stagesMatch, matches
        }, new JsonSerializerOptions { WriteIndented = true }), ct);
        Console.WriteLine($"DEV-F05 SQL comparison: {output}");
        if (!matches) throw new InvalidOperationException("DEV-F05 observations differ from the frozen return fixture.");
    }

    private static async Task<Snapshot> ReadSnapshot(DemoRun run, string tenant, CancellationToken ct)
    {
        await using var db = new SqlConnection(run.Connection(tenant));
        return await db.QuerySingleAsync<Snapshot>(new CommandDefinition("""
            SELECT
                COALESCE(SUM(IIF(l.Code='C2-MAIN',b.OnHandBaseQuantity,0)),0) AS Main,
                COALESCE(SUM(IIF(l.Code='C2-MAIN',b.OnHandCostAmount,0)),0) AS MainCost,
                COALESCE(SUM(IIF(l.Code='C2-TRUCK',b.OnHandBaseQuantity,0)),0) AS Truck,
                COALESCE(SUM(IIF(l.Code='C2-TRUCK',b.OnHandCostAmount,0)),0) AS TruckCost,
                COALESCE(SUM(IIF(l.Code='F05-QA',b.OnHandBaseQuantity,0)),0) AS Quarantine,
                COALESCE(SUM(IIF(l.Code='F05-QA',b.OnHandCostAmount,0)),0) AS QuarantineCost,
                (SELECT COALESCE(SUM(OutstandingAmount),0) FROM dbo.ReceivableOpenItem) AS Ar,
                (SELECT COALESCE(SUM(AmountDelta),0) FROM dbo.LedgerEffect WHERE LedgerKind='COGS') AS Cogs,
                (SELECT COUNT(*) FROM dbo.F05ReturnCase) AS ReturnCases,
                (SELECT COUNT(*) FROM dbo.F05ReturnStage WHERE Stage='RECEIVE') AS Receipts,
                (SELECT COUNT(*) FROM dbo.F05ReturnStage WHERE Stage='APPROVE') AS Approvals,
                (SELECT COUNT(*) FROM dbo.F05ReturnStage WHERE Stage='RELEASE') AS Releases
            FROM dbo.PhysicalStockBalance b
            JOIN dbo.StockLocation l ON l.TenantId=b.TenantId AND l.Id=b.StockLocationId
            JOIN dbo.Sku k ON k.TenantId=b.TenantId AND k.Id=b.SkuId AND k.Code=N'R-05'
            """, cancellationToken: ct));
    }
}
