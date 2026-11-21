using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Xunit;

namespace FmcgDms.Api.Tests;

[CollectionDefinition("Ac21SqlScratch", DisableParallelization = true)]
public sealed class Ac21SqlScratchCollection : ICollectionFixture<SqlScratchFixture> { }

[Collection("Ac21SqlScratch")]
public sealed class Ac21CashAllocationTests(SqlScratchFixture fixture)
{
    [Theory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public Task AC21_confirmed_receipt_allocations_and_cash_handover(string tenant) =>
        new App01e2CrossCommandRaceTests(fixture).VerifyAc21Async(tenant);
}

public sealed partial class App01e2CrossCommandRaceTests
{
    private sealed record Ac21Balances(decimal Ar, decimal Credit, decimal Custody, decimal Fund, decimal Allocated);

    internal async Task VerifyAc21Async(string tenant)
    {
        const string allocatePath = D + "allocate-cash-receipt";
        byte[] oracleBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "golden-oracles.json"));
        using var oracle = JsonDocument.Parse(oracleBytes);
        var expected = oracle.RootElement.GetProperty("AC-21").GetProperty("steps").EnumerateArray().ToArray();
        string peer = tenant == "tenant-a" ? "tenant-b" : "tenant-a";
        var peerBefore = await FullSql(peer);
        using var factory = fixture.CreateFactory();
        using var setup = SqlScratchFixture.WithTenant(factory.CreateClient(), tenant);
        Scene scene = Seed(tenant);
        await Prepare(setup, tenant, scene, load: true);
        var (receipt, item) = SeedAc21ConfirmedOpening(tenant, scene);
        using var sales = Client(factory, tenant, SalesActor, "sales");
        using var cashier = Client(factory, tenant, CashierActor, "cashier");
        var checkpoints = new List<object>();

        void Checkpoint(string stage)
        {
            var row = expected[checkpoints.Count];
            Assert.Equal(row[0].GetString(), stage);
            using var db = Open(tenant);
            var balances = db.QuerySingle<Ac21Balances>("""
                SELECT
                    (SELECT COALESCE(SUM(e.AmountDelta),0)
                       FROM dbo.LedgerEffect e
                       JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId AND l.Id=e.BusinessSourceLineId
                       JOIN dbo.BusinessSource s ON s.TenantId=l.TenantId AND s.Id=l.BusinessSourceId
                      WHERE s.TenantId=@Tenant AND s.TripId=@Trip AND e.LedgerKind='AR') AS Ar,
                    (SELECT ReceivedAmount-AppliedAmount FROM dbo.CashReceipt
                      WHERE TenantId=@Tenant AND Id=@Receipt) AS Credit,
                    (SELECT COALESCE(SUM(e.AmountDelta),0)
                       FROM dbo.LedgerEffect e
                       JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId AND l.Id=e.BusinessSourceLineId
                       JOIN dbo.BusinessSource s ON s.TenantId=l.TenantId AND s.Id=l.BusinessSourceId
                      WHERE s.TenantId=@Tenant AND s.TripId=@Trip AND e.LedgerKind='CUSTODY') AS Custody,
                    (SELECT COALESCE(SUM(e.AmountDelta),0)
                       FROM dbo.LedgerEffect e
                       JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId AND l.Id=e.BusinessSourceLineId
                       JOIN dbo.BusinessSource s ON s.TenantId=l.TenantId AND s.Id=l.BusinessSourceId
                      WHERE s.TenantId=@Tenant AND s.TripId=@Trip AND e.LedgerKind='FUND') AS Fund,
                    (SELECT COALESCE(SUM(AppliedAmount),0) FROM dbo.ReceiptAllocation
                      WHERE TenantId=@Tenant AND CashReceiptId=@Receipt) AS Allocated
                """, new { Tenant = tenant, scene.Trip, Receipt = receipt });
            decimal openItem = db.QuerySingle<decimal>("""
                SELECT OutstandingAmount FROM dbo.ReceivableOpenItem WHERE TenantId=@Tenant AND Id=@Item
                """, new { Tenant = tenant, Item = item });
            Assert.Equal(balances.Ar, openItem);
            Cell[] cells =
            [
                new("ar", row[1].GetDecimal(), balances.Ar),
                new("credit", row[2].GetDecimal(), balances.Credit),
                new("custody", row[3].GetDecimal(), balances.Custody),
                new("fund", row[4].GetDecimal(), balances.Fund),
                new("allocated", row[5].GetDecimal(), balances.Allocated)
            ];
            Assert.All(cells, cell => Assert.True(cell.Match, $"{stage}/{cell.Field}: {cell.Expected} != {cell.Actual}"));
            checkpoints.Add(new { Stage = stage, CapturedAtUtc = DateTimeOffset.UtcNow, Cells = cells });
        }

