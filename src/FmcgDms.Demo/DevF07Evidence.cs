using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal static class DevF07Evidence
{
    private const string Sales = "c1-dev-sales";
    private const string Returned = "C2-FIXTURE";
    private const string Waived = "F07-WAIVER";
    private static readonly string[] Phases = ["issued", "returned-waived", "repurchased"];
    internal sealed record Position(string Customer, decimal Purchased, decimal Accepted, int Issued, int GiftReturned,
        int ApprovedWaiver, int WaivedHeld);
    internal sealed record Stock(string Location, decimal Quantity, decimal Cost);
    internal sealed record Checkpoint(string Phase, Position[] Positions, Stock[] Stock);
    private sealed record GiftSource(Guid Id, string Kind, string Customer, string Actor, Guid? Related, int Quantity,
        long Cost, string? Reason, string? Evidence, string CommandStatus);
    private sealed record Effect(Guid SourceId, string Key, string Ledger, string? Location, string? Sku,
        decimal Quantity, decimal Amount);

    internal static string CheckpointDirectory(string root, DemoRun run) =>
        Path.Combine(root, "TestResults", "dev-f07", run.Id, "checkpoints");

    internal static async Task Seed(string connection, string tenant, string actor, CancellationToken ct)
    {
        await using var db = new SqlConnection(connection);
        await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(ct);
        await db.ExecuteAsync(new CommandDefinition("""
            DECLARE @purchase uniqueidentifier=NEWID(), @lot uniqueidentifier=NEWID(), @gift uniqueidentifier=NEWID(), @program uniqueidentifier=NEWID(),
                @giftWarehouse uniqueidentifier=NEWID(), @giftQa uniqueidentifier=NEWID(), @waived uniqueidentifier=NEWID(),
                @waivedPlan uniqueidentifier=NEWID(), @returnedQuote uniqueidentifier=NEWID(), @waivedQuote uniqueidentifier=NEWID(),
                @returned uniqueidentifier, @returnedPlan uniqueidentifier, @main uniqueidentifier, @truck uniqueidentifier,
                @purchaseQa uniqueidentifier;
            SELECT @returned=Id FROM dbo.Customer WHERE TenantId=@Tenant AND Code=N'C2-FIXTURE';
            SELECT @returnedPlan=Id FROM dbo.F04PricePlan WHERE TenantId=@Tenant AND CustomerId=@returned AND Version=1;
            SELECT @main=Id FROM dbo.StockLocation WHERE TenantId=@Tenant AND Code=N'C2-MAIN';
            SELECT @truck=Id FROM dbo.StockLocation WHERE TenantId=@Tenant AND Code=N'C2-TRUCK';
            SELECT @purchaseQa=Id FROM dbo.StockLocation WHERE TenantId=@Tenant AND Code=N'F05-QA';

            INSERT dbo.App02ActorRoleGrant(TenantId,ActorId,RoleCode,IsActive,ChangedBy)
                VALUES(@Tenant,@Actor,N'finance',1,N'f07-dev-launcher');
            INSERT dbo.Customer(Id,TenantId,Code,Name)
                VALUES(@waived,@Tenant,N'F07-WAIVER',N'Khách giữ quà được miễn thu hồi');
            INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom)
                VALUES(@purchase,@Tenant,N'G-BUY',N'Hàng mua đủ điều kiện nhận quà',N'EA'),
                      (@gift,@Tenant,N'G-GIFT',N'Quà DEV',N'EA');
            INSERT dbo.StockLocation(Id,TenantId,Code,Kind)
                VALUES(@giftWarehouse,@Tenant,N'F07-GIFT',N'WAREHOUSE'),(@giftQa,@Tenant,N'F07-GIFT-QA',N'QUARANTINE');
            INSERT dbo.F05QaScope(TenantId,ActorId,WarehouseId) VALUES(@Tenant,@Receiver,@giftWarehouse);
            INSERT dbo.StockPool(TenantId,StockLocationId,SkuId,OnHandBaseQuantity)
                VALUES(@Tenant,@main,@purchase,100);
            INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,SnapshotCode,BaseQuantity,CostAmount)
                VALUES(@Tenant,@main,@purchase,N'F07-v1',100,100000),(@Tenant,@truck,@purchase,N'F07-v1',0,0),
                      (@Tenant,@purchaseQa,@purchase,N'F07-v1',0,0),(@Tenant,@giftWarehouse,@gift,N'F07-v1',10,1000),
                      (@Tenant,@giftQa,@gift,N'F07-v1',0,0);
            INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,OnHandBaseQuantity,OnHandCostAmount)
                VALUES(@Tenant,@main,@purchase,100,100000),(@Tenant,@truck,@purchase,0,0),(@Tenant,@purchaseQa,@purchase,0,0),
                      (@Tenant,@giftWarehouse,@gift,10,1000),(@Tenant,@giftQa,@gift,0,0);
            INSERT dbo.F03Unit(TenantId,SkuId,UomCode,Factor,[Step]) VALUES(@Tenant,@purchase,N'EA',1,1);
            INSERT dbo.F03Lot(TenantId,Id,SkuId,Code,ExpiresOn)
                VALUES(@Tenant,@lot,@purchase,N'BUY-ORIGIN','20271231');
            INSERT dbo.F03LotOpening(TenantId,LotId,LocationId,Quantity) VALUES(@Tenant,@lot,@main,100);

            INSERT dbo.C2CommercialPolicy(TenantId,Id,CustomerId,SkuId,Version,Decision,EffectiveFromUtc,EffectiveUntilUtc,
                Maker,Checker,ApprovedAtUtc,EvidenceRef,UnitPrice,DiscountPerUnit,TaxRate,TermsDays,CreditCap,SellerTimeZone)
                VALUES(@Tenant,@returnedQuote,@returned,@purchase,1,'APPROVED',@From,@Until,N'f07-maker',N'f07-checker',
                        @From,N'F07-returned-quote-v1',10000,0,0,7,400000,'Asia/Ho_Chi_Minh'),
                      (@Tenant,@waivedQuote,@waived,@purchase,1,'APPROVED',@From,@Until,N'f07-maker',N'f07-checker',
                        @From,N'F07-waived-quote-v1',10000,0,0,7,400000,'Asia/Ho_Chi_Minh');
            INSERT dbo.F04PricePlan(TenantId,Id,CustomerId,Version,TaxMode,OrderDiscount,
                EffectiveFromUtc,EffectiveUntilUtc,Maker,Checker,ApprovedAtUtc,EvidenceRef)
                VALUES(@Tenant,@waivedPlan,@waived,1,'EXCLUSIVE',0,@From,@Until,N'f07-maker',N'f07-checker',@From,N'F07-waived-v1');
            INSERT dbo.F04PlanQuote(TenantId,PlanId,PolicyId)
                VALUES(@Tenant,@returnedPlan,@returnedQuote),(@Tenant,@waivedPlan,@waivedQuote);
            INSERT dbo.F07Program(TenantId,Id,PurchaseSkuId,GiftSkuId,GiftLocationId,QuarantineLocationId,
                PurchaseThreshold,GiftsPerThreshold)
                VALUES(@Tenant,@program,@purchase,@gift,@giftWarehouse,@giftQa,10,1);
            INSERT dbo.F07ProgramGrant(TenantId,ProgramId,ActorId,RoleCode)
                VALUES(@Tenant,@program,@Actor,N'sales'),(@Tenant,@program,@Actor,N'finance'),
                      (@Tenant,@program,@Finance,N'finance');
            """, new { Tenant = tenant, Actor = actor, DevF05Evidence.Receiver, DevF05Evidence.Finance, From = DevF05Evidence.FixtureUtc.AddHours(-1),
                Until = DevF05Evidence.FixtureUtc.AddDays(1) }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    internal static async Task<Checkpoint[]> ServeCheckpoints(DemoRun run, string root, Process browser, CancellationToken ct)
    {
        string directory = CheckpointDirectory(root, run);
        Directory.CreateDirectory(directory);
        var observations = new List<Checkpoint>();
        foreach (string phase in Phases)
        {
            string request = Path.Combine(directory, phase + ".request");
            while (!File.Exists(request))
            {
                if (browser.HasExited) return [.. observations];
                await Task.Delay(75, ct);
            }
            if (await File.ReadAllTextAsync(request, ct) != run.Id)
                throw new InvalidDataException("DEV-F07 checkpoint has the wrong run ID.");
            var observation = await ReadSnapshot(run, "tenant-a", phase, ct);
            observations.Add(observation);
            await File.WriteAllTextAsync(Path.Combine(directory, phase + ".json"), JsonSerializer.Serialize(observation), ct);
            string ready = Path.Combine(directory, phase + ".ready");
            await File.WriteAllTextAsync(ready + ".partial", run.Id, ct);
            File.Move(ready + ".partial", ready);
        }
        return [.. observations];
    }

    private static async Task<Checkpoint> ReadSnapshot(DemoRun run, string tenant, string phase, CancellationToken ct)
    {
        await using var db = new SqlConnection(run.Connection(tenant));
        using var rows = await db.QueryMultipleAsync(new CommandDefinition("""
            SELECT c.Code AS Customer,
                COALESCE((SELECT SUM(d.BaseQuantity) FROM dbo.SaleDelivery d
                    WHERE d.TenantId=c.TenantId AND d.CustomerId=c.Id AND d.SkuId=p.PurchaseSkuId AND d.RightKind='A'),0) AS Purchased,
                COALESCE((SELECT SUM(r.Quantity) FROM dbo.F05ReturnCase r
                    JOIN dbo.F05ReturnStage s ON s.TenantId=r.TenantId AND s.CaseId=r.Id AND s.Stage='RELEASE'
                    JOIN dbo.SaleDelivery d ON d.TenantId=r.TenantId AND d.Id=r.DeliveryId
                    JOIN dbo.F03LotIssue l ON l.TenantId=r.TenantId AND l.DeliveryId=d.Id AND l.LotId=r.LotId
                    WHERE d.TenantId=c.TenantId AND d.CustomerId=c.Id AND d.SkuId=p.PurchaseSkuId AND d.RightKind='A'),0) AS Accepted,
                COALESCE((SELECT SUM(i.Quantity) FROM dbo.F07Issue i WHERE i.TenantId=c.TenantId AND i.CustomerId=c.Id),0) AS Issued,
                COALESCE((SELECT SUM(r.Quantity) FROM dbo.F07GiftReturn r
                    JOIN dbo.F07Issue i ON i.TenantId=r.TenantId AND i.Id=r.IssueId
                    WHERE i.TenantId=c.TenantId AND i.CustomerId=c.Id),0) AS GiftReturned,
                COALESCE((SELECT SUM(w.Quantity) FROM dbo.F07Waiver w
                    JOIN dbo.F07WaiverDecision a ON a.TenantId=w.TenantId AND a.WaiverId=w.Id
                    JOIN dbo.F07Issue i ON i.TenantId=w.TenantId AND i.Id=w.IssueId
                    WHERE i.TenantId=c.TenantId AND i.CustomerId=c.Id),0) AS ApprovedWaiver,
                COALESCE((SELECT SUM(CASE WHEN w.Quantity<i.Quantity-back.Returned THEN w.Quantity ELSE i.Quantity-back.Returned END)
                    FROM dbo.F07Waiver w JOIN dbo.F07WaiverDecision a ON a.TenantId=w.TenantId AND a.WaiverId=w.Id
                    JOIN dbo.F07Issue i ON i.TenantId=w.TenantId AND i.Id=w.IssueId
                    CROSS APPLY (SELECT COALESCE(SUM(r.Quantity),0) AS Returned FROM dbo.F07GiftReturn r
                        WHERE r.TenantId=i.TenantId AND r.IssueId=i.Id) back
                    WHERE i.TenantId=c.TenantId AND i.CustomerId=c.Id),0) AS WaivedHeld
            FROM dbo.Customer c JOIN dbo.F07Program p ON p.TenantId=c.TenantId
            WHERE c.TenantId=@Tenant AND c.Code IN ('C2-FIXTURE','F07-WAIVER')
            ORDER BY c.Code;
            SELECT l.Code AS Location,b.OnHandBaseQuantity AS Quantity,b.OnHandCostAmount AS Cost
            FROM dbo.PhysicalStockBalance b
            JOIN dbo.StockLocation l ON l.TenantId=b.TenantId AND l.Id=b.StockLocationId
            JOIN dbo.F07Program p ON p.TenantId=b.TenantId AND b.SkuId IN(p.PurchaseSkuId,p.GiftSkuId)
            WHERE b.TenantId=@Tenant ORDER BY l.Code;
            """, new { Tenant = tenant }, cancellationToken: ct));
        return new(phase, (await rows.ReadAsync<Position>()).ToArray(), (await rows.ReadAsync<Stock>()).ToArray());
    }

    internal static async Task Verify(DemoRun run, string root, string source, string apiSha256,
        Checkpoint[]? checkpoints, CancellationToken ct)
    {
        await run.Verify("tenant-a");
        await run.Verify("tenant-b");
        string oraclePath = Path.Combine(root, "business", "reference-data", "expected-results", "additional-oracles.json");
        byte[] oracleBytes = await File.ReadAllBytesAsync(oraclePath, ct);
        using var reference = JsonDocument.Parse(oracleBytes);
        JsonElement oracle = reference.RootElement.GetProperty("AC-07");
        JsonElement program = oracle.GetProperty("program");
        int threshold = program.GetProperty("qualifiedPurchaseThreshold").GetInt32();
        int giftsPerThreshold = program.GetProperty("giftsPerThreshold").GetInt32();
        Checkpoint final = await ReadSnapshot(run, "tenant-a", "final", ct);
        Checkpoint peer = await ReadSnapshot(run, "tenant-b", "peer", ct);
        var observed = (checkpoints ?? []).ToDictionary(point => point.Phase);
        var expected = new List<Checkpoint>();
        foreach (string phase in Phases.Append("final"))
        {
            Position expectedPosition(string customer, string branchName)
            {
                JsonElement branch = oracle.GetProperty(branchName);
                int initialBuy = branch.GetProperty("initialPurchases").GetInt32();
                int initialGift = branch.GetProperty("initialGiftDelivered").GetInt32();
                int returnedBuy = phase == "issued" ? 0 : branch.GetProperty("purchaseReturnAccepted").GetInt32();
                int returnedGift = phase == "issued" ? 0 : branch.GetProperty("giftReturnAccepted").GetInt32();
                int waiver = phase != "issued" && branch.TryGetProperty("giftWaiverApproved", out var value) ? value.GetInt32() : 0;
                int boughtAgain = phase is "repurchased" or "final" ? branch.GetProperty("subsequentPurchase").GetInt32() : 0;
                int issuedAgain = phase == "final" ? branch.GetProperty("expectedGiftToDeliver").GetInt32() : 0;
                return new(customer, initialBuy + boughtAgain, returnedBuy, initialGift + issuedAgain, returnedGift, waiver, waiver);
            }
            Position[] positions = [expectedPosition(Returned, "branchReturned"), expectedPosition(Waived, "branchWaived")];
            decimal main = 100 - positions.Sum(row => row.Purchased - row.Accepted);
            int giftMain = 10 - positions.Sum(row => row.Issued);
            int giftQa = positions.Sum(row => row.GiftReturned);
            expected.Add(new(phase, positions, ExpectedStock(main, giftMain, giftQa)));
        }
        expected.Add(new("peer", [new(Returned, 0, 0, 0, 0, 0, 0), new(Waived, 0, 0, 0, 0, 0, 0)],
            ExpectedStock(100, 10, 0)));
        observed.Add("final", final);
        observed.Add("peer", peer);
        var phaseDiff = expected.Select(point =>
        {
            observed.TryGetValue(point.Phase, out var actual);
            return new { Expected = point, Actual = actual,
                Matches = actual is not null && actual.Positions.SequenceEqual(point.Positions) && actual.Stock.SequenceEqual(point.Stock) };
        }).ToArray();
        var entitlements = new[] { (Returned, "branchReturned"), (Waived, "branchWaived") }.Select(pair =>
        {
            var position = observed.GetValueOrDefault("repurchased")?.Positions.SingleOrDefault(row => row.Customer == pair.Item1);
            JsonElement branch = oracle.GetProperty(pair.Item2);
            decimal net = position is null ? -1 : position.Purchased - position.Accepted;
            int held = position is null ? -1 : position.Issued - position.GiftReturned;
            int available = Math.Max(0, (int)Math.Floor(net / threshold) * giftsPerThreshold - held);
            return new { Customer = pair.Item1, Net = net, Held = held, ToDeliver = available, Matches =
                net == branch.GetProperty("expectedNetPurchases").GetInt32() &&
                held == branch.GetProperty("expectedGiftHeld").GetInt32() &&
                available == branch.GetProperty("expectedGiftToDeliver").GetInt32() };
        }).ToArray();

        await using var a = new SqlConnection(run.Connection("tenant-a"));
        await using var b = new SqlConnection(run.Connection("tenant-b"));
        GiftSource[] records = (await a.QueryAsync<GiftSource>(new CommandDefinition("""
            WITH records AS (
                SELECT i.TenantId,i.Id,i.CustomerId,i.ActorId,CAST(NULL AS uniqueidentifier) AS Related,i.Quantity,i.CostAmount,
                       i.Reason,i.Evidence FROM dbo.F07Issue i
                UNION ALL SELECT r.TenantId,r.Id,i.CustomerId,r.ActorId,r.IssueId,r.Quantity,r.CostAmount,r.Reason,r.Evidence
                    FROM dbo.F07GiftReturn r JOIN dbo.F07Issue i ON i.TenantId=r.TenantId AND i.Id=r.IssueId
                UNION ALL SELECT w.TenantId,w.Id,i.CustomerId,w.ActorId,w.IssueId,w.Quantity,0,w.Reason,w.Evidence
                    FROM dbo.F07Waiver w JOIN dbo.F07Issue i ON i.TenantId=w.TenantId AND i.Id=w.IssueId
                UNION ALL SELECT a.TenantId,a.Id,i.CustomerId,a.ActorId,a.WaiverId,w.Quantity,0,a.Reason,a.Evidence
                    FROM dbo.F07WaiverDecision a JOIN dbo.F07Waiver w ON w.TenantId=a.TenantId AND w.Id=a.WaiverId
                    JOIN dbo.F07Issue i ON i.TenantId=w.TenantId AND i.Id=w.IssueId)
            SELECT r.Id,s.SourceKind AS Kind,c.Code AS Customer,r.ActorId AS Actor,r.Related,r.Quantity,r.CostAmount AS Cost,
                r.Reason,r.Evidence,b.Status AS CommandStatus
            FROM records r JOIN dbo.Customer c ON c.TenantId=r.TenantId AND c.Id=r.CustomerId
            JOIN dbo.BusinessSource s ON s.TenantId=r.TenantId AND s.Id=r.Id
            JOIN dbo.BusinessCommand b ON b.TenantId=s.TenantId AND b.Id=s.BusinessCommandId
            ORDER BY s.SourceKind,r.Id;
            """, cancellationToken: ct))).ToArray();
        Effect[] effects = (await a.QueryAsync<Effect>(new CommandDefinition("""
            SELECT s.Id AS SourceId,e.EffectKey AS [Key],e.LedgerKind AS Ledger,l.Code AS Location,k.Code AS Sku,
                e.QuantityDelta AS Quantity,e.AmountDelta AS Amount
            FROM dbo.BusinessSource s JOIN dbo.BusinessSourceLine sl ON sl.TenantId=s.TenantId AND sl.BusinessSourceId=s.Id
            JOIN dbo.LedgerEffect e ON e.TenantId=sl.TenantId AND e.BusinessSourceLineId=sl.Id
            LEFT JOIN dbo.StockLocation l ON l.TenantId=e.TenantId AND l.Id=e.StockLocationId
            LEFT JOIN dbo.Sku k ON k.TenantId=e.TenantId AND k.Id=e.SkuId
            WHERE s.SourceKind LIKE 'F07[_]%' ORDER BY s.Id,e.EffectKey;
            """, cancellationToken: ct))).ToArray();
        int CheckRecords(string kind, string customer, string actor, long cost)
            => records.Count(r => r.Kind == kind && r.Customer == customer && r.Actor == actor && r.Quantity == 2 && r.Cost == cost);
        var giftReturn = records.SingleOrDefault(r => r.Kind == "F07_RETURN");
        var waiverProposal = records.SingleOrDefault(r => r.Kind == "F07_PROPOSE");
        var waiverDecision = records.SingleOrDefault(r => r.Kind == "F07_APPROVE");
        bool recordsMatch = records.Length == 6 && records.All(r => r.CommandStatus == "APPLIED"
            && !string.IsNullOrWhiteSpace(r.Evidence) && !string.IsNullOrWhiteSpace(r.Reason))
            && CheckRecords("F07_ISSUE", Returned, Sales, 200) == 2
            && CheckRecords("F07_ISSUE", Waived, Sales, 200) == 1
            && CheckRecords("F07_RETURN", Returned, DevF05Evidence.Receiver, 200) == 1
            && CheckRecords("F07_PROPOSE", Waived, Sales, 0) == 1
            && CheckRecords("F07_APPROVE", Waived, DevF05Evidence.Finance, 0) == 1
            && records.Any(r => r.Id == giftReturn?.Related && r.Kind == "F07_ISSUE" && r.Customer == Returned)
            && records.Any(r => r.Id == waiverProposal?.Related && r.Kind == "F07_ISSUE" && r.Customer == Waived)
            && waiverProposal?.Id == waiverDecision?.Related;
        Effect[] expectedEffects = records.Where(r => r.Kind is "F07_ISSUE" or "F07_RETURN")
            .Select(r => r.Kind == "F07_ISSUE"
                ? new Effect(r.Id, "GIFT-OUT", "PHYSICAL_STOCK", "F07-GIFT", "G-GIFT", -2, -200)
                : new Effect(r.Id, "GIFT-QA-IN", "PHYSICAL_STOCK", "F07-GIFT-QA", "G-GIFT", 2, 200)).ToArray();
        bool effectsMatch = effects.Length == expectedEffects.Length && expectedEffects.All(effects.Contains);
        var sales = await a.QuerySingleAsync<(int Orders, int Deliveries, decimal Gross, int OpenItems, decimal Ar,
            int CashReceipts, int SourceCount, int ValidPurchases)>(new CommandDefinition("""
            SELECT (SELECT COUNT(*) FROM dbo.AOrder) AS Orders,
                (SELECT COUNT(*) FROM dbo.SaleDelivery) AS Deliveries,
                (SELECT COALESCE(SUM(PayableAmount),0) FROM dbo.SaleDelivery) AS Gross,
                (SELECT COUNT(*) FROM dbo.ReceivableOpenItem) AS OpenItems,
                (SELECT COALESCE(SUM(OriginalAmount),0) FROM dbo.ReceivableOpenItem) AS Ar,
                (SELECT COUNT(*) FROM dbo.CashReceipt) AS CashReceipts,
                (SELECT COUNT(*) FROM dbo.BusinessSource WHERE SourceKind LIKE 'F07[_]%') AS SourceCount,
                (SELECT COUNT(*) FROM dbo.SaleDelivery d
                    JOIN dbo.F07Program p ON p.TenantId=d.TenantId AND p.PurchaseSkuId=d.SkuId
                    WHERE d.RightKind='A' AND d.PayableAmount=d.BaseQuantity*10000) AS ValidPurchases;
            """, cancellationToken: ct));
        int peerEffects = await b.QuerySingleAsync<int>(new CommandDefinition("""
            SELECT (SELECT COUNT(*) FROM dbo.BusinessSource)+(SELECT COUNT(*) FROM dbo.LedgerEffect)
                +(SELECT COUNT(*) FROM dbo.SaleDelivery)+(SELECT COUNT(*) FROM dbo.ReceivableOpenItem)
                +(SELECT COUNT(*) FROM dbo.AOrder)+(SELECT COUNT(*) FROM dbo.CashReceipt);
            """, cancellationToken: ct));
        bool matches = phaseDiff.All(row => row.Matches) && entitlements.All(row => row.Matches) && recordsMatch &&
            effectsMatch && sales == (4, 4, 620000m, 4, 620000m, 0, 6, 4) && peerEffects == 0;
        string path = Path.Combine(root, "TestResults", "dev-f07", run.Id, "sql-result.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            runId = run.Id, source, apiSha256, oracleSha256 = Convert.ToHexString(SHA256.HashData(oracleBytes)),
            matches, phaseDiff, entitlements, records, recordsMatch, effects, expectedEffects, effectsMatch,
            sales = new { sales.Orders, sales.Deliveries, sales.Gross, sales.OpenItems, sales.Ar, sales.CashReceipts,
                sales.SourceCount, sales.ValidPurchases }, peerEffects
        }, new JsonSerializerOptions { WriteIndented = true }), ct);
        if (!matches) throw new InvalidOperationException($"DEV-F07 AC-07 SQL reconciliation failed. See {path}.");
        Console.WriteLine($"DEV-F07 AC-07 SQL PASS {path}");
    }

    private static Stock[] ExpectedStock(decimal purchaseMain, int giftMain, int giftQa) =>
    [
        new("C2-MAIN", purchaseMain, purchaseMain * 1000), new("C2-TRUCK", 0, 0), new("F05-QA", 0, 0),
        new("F07-GIFT", giftMain, giftMain * 100), new("F07-GIFT-QA", giftQa, giftQa * 100)
    ];
}
