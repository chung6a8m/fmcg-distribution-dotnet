using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class DevF06Evidence
{
    private static readonly DateTime November = new(2026, 11, 1);
    private static readonly DateTime December = new(2026, 12, 1);
    private static readonly DateTime ClosedAt = new(2026, 11, 30, 17, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DecemberUtc = new(2026, 12, 1, 2, 0, 0, DateTimeKind.Utc);
    private const string CloseReference = "DEV-F06-NOV-CLOSE-v1";

    internal sealed record Balance(decimal Main, decimal MainValue, decimal Quarantine, decimal QuarantineValue,
        decimal Ar, decimal Cogs, int Returns, int Proposals, int Adjustments);
    internal sealed record Digest(string Area, int Rows, string Sha256);
    internal sealed record Checkpoints(Balance? NovemberBalance, Digest[]? November, Digest[]? PeerNovember,
        bool Locked, bool Advanced, Balance? Adjusted);
    private sealed record Period(DateTime MonthStart, string State, string? CloseReference, DateTime? ClosedAtUtc);
    private sealed record Adjustment(Guid Id, Guid ProposalId, Guid DeliveryId, decimal OriginalCost, decimal AdjustedCost,
        decimal PriorReturnedQuantity, decimal PriorRefund, decimal PriorAdjustment, DateTime PeriodStart,
        string ActorId, string Reason, string Evidence, string Warehouse, DateTime OccurredAtUtc);
    private sealed record Proposal(Guid Id, Guid DeliveryId, decimal NewCost, string ActorId, string Reason, string Evidence,
        DateTime PeriodStart, DateTime OccurredAtUtc);
    private sealed record Ledger(string EffectKey, string Kind, string? Location, decimal Quantity, decimal Amount,
        DateTime RecordedAtUtc);
    private sealed record Return(Guid CaseId, Guid DeliveryId, Guid LotId, string Lot, decimal Quantity, decimal Cost,
        DateTime OccurredAtUtc, decimal CogsReversal);

    internal static string CheckpointDirectory(string root, DemoRun run) =>
        Path.Combine(root, "TestResults", "dev-f06", run.Id, "checkpoints");

    internal static async Task Seed(string connection, string tenant, CancellationToken ct)
    {
        await using var db = new SqlConnection(connection);
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT dbo.F06Period(TenantId,MonthStart,State)
            VALUES(@Tenant,@November,'OPEN'),(@Tenant,@December,'OPEN');

            -- The active dual-role fixture exercises maker/checker separation.
            INSERT dbo.App02ActorRoleGrant(TenantId,ActorId,RoleCode,IsActive,ChangedBy)
            VALUES(@Tenant,@Finance,N'case_checker',1,N'f06-dev-self-check');
            INSERT dbo.F05QaScope(TenantId,ActorId,WarehouseId)
            SELECT @Tenant,@Finance,Id FROM dbo.StockLocation WHERE TenantId=@Tenant AND Code=N'C2-MAIN';

            IF @Tenant=N'tenant-a'
            BEGIN
                DECLARE @sku uniqueidentifier=NEWID(), @lot uniqueidentifier=NEWID(), @quote uniqueidentifier=NEWID(),
                    @main uniqueidentifier, @truck uniqueidentifier, @customer uniqueidentifier, @plan uniqueidentifier;
                SELECT @main=Id FROM dbo.StockLocation WHERE TenantId=@Tenant AND Code=N'C2-MAIN';
                SELECT @truck=Id FROM dbo.StockLocation WHERE TenantId=@Tenant AND Code=N'C2-TRUCK';
                SELECT @customer=Id FROM dbo.Customer WHERE TenantId=@Tenant AND Code=N'C2-FIXTURE';
                SELECT @plan=Id FROM dbo.F04PricePlan WHERE TenantId=@Tenant AND CustomerId=@customer AND Version=1;

                INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom)
                VALUES(@sku,@Tenant,N'R-LOCK',N'F06 locked delivery guard',N'EA');
                INSERT dbo.StockPool(TenantId,StockLocationId,SkuId,OnHandBaseQuantity) VALUES(@Tenant,@main,@sku,1);
                INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,SnapshotCode,BaseQuantity,CostAmount)
                VALUES(@Tenant,@main,@sku,N'F06-v1',1,250),(@Tenant,@truck,@sku,N'F06-v1',0,0);
                INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,OnHandBaseQuantity,OnHandCostAmount)
                VALUES(@Tenant,@main,@sku,1,250),(@Tenant,@truck,@sku,0,0);
                INSERT dbo.F03Unit(TenantId,SkuId,UomCode,Factor,[Step]) VALUES(@Tenant,@sku,N'EA',1,1);
                INSERT dbo.F03Lot(TenantId,Id,SkuId,Code,ExpiresOn)
                VALUES(@Tenant,@lot,@sku,N'LOCK-GUARD','20271231');
                INSERT dbo.F03LotOpening(TenantId,LotId,LocationId,Quantity) VALUES(@Tenant,@lot,@main,1);
                INSERT dbo.C2CommercialPolicy(TenantId,Id,CustomerId,SkuId,Version,Decision,EffectiveFromUtc,EffectiveUntilUtc,
                    Maker,Checker,ApprovedAtUtc,EvidenceRef,UnitPrice,DiscountPerUnit,TaxRate,TermsDays,CreditCap,SellerTimeZone)
                VALUES(@Tenant,@quote,@customer,@sku,1,'APPROVED',@From,@Until,N'f06-maker',N'f06-checker',
                    @From,N'F06-lock-guard-v1',10000,0,0,7,100000,'Asia/Ho_Chi_Minh');
                INSERT dbo.F04PlanQuote(TenantId,PlanId,PolicyId) VALUES(@Tenant,@plan,@quote);
            END;
            """, new { Tenant = tenant, November, December, DevF05Evidence.Finance,
                From = DevF05Evidence.FixtureUtc.AddHours(-1), Until = DevF05Evidence.FixtureUtc.AddDays(1) }, cancellationToken: ct));
    }

    internal static async Task<Checkpoints> ServeCheckpoints(DemoRun run, string root, Process browser, CancellationToken ct)
    {
        string dir = CheckpointDirectory(root, run);
        Directory.CreateDirectory(dir);
        if (!await WaitRequest(browser, dir, "lock-november", run, ct))
            return new(null, null, null, false, false, null);

        Balance original = await ReadBalance(run, "tenant-a", ct);
        if (original != new Balance(1, 3334, 0, 0, 20000, 3333, 1, 0, 0))
            throw new InvalidOperationException("DEV-F06 starts at the first released November return.");
        Digest[] frozen = await ReadFrozen(run, "tenant-a", ct);
        Digest[] frozenPeer = await ReadFrozen(run, "tenant-b", ct);
        await LockNovember(run, ct);
        await Publish(dir, "lock-november", run, new { original, frozen, frozenPeer, CloseReference }, ct);

        if (!await WaitRequest(browser, dir, "advance-december", run, ct))
            return new(original, frozen, frozenPeer, true, false, null);
        await AdvanceClock(run, root, ct);
        await Publish(dir, "advance-december", run, new { Clock = DecemberUtc }, ct);

        if (!await WaitRequest(browser, dir, "approved-adjustment", run, ct))
            return new(original, frozen, frozenPeer, true, true, null);
        Balance adjusted = await ReadBalance(run, "tenant-a", ct);
        await Publish(dir, "approved-adjustment", run, adjusted, ct);
        return new(original, frozen, frozenPeer, true, true, adjusted);
    }

    internal static async Task<bool> CloseInteractive(DemoRun run, string root, CancellationToken ct)
    {
        if (await ReadBalance(run, "tenant-a", ct) != new Balance(1, 3334, 0, 0, 20000, 3333, 1, 0, 0))
            return false;
        await LockNovember(run, ct);
        await AdvanceClock(run, root, ct);
        return true;
    }

    private static async Task<bool> WaitRequest(Process browser, string directory, string phase, DemoRun run, CancellationToken ct)
    {
        string request = Path.Combine(directory, phase + ".request");
        while (!File.Exists(request))
        {
            if (browser.HasExited) return false;
            await Task.Delay(75, ct);
        }
        if ((await File.ReadAllTextAsync(request, ct)) != run.Id)
            throw new InvalidDataException("DEV-F06 checkpoint has the wrong run ID.");
        return true;
    }

    private static async Task Publish(string directory, string phase, DemoRun run, object result, CancellationToken ct)
    {
        await File.WriteAllTextAsync(Path.Combine(directory, phase + ".json"), JsonSerializer.Serialize(result), ct);
        string ready = Path.Combine(directory, phase + ".ready");
        await File.WriteAllTextAsync(ready + ".partial", run.Id, ct);
        File.Move(ready + ".partial", ready);
    }

    private static async Task LockNovember(DemoRun run, CancellationToken ct)
    {
        foreach (string tenant in DemoRun.Tenants)
        {
            await run.Verify(tenant);
            await using var db = new SqlConnection(run.Connection(tenant));
            if (await db.ExecuteAsync(new CommandDefinition("""
                UPDATE dbo.F06Period SET State='LOCKED',CloseReference=@Reference,ClosedAtUtc=@ClosedAt
                WHERE TenantId=@Tenant AND MonthStart=@Month AND State='OPEN'
                """, new { Tenant = tenant, Month = November, Reference = CloseReference, ClosedAt }, cancellationToken: ct)) != 1)
                throw new InvalidOperationException("DEV-F06 could not close the nonce-owned November fixture period.");
        }
    }

    private static async Task AdvanceClock(DemoRun run, string root, CancellationToken ct)
    {
        foreach (string tenant in DemoRun.Tenants)
        {
            await run.Verify(tenant);
            await using var db = new SqlConnection(run.Connection(tenant));
            if (!(await ReadPeriods(db, ct)).SequenceEqual(ExpectedPeriods()))
                throw new InvalidOperationException("DEV-F06 requires locked November and open December before advancing.");
        }
        string clock = Path.Combine(root, "TestResults", "dev-f06", run.Id, "clock.utc");
        await File.WriteAllTextAsync(clock + ".partial", DecemberUtc.ToString("O"), ct);
        File.Move(clock + ".partial", clock, overwrite: true);
    }

    internal static async Task Verify(DemoRun run, string root, string source, string apiSha256, Checkpoints? checkpoints,
        CancellationToken ct)
    {
        await run.Verify("tenant-a");
        await run.Verify("tenant-b");
        string oraclePath = Path.Combine(root, "business", "reference-data", "expected-results", "additional-oracles.json");
        using var golden = JsonDocument.Parse(await File.ReadAllTextAsync(oraclePath, ct));
        JsonElement oracle = golden.RootElement.GetProperty("AC-06");
        decimal originalCost = oracle.GetProperty("sourceOriginalCost").GetDecimal();
        decimal adjustedCost = oracle.GetProperty("sourceAdjustedCost").GetDecimal();
        decimal originalRefund = oracle.GetProperty("firstRefund").GetDecimal();
        decimal refundAdjustment = oracle.GetProperty("firstRefundAdjustment").GetDecimal();
        decimal adjustedSecond = oracle.GetProperty("finalSecondRefund").GetDecimal();
        decimal expectedCostSum = oracle.GetProperty("expectedAdjustedSum").GetDecimal();
        var expectedNovember = new Balance(1, originalRefund, 0, 0, 20000, originalCost - originalRefund, 1, 0, 0);
        var expectedAdjusted = new Balance(1, originalRefund + refundAdjustment, 1, 0, 20000, adjustedSecond, 1, 1, 1);
        var expectedAfter = new Balance(2, expectedCostSum, 0, 0, 20000, 0, 2, 1, 1);
        var expectedPeer = new Balance(2, originalCost, 0, 0, 0, 0, 0, 0, 0);

        await using var a = new SqlConnection(run.Connection("tenant-a"));
        await using var b = new SqlConnection(run.Connection("tenant-b"));
        Digest[] afterNovember = await ReadFrozen(run, "tenant-a", ct);
        Digest[] peerNovember = await ReadFrozen(run, "tenant-b", ct);
        Balance after = await ReadBalance(run, "tenant-a", ct);
        Balance peer = await ReadBalance(run, "tenant-b", ct);
        Period[] periodsA = await ReadPeriods(a, ct);
        Period[] periodsB = await ReadPeriods(b, ct);
        Adjustment[] adjustments = (await a.QueryAsync<Adjustment>(new CommandDefinition("""
            SELECT x.Id,x.ProposalId,x.DeliveryId,CAST(x.OriginalCost AS decimal(19,4)) AS OriginalCost,
                CAST(x.AdjustedCost AS decimal(19,4)) AS AdjustedCost,x.PriorReturnedQuantity,
                CAST(x.PriorRefund AS decimal(19,4)) AS PriorRefund,CAST(x.PriorAdjustment AS decimal(19,4)) AS PriorAdjustment,
                x.PeriodStart,x.ActorId,x.Reason,x.Evidence,l.Code AS Warehouse,s.OccurredAtUtc
            FROM dbo.F06CostAdjustment x
            JOIN dbo.BusinessSource s ON s.TenantId=x.TenantId AND s.Id=x.Id
            JOIN dbo.StockLocation l ON l.TenantId=x.TenantId AND l.Id=x.WarehouseId
            """, cancellationToken: ct))).ToArray();
        Proposal[] proposals = (await a.QueryAsync<Proposal>(new CommandDefinition("""
            SELECT x.Id,x.DeliveryId,CAST(x.NewCost AS decimal(19,4)) AS NewCost,x.ActorId,x.Reason,x.Evidence,
                x.PeriodStart,s.OccurredAtUtc
            FROM dbo.F06CostProposal x JOIN dbo.BusinessSource s ON s.TenantId=x.TenantId AND s.Id=x.Id
            """, cancellationToken: ct))).ToArray();
        Ledger[] ledger = (await a.QueryAsync<Ledger>(new CommandDefinition("""
            SELECT e.EffectKey,e.LedgerKind AS Kind,l.Code AS Location,e.QuantityDelta AS Quantity,e.AmountDelta AS Amount,
                e.RecordedAtUtc FROM dbo.F06CostAdjustment x
            JOIN dbo.BusinessSourceLine s ON s.TenantId=x.TenantId AND s.BusinessSourceId=x.Id
            JOIN dbo.LedgerEffect e ON e.TenantId=s.TenantId AND e.BusinessSourceLineId=s.Id
            LEFT JOIN dbo.StockLocation l ON l.TenantId=e.TenantId AND l.Id=e.StockLocationId
            ORDER BY e.EffectKey
            """, cancellationToken: ct))).ToArray();
        var expectedLedger = new Ledger[]
        {
            new("COST-RETURN-DELTA", "COGS", null, 0, -refundAdjustment, DecemberUtc),
            new("COST-RETURN-STOCK", "PHYSICAL_STOCK", "C2-MAIN", 0, refundAdjustment, DecemberUtc),
            new("COST-SOURCE-DELTA", "COGS", null, 0, adjustedCost - originalCost, DecemberUtc)
        };
        Return[] returns = (await a.QueryAsync<Return>(new CommandDefinition("""
            SELECT c.Id AS CaseId,c.DeliveryId,c.LotId,l.Code AS Lot,c.Quantity,t.AcceptedCost AS Cost,s.OccurredAtUtc,
                e.AmountDelta AS CogsReversal
            FROM dbo.F05ReturnStage t
            JOIN dbo.F05ReturnCase c ON c.TenantId=t.TenantId AND c.Id=t.CaseId
            JOIN dbo.F03Lot l ON l.TenantId=c.TenantId AND l.Id=c.LotId
            JOIN dbo.BusinessSource s ON s.TenantId=t.TenantId AND s.Id=t.SourceId
            JOIN dbo.BusinessSourceLine d ON d.TenantId=s.TenantId AND d.BusinessSourceId=s.Id
            JOIN dbo.LedgerEffect e ON e.TenantId=d.TenantId AND e.BusinessSourceLineId=d.Id AND e.EffectKey=N'RETURN-COGS'
            WHERE t.Stage='RELEASE' ORDER BY s.OccurredAtUtc,t.SourceId
            """, cancellationToken: ct))).ToArray();
        var outbound = await a.QuerySingleAsync<(Guid Id, decimal Cost, decimal Quantity, bool InvoiceEligible)>(
            new CommandDefinition("""
                SELECT Id,CostAmount AS Cost,BaseQuantity AS Quantity,InvoiceEligible FROM dbo.SaleDelivery
                """, cancellationToken: ct));
        int peerSources = await b.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM dbo.BusinessSource", cancellationToken: ct));
        var negatives = (await a.QueryAsync<string>(new CommandDefinition("""
            SELECT ReasonCode FROM dbo.BusinessCommand
            WHERE ReasonCode IN ('F06_OPEN_PERIOD_REQUIRED','F06_SEPARATION_REQUIRED','F06_ADJUSTMENT_EXISTS',
                'F06_ADJUSTMENT_REQUIRED') ORDER BY ReasonCode
            """, cancellationToken: ct))).ToArray();
        string[] expectedRejections = ["F06_ADJUSTMENT_EXISTS", "F06_ADJUSTMENT_REQUIRED",
            "F06_OPEN_PERIOD_REQUIRED", "F06_OPEN_PERIOD_REQUIRED", "F06_SEPARATION_REQUIRED"];
        int deniedPeerSource = await b.QuerySingleAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM dbo.BusinessCommand WHERE ReasonCode='F06_SOURCE_NOT_FOUND'
            """, cancellationToken: ct));

        bool periodsMatch = periodsA.SequenceEqual(ExpectedPeriods()) && periodsB.SequenceEqual(ExpectedPeriods());
        bool frozenMatch = checkpoints is { Locked: true, Advanced: true, November: not null, PeerNovember: not null }
            && checkpoints.November.SequenceEqual(afterNovember) && checkpoints.PeerNovember.SequenceEqual(peerNovember)
            && afterNovember.Any(row => row.Area == "LedgerEffect" && row.Rows > 0);
        bool checkpointBalancesMatch = checkpoints?.NovemberBalance == expectedNovember && checkpoints.Adjusted == expectedAdjusted;
        bool adjustmentMatches = adjustments is [var row] && proposals is [var proposal]
            && row.ProposalId == proposal.Id && row.DeliveryId == outbound.Id && proposal.DeliveryId == row.DeliveryId
            && row.OriginalCost == originalCost && row.AdjustedCost == adjustedCost && proposal.NewCost == adjustedCost
            && row.PriorReturnedQuantity == 1 && row.PriorRefund == originalRefund && row.PriorAdjustment == refundAdjustment
            && row.PeriodStart == December && row.OccurredAtUtc == DecemberUtc
            && proposal.PeriodStart == December && proposal.OccurredAtUtc == DecemberUtc
            && proposal.ActorId == DevF05Evidence.Finance && row.ActorId == DevF05Evidence.Qa && row.Warehouse == "C2-MAIN";
        bool returnsMatch = returns is [var first, var second] && first.DeliveryId == outbound.Id && second.DeliveryId == outbound.Id
            && first.LotId == second.LotId && first.Lot == "RET-ORIGIN" && second.Lot == "RET-ORIGIN"
            && first.Quantity == 1 && second.Quantity == 1 && first.OccurredAtUtc == DevF05Evidence.FixtureUtc
            && second.OccurredAtUtc == DecemberUtc && first.Cost == originalRefund && second.Cost == adjustedSecond
            && first.CogsReversal == -originalRefund && second.CogsReversal == -adjustedSecond
            && first.Cost + refundAdjustment + second.Cost == expectedCostSum;
        bool outboundMatches = outbound.Cost == originalCost && outbound.Quantity == oracle.GetProperty("sourceQty").GetDecimal()
            && !outbound.InvoiceEligible;
        bool matches = periodsMatch && frozenMatch && checkpointBalancesMatch && adjustmentMatches && returnsMatch
            && outboundMatches && ledger.SequenceEqual(expectedLedger) && after == expectedAfter && peer == expectedPeer
            && peerSources == 0 && negatives.SequenceEqual(expectedRejections) && deniedPeerSource == 1;

        string output = Path.Combine(root, "TestResults", "dev-f06", run.Id, "sql-result.json");
        static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            runId = run.Id, source, apiSha256, environment = "Synthetic Windows LocalDB", instance = DemoRun.Instance,
            observedAtUtc = DateTime.UtcNow, fixtureVersion = "v1", acceptance = "AC-06", immutableOracleSha256 = Hash(oraclePath),
            browserFixtureSha256 = Hash(Path.Combine(root, "web", "e2e-dev-f06", "cost-adjustments.spec.ts")),
            browserReportSha256 = Hash(Path.Combine(root, "web", "test-results-app02c5", run.Id, "junit.xml")),
            expected = new { originalCost, adjustedCost, originalRefund, refundAdjustment, adjustedSecond, expectedCostSum },
            outbound = new { outbound.Id, outbound.Cost, outbound.Quantity, outbound.InvoiceEligible },
            checkpoints, expectedNovember, expectedAdjusted, expectedAfter, expectedPeer, after, peer, peerSources,
            afterNovember, peerNovember, periodsA, periodsB, proposals, adjustments, returns, expectedLedger, ledger,
            returnedCostDiff = returns.Sum(r => r.Cost) + adjustments.Sum(r => r.PriorAdjustment) - expectedCostSum,
            expectedRejections, negatives, deniedPeerSource, periodsMatch, frozenMatch, checkpointBalancesMatch,
            adjustmentMatches, returnsMatch, outboundMatches, matches
        }, new JsonSerializerOptions { WriteIndented = true }), ct);
        Console.WriteLine($"DEV-F06 SQL comparison: {output}");
        if (!matches) throw new InvalidOperationException("DEV-F06 observations differ from the locked AC-06 fixture.");
    }

    private static Period[] ExpectedPeriods() =>
        [new(November, "LOCKED", CloseReference, ClosedAt), new(December, "OPEN", null, null)];

    private static async Task<Period[]> ReadPeriods(SqlConnection db, CancellationToken ct) =>
        (await db.QueryAsync<Period>(new CommandDefinition("""
            SELECT MonthStart,State,CloseReference,ClosedAtUtc FROM dbo.F06Period ORDER BY MonthStart
            """, cancellationToken: ct))).ToArray();

    private static async Task<Balance> ReadBalance(DemoRun run, string tenant, CancellationToken ct)
    {
        await using var db = new SqlConnection(run.Connection(tenant));
        return await db.QuerySingleAsync<Balance>(new CommandDefinition("""
            SELECT
                COALESCE(SUM(IIF(l.Code='C2-MAIN',b.OnHandBaseQuantity,0)),0) AS Main,
                COALESCE(SUM(IIF(l.Code='C2-MAIN',b.OnHandCostAmount,0)),0) AS MainValue,
                COALESCE(SUM(IIF(l.Code='F05-QA',b.OnHandBaseQuantity,0)),0) AS Quarantine,
                COALESCE(SUM(IIF(l.Code='F05-QA',b.OnHandCostAmount,0)),0) AS QuarantineValue,
                (SELECT COALESCE(SUM(OutstandingAmount),0) FROM dbo.ReceivableOpenItem) AS Ar,
                (SELECT COALESCE(SUM(AmountDelta),0) FROM dbo.LedgerEffect WHERE LedgerKind='COGS') AS Cogs,
                (SELECT COUNT(*) FROM dbo.F05ReturnStage WHERE Stage='RELEASE') AS Returns,
                (SELECT COUNT(*) FROM dbo.F06CostProposal) AS Proposals,
                (SELECT COUNT(*) FROM dbo.F06CostAdjustment) AS Adjustments
            FROM dbo.PhysicalStockBalance b JOIN dbo.StockLocation l ON l.TenantId=b.TenantId AND l.Id=b.StockLocationId
            JOIN dbo.Sku k ON k.TenantId=b.TenantId AND k.Id=b.SkuId AND k.Code=N'R-05'
            """, cancellationToken: ct));
    }

    private static async Task<Digest[]> ReadFrozen(DemoRun run, string tenant, CancellationToken ct)
    {
        await using var db = new SqlConnection(run.Connection(tenant));
        (string Area, string Select)[] queries = [
            ("BusinessSource", "SELECT s.* FROM dbo.BusinessSource s WHERE s.OccurredAtUtc<@Cutoff"),
            ("BusinessCommand", """
                SELECT c.* FROM dbo.BusinessCommand c JOIN dbo.BusinessSource s ON s.TenantId=c.TenantId AND s.BusinessCommandId=c.Id
                WHERE s.OccurredAtUtc<@Cutoff
                """),
            ("BusinessSourceLine", """
                SELECT l.* FROM dbo.BusinessSourceLine l JOIN dbo.BusinessSource s ON s.TenantId=l.TenantId AND s.Id=l.BusinessSourceId
                WHERE s.OccurredAtUtc<@Cutoff
                """),
            ("LedgerEffect", """
                SELECT e.* FROM dbo.LedgerEffect e
                JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId AND l.Id=e.BusinessSourceLineId
                JOIN dbo.BusinessSource s ON s.TenantId=l.TenantId AND s.Id=l.BusinessSourceId
                WHERE s.OccurredAtUtc<@Cutoff
                """),
            ("SaleDelivery", "SELECT d.* FROM dbo.SaleDelivery d WHERE d.BusinessDate<@Month"),
            ("DeliveryStockIssue", """
                SELECT x.* FROM dbo.DeliveryStockIssue x JOIN dbo.SaleDelivery d ON d.TenantId=x.TenantId AND d.Id=x.SaleDeliveryId
                WHERE d.BusinessDate<@Month
                """),
            ("F04DeliveryPricing", """
                SELECT p.* FROM dbo.F04DeliveryPricing p JOIN dbo.SaleDelivery d ON d.TenantId=p.TenantId AND d.Id=p.DeliveryId
                WHERE d.BusinessDate<@Month
                """),
            ("F03LotIssue", """
                SELECT i.* FROM dbo.F03LotIssue i JOIN dbo.SaleDelivery d ON d.TenantId=i.TenantId AND d.Id=i.DeliveryId
                WHERE d.BusinessDate<@Month
                """),
            ("ReceivableOpenItem", """
                SELECT r.* FROM dbo.ReceivableOpenItem r JOIN dbo.SaleDelivery d ON d.TenantId=r.TenantId AND d.Id=r.SaleDeliveryId
                WHERE d.BusinessDate<@Month
                """),
            ("F05ReturnCase", """
                SELECT c.* FROM dbo.F05ReturnCase c JOIN dbo.BusinessSource s ON s.TenantId=c.TenantId AND s.Id=c.Id
                WHERE s.OccurredAtUtc<@Cutoff
                """),
            ("F05ReturnStage", """
                SELECT r.* FROM dbo.F05ReturnStage r JOIN dbo.BusinessSource s ON s.TenantId=r.TenantId AND s.Id=r.SourceId
                WHERE s.OccurredAtUtc<@Cutoff
                """),
            ("C4DeliveryEvidence", """
                SELECT e.* FROM dbo.C4DeliveryEvidence e
                JOIN dbo.SaleDelivery d ON d.TenantId=e.TenantId AND d.C4EvidenceId=e.Id
                WHERE d.BusinessDate<@Month
                """)
        ];
        var sealedRows = new List<Digest>();
        foreach (var (area, select) in queries)
        {
            string[] rows = (await db.QueryAsync<string>(new CommandDefinition($"""
                SELECT (SELECT historical.* FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)
                FROM ({select}) historical
                """, new { Cutoff = ClosedAt, Month = December }, cancellationToken: ct))).Order(StringComparer.Ordinal).ToArray();
            string sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(rows))));
            sealedRows.Add(new Digest(area, rows.Length, sha));
        }
        return sealedRows.ToArray();
    }
}