        var executed = new List<(HttpClient Client, Request Request, Reply Reply)>();
        async Task<Guid> Post(HttpClient client, Request request, string outcome, string stage)
        {
            var reply = await Send(client, request.Path, request.Payload);
            Assert.Equal(outcome, reply.Outcome.OutcomeCode);
            AssertCommandFromSql(tenant, scene, request, reply, Audit(tenant, request.Payload));
            executed.Add((client, request, reply));
            Checkpoint(stage);
            return reply.Outcome.SourceId!.Value;
        }

        Checkpoint("CONFIRMED");
        var first = new AllocateCashReceiptCommand("ac21", Id(), Id(), scene.Trip, receipt, item, 20000m);
        await Post(sales, Req(allocatePath, first, "sales", SalesActor, "CASH_RECEIPT_ALLOCATION", 1, 1),
            "CASH_RECEIPT_ALLOCATED", "ALLOC_20K");
        var second = first with { CommandId = Id(), SourceNumber = Id(), Amount = 15000m };
        await Post(sales, Req(allocatePath, second, "sales", SalesActor, "CASH_RECEIPT_ALLOCATION", 1, 1),
            "CASH_RECEIPT_ALLOCATED", "ALLOC_15K");
        var offer = await Post(sales, Req(D + "offer-cash-handover",
            new OfferCashHandoverCommand("ac21", Id(), Id(), scene.Trip, 50000m),
            "sales", SalesActor, "CASH_HANDOVER_OFFER", 1, 0), "CASH_HANDOVER_OFFERED", "OFFER_50K");

        string count = Id();
        using (var db = Open(tenant))
            db.Execute("""
                INSERT dbo.CashierCountEvidence(TenantId,EvidenceRef,TripId,CountedAmount,
                    CountedBy,VerificationStatus,CountedAtUtc)
                VALUES(@Tenant,@Evidence,@Trip,50000,@Cashier,N'VERIFIED',SYSUTCDATETIME())
                """, new { Tenant = tenant, Evidence = count, scene.Trip, Cashier = CashierActor });
        await Post(cashier, Req(D + "accept-cash-handover",
            new AcceptCashHandoverCommand("ac21", Id(), Id(), scene.Trip, offer, count),
            "cashier", CashierActor, "CASH_HANDOVER_ACCEPT", 1, 2), "CASH_HANDOVER_ACCEPTED", "ACCEPT_50K");

        var beforeReplay = await FullSql(tenant);
        var replays = new List<object>();
        foreach (var (client, request, original) in executed)
        {
            var replay = await Send(client, request.Path, request.Payload);
            Assert.Equal(original.HttpStatus, replay.HttpStatus);
            Assert.Equal(original.Outcome with { Replayed = true }, replay.Outcome);
            var afterReplay = await FullSql(tenant);
            Assert.Equal(beforeReplay, afterReplay);
            replays.Add(new { request.Payload.CommandId, replay.Outcome, Sql = afterReplay });
        }

        var conflict = await Send(sales, allocatePath, first with { Amount = 20001m });
        Assert.Equal(HttpStatusCode.Conflict, conflict.HttpStatus);
        Assert.Equal(beforeReplay, await FullSql(tenant));

