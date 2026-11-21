using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Xunit;

namespace FmcgDms.Api.Tests;

public sealed partial class App01e2CrossCommandRaceTests
{
    private sealed record Ac09Grant(Guid Id, decimal Granted, decimal Consumed, decimal Released, decimal Recorded);
    private sealed record Ac09Posting(Guid SourceId, Guid LineId, Guid EffectId, string LedgerKind,
        string EffectKey, decimal Quantity, decimal Amount);

    [Theory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public async Task AC09_two_sales_contend_for_one_credit_grant(string tenant)
    {
        byte[] oracleBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "additional-oracles.json"));
        using var oracle = JsonDocument.Parse(oracleBytes);
        var raceSpec = oracle.RootElement.GetProperty("AC-09").GetProperty("concurrencyNegative");
        decimal opening = raceSpec.GetProperty("openingGrant").GetDecimal();
        decimal exposure = raceSpec.GetProperty("twoDistinctCommandsExposureEach").GetDecimal();
        Assert.False(raceSpec.GetProperty("bothMayCommit").GetBoolean());
        Assert.True(exposure <= opening && 2 * exposure > opening);

        string peerTenant = tenant == "tenant-a" ? "tenant-b" : "tenant-a";
        var peerBefore = await FullSql(peerTenant);
        using var factory = fixture.CreateFactory();
        using var setup = SqlScratchFixture.WithTenant(factory.CreateClient(), tenant);
        Scene scene = Seed(tenant);
        await Prepare(setup, tenant, scene, load: true);
        const decimal quantity = 10m;
        const decimal payable = 100m;
        const decimal unitCost = 10m;
        decimal cash = payable - exposure;

        Request BuildCommand(string actor) => Req(C + "post-b-sale-delivery-receipt",
            new PostBSaleDeliveryReceiptCommand("ac09-credit-race", Id(), Id(), scene.Trip,
                scene.Customer, scene.ShipTo, scene.BManifestLine, scene.Sku, quantity,
                payable / quantity, cash, CashWitness(tenant, scene, cash)),
            "sales", actor, "B_SALE_DELIVERY", 1, 7);
        Request left = BuildCommand("ac09-sales-left");
        Request right = BuildCommand("ac09-sales-right");
        Assert.NotEqual(left.Payload.CommandId, right.Payload.CommandId);

        Ac09Grant Grant()
        {
            using var db = Open(tenant);
            return db.QuerySingle<Ac09Grant>("""
                SELECT g.Id, g.GrantedAmount AS Granted, g.ConsumedAmount AS Consumed, g.ReleasedAmount AS Released,
                       (SELECT COALESCE(SUM(c.ConsumedAmount),0) FROM dbo.BGrantConsumption c
                        WHERE c.TenantId=g.TenantId AND c.BCreditGrantId=g.Id) AS Recorded
                FROM dbo.BCreditGrant g
                WHERE g.TenantId=@tenant AND g.TripId=@Trip AND g.CustomerId=@Customer
                  AND g.ShipToSnapshotId=@ShipTo AND g.PoolCode=N'B' AND g.CurrencyUnit=N'SYNTHETIC_UNITLESS'
                """, new { tenant, scene.Trip, scene.Customer, scene.ShipTo });
        }

        var grantBefore = Grant();
        var before = ReadSql(tenant, scene);
        await using var gate = await App01e2SqlContentionGate.HoldAsync(
            Connection(tenant), tenant, scene.Trip, scene.Warehouse, scene.Sku, "trip-app");
        var race = await Race(factory, tenant, gate, left, right);
        Assert.True(race.ObservedSqlWaiters >= 2);

        var sides = new[] { (Request: left, Reply: race.Left), (Request: right, Reply: race.Right) };
        var winner = Assert.Single(sides, side => side.Reply.Outcome.Status == "APPLIED");
        var loser = Assert.Single(sides, side => side.Reply.Outcome.Status == "REJECTED");
        Assert.Equal("B_SALE_DELIVERY_POSTED", winner.Reply.Outcome.OutcomeCode);
        Assert.Equal("B_CREDIT_INSUFFICIENT", loser.Reply.Outcome.ReasonCode);
        var audits = sides.Select(side => Audit(tenant, side.Request.Payload)).ToArray();
        for (int i = 0; i < sides.Length; i++)
            AssertCommandFromSql(tenant, scene, sides[i].Request, sides[i].Reply, audits[i]);