        using var forbidden = await cashier.PostAsJsonAsync(allocatePath, first, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(beforeReplay, await FullSql(tenant));

        var excess = first with { CommandId = Id(), SourceNumber = Id(), Amount = 25001m };
        var excessReply = await Send(sales, allocatePath, excess);
        Assert.Equal("RECEIPT_UNAPPLIED_INSUFFICIENT", excessReply.Outcome.ReasonCode);
        var rejected = Audit(tenant, excess);
        AssertCommandFromSql(tenant, scene,
            Req(allocatePath, excess, "sales", SalesActor, "CASH_RECEIPT_ALLOCATION", 0, 0), excessReply, rejected);
        var peerAfter = await FullSql(peer);
        Assert.Equal(peerBefore, peerAfter);

        using var peerSales = Client(factory, peer, SalesActor, "sales");
        var foreign = first with { CommandId = Id(), SourceNumber = Id() };
        var foreignReply = await Send(peerSales, allocatePath, foreign);
        Assert.Equal("ALLOCATION_SCOPE_INVALID", foreignReply.Outcome.ReasonCode);
        var foreignAudit = Audit(peer, foreign);
        Assert.Equal((0, 0), (foreignAudit.Lines, foreignAudit.Effects));

        using var connection = Open(tenant);
        var allocations = connection.Query("""
            SELECT s.SourceNumber,s.Id AS SourceId,a.AllocationSourceLineId AS SourceLineId,
                e.Id AS EffectId,a.CashReceiptId,a.ReceivableOpenItemId,a.AppliedAmount,
                e.LedgerKind,e.AmountDelta
            FROM dbo.ReceiptAllocation a
            JOIN dbo.BusinessSourceLine l ON l.TenantId=a.TenantId AND l.Id=a.AllocationSourceLineId
            JOIN dbo.BusinessSource s ON s.TenantId=l.TenantId AND s.Id=l.BusinessSourceId
            JOIN dbo.LedgerEffect e ON e.TenantId=l.TenantId AND e.BusinessSourceLineId=l.Id
            WHERE a.TenantId=@Tenant AND a.CashReceiptId=@Receipt
            ORDER BY a.AppliedAmount DESC;
            """, new { Tenant = tenant, Receipt = receipt }).ToArray();
        Assert.Equal(new[] { 20000m, 15000m }, allocations.Select(a => (decimal)a.AppliedAmount));
        Assert.All(allocations, a =>
        {
            Assert.Equal("AR", (string)a.LedgerKind);
            Assert.Equal(-(decimal)a.AppliedAmount, (decimal)a.AmountDelta);
            Assert.Equal(item, (Guid)a.ReceivableOpenItemId);
        });

        var report = new
        {
            Case = "AC-21", Tenant = tenant, Scope = "ConfirmedOpeningFixtureThenSyntheticHttp",
            Environment = "SYNTHETIC_NONPRODUCTION", PhysicalPreprod = "NOT_VERIFIED",
            EngineVersion = connection.QuerySingle<string>("SELECT CONVERT(varchar(32), SERVERPROPERTY('ProductVersion'))"),
            TestRunId = Environment.GetEnvironmentVariable("AC21_TEST_RUN_ID"),
            Source = Environment.GetEnvironmentVariable("AC21_SOURCE"), CapturedAtUtc = DateTimeOffset.UtcNow,
            OracleSha256 = Convert.ToHexString(SHA256.HashData(oracleBytes)), ReceiptId = receipt, OpenItemId = item,
            Checkpoints = checkpoints, Commands = executed.Select(e => new { e.Request.Path, e.Request.Payload, e.Reply.Outcome }),
            Allocations = allocations, Replay = new { Before = beforeReplay, Attempts = replays },
            Conflict = conflict.Outcome, WrongRoleHttp = (int)forbidden.StatusCode,
            Excess = new { excessReply.Outcome, Audit = rejected },
            ForeignTenant = new { foreignReply.Outcome, Audit = foreignAudit },
            Peer = new { Before = peerBefore, After = peerAfter }
        };
        var output = Path.Combine(AppContext.BaseDirectory, "TestResults", "AC21");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, $"{tenant}.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private (Guid Receipt, Guid Item) SeedAc21ConfirmedOpening(string tenant, Scene scene)
    {
        string evidence = CashWitness(tenant, scene, 60000m);
        Guid receipt = Guid.NewGuid(), item = Guid.NewGuid();
        using var db = Open(tenant);
        db.Open();
        using var tx = db.BeginTransaction();
        db.Execute("""
            INSERT dbo.BusinessCommand(Id,TenantId,ProducerId,CommandId,PayloadHashVersion,
                PayloadHash,ActorId,Status,OutcomeCode,CompletedAtUtc)
            VALUES(@Command,@Tenant,N'ac21-opening-fixture',@Number,1,@Hash,N'ac21-fixture',
                N'APPLIED',N'CONFIRMED_OPENING',SYSUTCDATETIME());
            INSERT dbo.BusinessSource(Id,TenantId,BusinessCommandId,SourceKind,SourceNumber,
                ActorId,CustomerId,TripId,OccurredAtUtc)
            VALUES(@Source,@Tenant,@Command,N'B_SALE_DELIVERY',@Number,N'ac21-fixture',@Customer,@Trip,SYSUTCDATETIME());
            INSERT dbo.BusinessSourceLine(Id,TenantId,BusinessSourceId,LineNumber,SkuId,BaseQuantity,Amount)
            VALUES(@Line,@Tenant,@Source,1,@Sku,1,140000);
            INSERT dbo.SaleDelivery(Id,TenantId,TripId,CustomerId,ShipToSnapshotId,TripManifestLineId,
                LoadSourceLineId,StockRightId,RightKind,SkuId,BusinessSourceId,BusinessSourceKind,
                BusinessSourceLineId,BaseQuantity,PayableAmount,CostAmount,CostBasisVersion,BusinessDate)
            SELECT @Delivery,m.TenantId,m.TripId,@Customer,@ShipTo,m.Id,m.LoadSourceLineId,m.StockRightId,
                'B',m.SkuId,@Source,N'B_SALE_DELIVERY',@Line,1,140000,0,N'SYNTHETIC_MA_V1',t.BusinessDate
            FROM dbo.TripManifestLine m
            JOIN dbo.Trip t ON t.TenantId=m.TenantId AND t.Id=m.TripId
            WHERE m.TenantId=@Tenant AND m.Id=@ManifestLine;
            INSERT dbo.ReceivableOpenItem(Id,TenantId,SaleDeliveryId,CustomerId,BusinessSourceLineId,RightKind,OriginalAmount)
            VALUES(@Item,@Tenant,@Delivery,@Customer,@Line,'B',140000);
            INSERT dbo.CashReceipt(Id,TenantId,SaleDeliveryId,TripId,CustomerId,BusinessSourceLineId,
                VerificationEvidenceRef,ReceivedAmount,VerifiedAtUtc)
            SELECT @Receipt,@Tenant,@Delivery,@Trip,@Customer,@Line,EvidenceRef,VerifiedAmount,VerifiedAtUtc
            FROM dbo.VerifiedCashEvidence WHERE TenantId=@Tenant AND EvidenceRef=@Evidence;
            INSERT dbo.LedgerEffect(Id,TenantId,BusinessSourceLineId,EffectKey,LedgerKind,CustomerId,QuantityDelta,AmountDelta)
            VALUES(NEWID(),@Tenant,@Line,N'AR-OPENING',N'AR',@Customer,0,140000),
                  (NEWID(),@Tenant,@Line,N'CASH-OPENING',N'CUSTODY',@Customer,0,60000);
            """, new { Tenant = tenant, scene.Customer, scene.Trip, scene.ShipTo, scene.Sku,
                ManifestLine = scene.BManifestLine, Receipt = receipt, Item = item, Evidence = evidence,
                Command = Guid.NewGuid(), Number = Id(), Source = Guid.NewGuid(), Line = Guid.NewGuid(),
                Delivery = Guid.NewGuid(), Hash = SHA256.HashData(Guid.NewGuid().ToByteArray()) }, tx);
        tx.Commit();
        return (receipt, item);
    }
}