        var grantAfter = Grant();
        Assert.Equal(grantBefore.Id, grantAfter.Id);
        var after = ReadSql(tenant, scene);
        var cells = new Cell[]
        {
            new("OpeningGrant", opening, grantBefore.Granted),
            new("OpeningConsumed", 0, grantBefore.Consumed),
            new("OpeningReleased", 0, grantBefore.Released),
            new("OpeningRecordedConsumption", 0, grantBefore.Recorded),
            new("GrantAfter", opening, grantAfter.Granted),
            new("GrantConsumed", exposure, grantAfter.Consumed),
            new("GrantReleased", 0, grantAfter.Released),
            new("GrantRemaining", opening - exposure, grantAfter.Granted - grantAfter.Consumed - grantAfter.Released),
            new("ConsumptionRowsAmount", exposure, grantAfter.Recorded),
            new("AccountsReceivableDelta", exposure, after.Numbers.ArB - before.Numbers.ArB),
            new("CashCustodyDelta", cash, after.Numbers.Custody - before.Numbers.Custody),
            new("FundDelta", 0, after.Numbers.Fund - before.Numbers.Fund),
            new("BQuotaConsumedDelta", quantity, after.Numbers.BConsumed - before.Numbers.BConsumed),
            new("VehicleQuantityDelta", -quantity, after.Numbers.VehicleQty - before.Numbers.VehicleQty),
            new("VehicleCostDelta", -quantity * unitCost, after.Numbers.VehicleCost - before.Numbers.VehicleCost),
            new("CogsDelta", quantity * unitCost, after.Numbers.Cogs - before.Numbers.Cogs),
            new("BDeliveryCountDelta", 1, after.Numbers.BRows - before.Numbers.BRows),
            new("ReceiptAppliedDelta", cash, after.Numbers.ReceiptApplied - before.Numbers.ReceiptApplied)
        };
        Assert.All(cells, cell => Assert.True(cell.Match, $"{cell.Field}: {cell.Expected} != {cell.Actual}"));

        Ac09Posting[] postings;
        using (var db = Open(tenant))
            postings = db.Query<Ac09Posting>("""
                SELECT s.Id AS SourceId,l.Id AS LineId,e.Id AS EffectId,e.LedgerKind,e.EffectKey,
                       e.QuantityDelta AS Quantity,e.AmountDelta AS Amount
                FROM dbo.BusinessSource s
                JOIN dbo.BusinessSourceLine l ON s.TenantId=l.TenantId AND s.Id=l.BusinessSourceId
                JOIN dbo.LedgerEffect e ON l.TenantId=e.TenantId AND l.Id=e.BusinessSourceLineId
                WHERE s.TenantId=@tenant AND s.TripId=@Trip AND s.Id=@source
                ORDER BY e.EffectKey
                """, new { tenant, scene.Trip, source = winner.Reply.Outcome.SourceId }).ToArray();
        Assert.Equal(7, postings.Length);

        var fingerprint = await FullSql(tenant);
        var replayFingerprints = new List<object>();
        foreach (var (request, reply) in sides)
        {
            using var replayClient = Client(factory, tenant, request.Actor, request.Role);
            var replay = await Send(replayClient, request.Path, request.Payload);
            Assert.Equal(reply.HttpStatus, replay.HttpStatus);
            Assert.Equal(reply.Outcome with { Replayed = true }, replay.Outcome);
            var afterReplay = await FullSql(tenant);
            Assert.Equal(fingerprint, afterReplay);
            replayFingerprints.Add(new { request.Payload.CommandId, replay.Outcome, After = afterReplay });
        }
        var peerAfter = await FullSql(peerTenant);
        Assert.Equal(peerBefore, peerAfter);

        var report = new
        {
            Case = "AC-09", Scope = "concurrencyNegative", Environment = "SYNTHETIC_NONPRODUCTION",
            DatabaseEngine = "Windows SQL Server LocalDB", PhysicalPreprod = "NOT_VERIFIED",
            TestRunId = System.Environment.GetEnvironmentVariable("AC09_TEST_RUN_ID") ?? "LOCAL_UNTRACKED",
            Source = System.Environment.GetEnvironmentVariable("AC09_SOURCE"),
            OracleSha256 = Convert.ToHexString(SHA256.HashData(oracleBytes)),
            CapturedAtUtc = DateTimeOffset.UtcNow, Tenant = tenant, Database = after.Database,
            Inputs = new { OpeningGrant = opening, ExposureEach = exposure, PayableEach = payable, CashEach = cash },
            ArrivedSqlWaiters = race.ObservedSqlWaiters,
            Commands = sides.Select((side, i) => new { Input = side.Request.Payload, side.Reply.Outcome, Audit = audits[i] }),
            GrantBefore = grantBefore, GrantAfter = grantAfter, Cells = cells, Postings = postings,
            Replay = new { Before = fingerprint, Attempts = replayFingerprints, Unchanged = true },
            Peer = new { Tenant = peerTenant, Before = peerBefore, After = peerAfter, Unchanged = true },
            Status = "PASS"
        };
        var directory = Path.Combine(AppContext.BaseDirectory, "TestResults", "AC09");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{tenant}-credit-race.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
