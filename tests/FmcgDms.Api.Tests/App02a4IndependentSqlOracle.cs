using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FmcgDms.Api.Tests;

/// <summary>
/// Independent APP-02A.4 SQL oracle. Only the synthetic scene/HTTP drivers
/// are reused from A.3; all outcome numbers, constraints and hashes below
/// come from persisted SQL, not worker return values or intake responses.
/// </summary>
public sealed partial class App01e2CrossCommandRaceTests
{
    private static readonly string[] A4Columns =
    [
        "PReceipts", "CReceipts", "Pending", "Applied", "CaseState",
        "Rejected", "Resolutions", "Applications", "Acceptances",
        "BoundSources", "CustodyLegs", "CustodyAmount", "FundLegs",
        "FundAmount", "OfferEffects", "Outbox", "Consumers", "Acked",
        "Retries", "CaseRows", "Dispositions", "Corrections",
        "CorrectionEffects", "BrokenLinks"
    ];

    private sealed record A4Cell(string Metric, decimal Expected, decimal Actual,
        decimal Difference, bool Match);
    private sealed record A4Checkpoint(string Phase, Guid TripId,
        string Database, string SqlSha256, int SqlTables, int SqlRows,
        string[] SqlStatusHistory, A4Cell[] Cells);
    private sealed class A4Report
    {
        public string Schema { get; init; } = "APP02A4_SQL_ORACLE_V1";
        public string Status { get; set; } = "FAIL";
        public string? Failure { get; set; }
        public string Group { get; init; } = "";
        public string TenantId { get; init; } = "";
        public string Database { get; set; } = "";
        public string PeerDatabase { get; set; } = "";
        public string PeerBeforeSha256 { get; set; } = "";
        public string PeerAfterSha256 { get; set; } = "";
        public string TestRunId { get; init; } = "";
        public string SourceHeadSha { get; init; } = "";
        public string WorkflowSha { get; init; } = "";
        public string OracleSha256 { get; init; } = "";
        public List<A4Checkpoint> Checkpoints { get; } = [];
        public int CheckedCells => Checkpoints.Sum(x => x.Cells.Length);
        public int MatchedCells => Checkpoints.Sum(x => x.Cells.Count(y => y.Match));
    }

    private sealed class A4SqlFacts
    {
        public int PReceipts { get; set; }
        public int CReceipts { get; set; }
        public int Pending { get; set; }
        public int Applied { get; set; }
        public int CaseState { get; set; }
        public int Rejected { get; set; }
        public int Resolutions { get; set; }
        public int Applications { get; set; }
        public int Acceptances { get; set; }
        public int BoundSources { get; set; }
        public int CustodyLegs { get; set; }
        public decimal CustodyAmount { get; set; }
        public int FundLegs { get; set; }
        public decimal FundAmount { get; set; }
        public int OfferEffects { get; set; }
        public int Outbox { get; set; }
        public int Consumers { get; set; }
        public int Acked { get; set; }
        public int Retries { get; set; }
        public int CaseRows { get; set; }
        public int Dispositions { get; set; }
        public int Corrections { get; set; }
        public int CorrectionEffects { get; set; }
        public int BrokenLinks { get; set; }

        public decimal[] Values() =>
        [
            PReceipts, CReceipts, Pending, Applied, CaseState, Rejected,
            Resolutions, Applications, Acceptances, BoundSources, CustodyLegs,
            CustodyAmount, FundLegs, FundAmount, OfferEffects, Outbox,
            Consumers, Acked, Retries, CaseRows, Dispositions, Corrections,
            CorrectionEffects, BrokenLinks
        ];
    }

    private sealed record A4Session(string Tenant, TestApiFactory Factory,
        HttpClient Setup, HttpClient Sales, HttpClient Cashier, HttpClient Maker,
        HttpClient Checker, Scene Scene, Guid Offer, string Count,
        App02EventInput P, App02EventInput C, A4Report Report);

    private static string A4FixturePath() =>
        Path.Combine(AppContext.BaseDirectory, "app02a4-expected.json");

    private static (string[] Phases, JsonElement Group) A4Expected(string group)
    {
        using var fixtureDoc = JsonDocument.Parse(File.ReadAllBytes(A4FixturePath()));
        JsonElement root = fixtureDoc.RootElement;
        Assert.Equal("APP02A4_EXPECTED_V1", root.GetProperty("schema").GetString());
        Assert.Equal("SYNTHETIC_NONPRODUCTION", root.GetProperty("scope").GetString());
        Assert.Equal("SYNTHETIC_UNITLESS", root.GetProperty("unit").GetString());
        Assert.False(root.GetProperty("invoiceEligible").GetBoolean());
        Assert.Equal("200.0000", root.GetProperty("amount").GetString());
        Assert.Equal(A4Columns, root.GetProperty("columns")
            .EnumerateArray().Select(x => x.GetString()).ToArray());
        JsonElement groupDef = root.GetProperty("groups").GetProperty(group).Clone();
        return (groupDef.EnumerateObject().Select(x => x.Name).ToArray(), groupDef);
    }

    private A4SqlFacts A4ReadSql(string tenant, Guid trip)
    {
        using SqlConnection db = Open(tenant);
        // Independent SQL observations, deliberately avoiding A.3 repository
        // responses, in-memory state, and its existing assertion helpers.
        A4SqlFacts facts = db.QuerySingle<A4SqlFacts>("""
            SELECT
              (SELECT COUNT(*) FROM dbo.App02EventReceipt r
                WHERE r.TenantId=@Tenant AND r.TripId=@Trip
                  AND r.EventKind=N'CashHandoverOffered') PReceipts,
              (SELECT COUNT(*) FROM dbo.App02EventReceipt r
                WHERE r.TenantId=@Tenant AND r.TripId=@Trip
                  AND r.EventKind=N'CashierAcceptanceRequested') CReceipts,
              (SELECT COUNT(*) FROM dbo.App02EventProgress p
                JOIN dbo.App02EventReceipt r ON r.TenantId=p.TenantId
                  AND r.ReceiptId=p.ReceiptId
                WHERE r.TenantId=@Tenant AND r.TripId=@Trip
                  AND r.EventKind=N'CashierAcceptanceRequested'
                  AND p.Status=N'PENDING_DEPENDENCY') Pending,
              (SELECT COUNT(*) FROM dbo.App02EventProgress p
                JOIN dbo.App02EventReceipt r ON r.TenantId=p.TenantId
                  AND r.ReceiptId=p.ReceiptId
                WHERE r.TenantId=@Tenant AND r.TripId=@Trip
                  AND r.EventKind=N'CashierAcceptanceRequested'
                  AND p.Status=N'APPLIED') Applied,
              (SELECT COUNT(*) FROM dbo.App02EventProgress p
                JOIN dbo.App02EventReceipt r ON r.TenantId=p.TenantId
                  AND r.ReceiptId=p.ReceiptId
                WHERE r.TenantId=@Tenant AND r.TripId=@Trip
                  AND r.EventKind=N'CashierAcceptanceRequested'
                  AND p.Status=N'CASE') CaseState,
              (SELECT COUNT(*) FROM dbo.App02EventProgress p
                JOIN dbo.App02EventReceipt r ON r.TenantId=p.TenantId
                  AND r.ReceiptId=p.ReceiptId
                WHERE r.TenantId=@Tenant AND r.TripId=@Trip
                  AND r.EventKind=N'CashierAcceptanceRequested'
                  AND p.Status=N'REJECTED') Rejected,
              (SELECT COUNT(*) FROM dbo.App02ResolvedDependency d
                WHERE d.TenantId=@Tenant AND d.TripId=@Trip) Resolutions,
              (SELECT COUNT(*) FROM dbo.App02AcceptanceApplication a
                WHERE a.TenantId=@Tenant AND a.TripId=@Trip) Applications,
              (SELECT COUNT(*) FROM dbo.CashHandoverAcceptance a
                WHERE a.TenantId=@Tenant AND a.TripId=@Trip) Acceptances,
              (SELECT COUNT(*) FROM dbo.CashHandoverAcceptance a
                JOIN dbo.BusinessSourceLine sl ON sl.TenantId=a.TenantId
                  AND sl.Id=a.SourceLineId
                JOIN dbo.BusinessSource s ON s.TenantId=sl.TenantId
                  AND s.Id=sl.BusinessSourceId
                WHERE a.TenantId=@Tenant AND a.TripId=@Trip
                  AND s.SourceKind=N'CASH_HANDOVER_ACCEPT'
                  AND s.SourceNumber LIKE N'APP02:%') BoundSources,
              (SELECT COUNT(*) FROM dbo.LedgerEffect e
                JOIN dbo.CashHandoverAcceptance a ON a.TenantId=e.TenantId
                  AND a.SourceLineId=e.BusinessSourceLineId
                WHERE a.TenantId=@Tenant AND a.TripId=@Trip
                  AND e.EffectKey=N'CUSTODY-OUT') CustodyLegs,
              (SELECT COALESCE(SUM(e.AmountDelta),0) FROM dbo.LedgerEffect e
                JOIN dbo.CashHandoverAcceptance a ON a.TenantId=e.TenantId
                  AND a.SourceLineId=e.BusinessSourceLineId
                WHERE a.TenantId=@Tenant AND a.TripId=@Trip
                  AND e.EffectKey=N'CUSTODY-OUT') CustodyAmount,
              (SELECT COUNT(*) FROM dbo.LedgerEffect e
                JOIN dbo.CashHandoverAcceptance a ON a.TenantId=e.TenantId
                  AND a.SourceLineId=e.BusinessSourceLineId
                WHERE a.TenantId=@Tenant AND a.TripId=@Trip
                  AND e.EffectKey=N'FUND-IN') FundLegs,
              (SELECT COALESCE(SUM(e.AmountDelta),0) FROM dbo.LedgerEffect e
                JOIN dbo.CashHandoverAcceptance a ON a.TenantId=e.TenantId
                  AND a.SourceLineId=e.BusinessSourceLineId
                WHERE a.TenantId=@Tenant AND a.TripId=@Trip
                  AND e.EffectKey=N'FUND-IN') FundAmount,
              (SELECT COUNT(*) FROM dbo.LedgerEffect e
                JOIN dbo.CashHandoverOffer o ON o.TenantId=e.TenantId
                  AND o.SourceLineId=e.BusinessSourceLineId
                WHERE o.TenantId=@Tenant AND o.TripId=@Trip) OfferEffects,
              (SELECT COUNT(*) FROM dbo.App02OutboxMessage m
                JOIN dbo.App02AcceptanceApplication a ON a.TenantId=m.TenantId
                  AND a.MessageId=m.MessageId
                WHERE a.TenantId=@Tenant AND a.TripId=@Trip) Outbox,
              (SELECT COUNT(*) FROM dbo.App02SyntheticConsumerInbox i
                JOIN dbo.App02AcceptanceApplication a ON a.TenantId=i.TenantId
                  AND a.MessageId=i.MessageId
                WHERE a.TenantId=@Tenant AND a.TripId=@Trip) Consumers,
              (SELECT COUNT(*) FROM dbo.App02OutboxDelivery d
                JOIN dbo.App02AcceptanceApplication a ON a.TenantId=d.TenantId
                  AND a.MessageId=d.MessageId
                WHERE a.TenantId=@Tenant AND a.TripId=@Trip
                  AND d.Status=N'ACKED') Acked,
              (SELECT COALESCE(SUM(p.TechnicalRetryCount),0)
                FROM dbo.App02EventProgress p
                JOIN dbo.App02EventReceipt r ON r.TenantId=p.TenantId
                  AND r.ReceiptId=p.ReceiptId
                WHERE r.TenantId=@Tenant AND r.TripId=@Trip
                  AND r.EventKind=N'CashierAcceptanceRequested') Retries,
              (SELECT COUNT(*) FROM dbo.App02ExceptionCase c
                JOIN dbo.App02EventReceipt r ON r.TenantId=c.TenantId
                  AND r.ReceiptId=c.ReceiptId
                WHERE r.TenantId=@Tenant AND r.TripId=@Trip) CaseRows,
              (SELECT COUNT(*) FROM dbo.App02CaseDisposition d
                JOIN dbo.App02ExceptionCase c ON c.TenantId=d.TenantId
                  AND c.CaseId=d.CaseId
                JOIN dbo.App02EventReceipt r ON r.TenantId=c.TenantId
                  AND r.ReceiptId=c.ReceiptId
                WHERE r.TenantId=@Tenant AND r.TripId=@Trip) Dispositions,
              (SELECT COUNT(*) FROM dbo.App02CorrectionLink l
                JOIN dbo.App02AcceptanceApplication a
                  ON a.TenantId=l.TenantId
                  AND a.AcceptanceId=l.OriginalAcceptanceId
                WHERE a.TenantId=@Tenant AND a.TripId=@Trip) Corrections,
              (SELECT COUNT(*) FROM dbo.App02CorrectionLink l
                JOIN dbo.App02AcceptanceApplication a
                  ON a.TenantId=l.TenantId
                  AND a.AcceptanceId=l.OriginalAcceptanceId
                JOIN dbo.BusinessSourceLine sl ON sl.TenantId=l.TenantId
                  AND sl.BusinessSourceId=l.CorrectionSourceId
                JOIN dbo.LedgerEffect e ON e.TenantId=sl.TenantId
                  AND e.BusinessSourceLineId=sl.Id
                WHERE a.TenantId=@Tenant AND a.TripId=@Trip) CorrectionEffects,
              (SELECT COUNT(*) FROM dbo.App02AcceptanceApplication a
                LEFT JOIN dbo.App02ResolvedDependency d
                  ON d.TenantId=a.TenantId AND d.CReceiptId=a.CReceiptId
                LEFT JOIN dbo.CashHandoverAcceptance acc
                  ON acc.TenantId=a.TenantId AND acc.Id=a.AcceptanceId
                LEFT JOIN dbo.BusinessSourceLine sl
                  ON sl.TenantId=acc.TenantId AND sl.Id=acc.SourceLineId
                LEFT JOIN dbo.BusinessSource src
                  ON src.TenantId=sl.TenantId AND src.Id=sl.BusinessSourceId
                LEFT JOIN dbo.App02OutboxMessage m
                  ON m.TenantId=a.TenantId AND m.MessageId=a.MessageId
                LEFT JOIN dbo.App02OutboxDelivery delivery
                  ON delivery.TenantId=m.TenantId AND delivery.MessageId=m.MessageId
                WHERE a.TenantId=@Tenant AND a.TripId=@Trip
                  AND (d.CReceiptId IS NULL OR acc.Id IS NULL
                    OR acc.OfferId<>a.OfferId OR acc.EvidenceRef<>a.CountEvidenceRef
                    OR acc.AcceptedBy<>a.CActorId OR acc.AcceptedAmount<>200
                    OR src.Id IS NULL OR src.SourceKind<>N'CASH_HANDOVER_ACCEPT'
                    OR src.TripId<>a.TripId OR src.ActorId<>a.CActorId
                    OR m.MessageId IS NULL OR m.CReceiptId<>a.CReceiptId
                    OR m.AcceptanceId<>a.AcceptanceId
                    OR m.PayloadVersion<>1 OR m.PayloadHash<>
                       HASHBYTES('SHA2_256',m.CanonicalBytes)
                    OR delivery.MessageId IS NULL))
              +
              (SELECT COUNT(*) FROM dbo.App02OutboxDelivery delivery
                JOIN dbo.App02AcceptanceApplication a
                  ON a.TenantId=delivery.TenantId
                  AND a.MessageId=delivery.MessageId
                LEFT JOIN dbo.App02SyntheticConsumerInbox i
                  ON i.TenantId=delivery.TenantId
                  AND i.MessageId=delivery.MessageId
                WHERE a.TenantId=@Tenant AND a.TripId=@Trip
                  AND delivery.Status=N'ACKED'
                  AND (i.MessageId IS NULL OR delivery.AckAtUtc<i.ReceivedAtUtc
                    OR i.PayloadHash<>(SELECT m.PayloadHash
                       FROM dbo.App02OutboxMessage m
                       WHERE m.TenantId=i.TenantId AND m.MessageId=i.MessageId)))
              +
              (SELECT COUNT(*) FROM dbo.App02EventProgress progress
                JOIN dbo.App02EventReceipt r
                  ON r.TenantId=progress.TenantId
                  AND r.ReceiptId=progress.ReceiptId
                WHERE r.TenantId=@Tenant AND r.TripId=@Trip
                  AND NOT EXISTS (SELECT 1 FROM dbo.App02EventStatusHistory h
                    WHERE h.TenantId=progress.TenantId
                      AND h.ReceiptId=progress.ReceiptId
                      AND h.StatusTo=progress.Status)) BrokenLinks
            """, new { Tenant = tenant, Trip = trip });
        Assert.Equal(1, db.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM dbo.CashierCountEvidence
            WHERE TenantId=@Tenant AND TripId=@Trip
              AND CountedBy=@Actor AND CountedAmount=200
              AND VerificationStatus=N'VERIFIED'
            """, new { Tenant = tenant, Trip = trip, Actor = CashierActor }));
        return facts;
    }

    private async Task A4Capture(A4Session s, string phase, Guid? tripId = null)
    {
        var expected = A4Expected(s.Report.Group).Group;
        Assert.True(expected.TryGetProperty(phase, out JsonElement stateName),
            $"Unapproved APP02 A4 checkpoint {s.Report.Group}/{phase}");
        using var root = JsonDocument.Parse(File.ReadAllBytes(A4FixturePath()));
        decimal[] expectedValues = root.RootElement.GetProperty("states")
            .GetProperty(stateName.GetString()!).EnumerateArray()
            .Select(x => x.GetDecimal()).ToArray();
        Assert.Equal(A4Columns.Length, expectedValues.Length);
        Guid trip = tripId ?? s.Scene.Trip;
        decimal[] actual = A4ReadSql(s.Tenant, trip).Values();
        Assert.Equal(expectedValues.Length, actual.Length);
        var cells = A4Columns.Select((metric, index) =>
            new A4Cell(metric, expectedValues[index], actual[index],
                actual[index] - expectedValues[index],
                actual[index] == expectedValues[index])).ToArray();
        using var db = Open(s.Tenant);
        string[] history = db.Query<string>("""
            SELECT h.StatusTo
            FROM dbo.App02EventStatusHistory h
            JOIN dbo.App02EventReceipt r ON r.TenantId=h.TenantId
              AND r.ReceiptId=h.ReceiptId
            WHERE r.TenantId=@Tenant AND r.TripId=@Trip
              AND r.EventKind=N'CashierAcceptanceRequested'
            ORDER BY h.LoggedAtUtc,h.HistoryId
            """, new { Tenant = s.Tenant, Trip = trip }).ToArray();
        Assert.NotEmpty(history);
        var full = await FullSql(s.Tenant);
        s.Report.Checkpoints.Add(new A4Checkpoint(phase, trip,
            full.Database, full.Sha256, full.Tables, full.Rows, history, cells));
        Assert.All(cells, cell => Assert.True(cell.Match,
            $"{s.Tenant}/{s.Report.Group}/{phase}/{cell.Metric}: " +
            $"expected={cell.Expected} actual={cell.Actual} diff={cell.Difference}"));
        Assert.Equal(0m, actual[11] + actual[13]);
    }

    private async Task A4Run(string tenant, string group,
        Func<A4Session, Task> scenario)
    {
        string peer = tenant == "tenant-a" ? "tenant-b" : "tenant-a";
        FullSqlState peerBefore = await FullSql(peer);
        byte[] fixtureBytes = File.ReadAllBytes(A4FixturePath());
        var report = new A4Report
        {
            Group = group, TenantId = tenant,
            TestRunId = "app02a4-" +
                (Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? "local") + "-" +
                (Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") ?? "local"),
            SourceHeadSha = Environment.GetEnvironmentVariable("APP02A4_SOURCE_HEAD_SHA") ?? "LOCAL",
            WorkflowSha = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "LOCAL",
            OracleSha256 = Convert.ToHexString(SHA256.HashData(fixtureBytes)),
            PeerDatabase = peerBefore.Database,
            PeerBeforeSha256 = peerBefore.Sha256
        };
        Exception? failure = null;
        try
        {
            using var factory = fixture.CreateFactory();
            using var setup = SqlScratchFixture.WithTenant(factory.CreateClient(), tenant);
            using var sales = Client(factory, tenant, SalesActor, "sales");
            using var cashier = Client(factory, tenant, CashierActor, "cashier");
            using var maker = Client(factory, tenant, "a3-maker", "cashier_supervisor");
            using var checker = Client(factory, tenant, "a3-checker", "case_checker");
            var scene = await SetupA3(tenant, setup, sales);
            var p = NewPEvent(scene.Scene.Trip, scene.Offer);
            var c = NewCEvent(scene.Scene.Trip, p, scene.Count);
            var session = new A4Session(tenant, factory, setup, sales, cashier,
                maker, checker, scene.Scene, scene.Offer, scene.Count, p, c, report);
            await scenario(session);
            string[] phases = A4Expected(group).Phases;
            Assert.Equal(phases, report.Checkpoints.Select(x => x.Phase).ToArray());
            Assert.Equal(phases.Length * A4Columns.Length, report.CheckedCells);
            Assert.Equal(report.CheckedCells, report.MatchedCells);
            Assert.All(report.Checkpoints, x =>
            {
                Assert.Equal(tenant == "tenant-a" ? fixture.TenantADb : fixture.TenantBDb,
                    x.Database);
                Assert.True(x.SqlTables >= 25);
                Assert.True(x.SqlRows > 0);
                Assert.Equal(64, x.SqlSha256.Length);
                Assert.NotEmpty(x.SqlStatusHistory);
            });
            report.Database = report.Checkpoints[^1].Database;
            report.Status = "PASS";
        }
        catch (Exception ex)
        {
            report.Failure = ex.ToString();
            failure = ex;
        }
        finally
        {
            try
            {
                FullSqlState peerAfter = await FullSql(peer);
                report.PeerAfterSha256 = peerAfter.Sha256;
                if (peerAfter.Database != peerBefore.Database
                    || peerAfter.Sha256 != peerBefore.Sha256)
                {
                    report.Status = "FAIL";
                    report.Failure = (report.Failure ?? "") +
                        "\nPeer physical SQL mutated during isolated oracle group";
                    failure ??= new InvalidOperationException(report.Failure);
                }
                string folder = Path.Combine(AppContext.BaseDirectory,
                    "TestResults", "APP-02A4");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder,
                    $"{tenant}-{group}-oracle.json"),
                    JsonSerializer.Serialize(report, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        DefaultIgnoreCondition = JsonIgnoreCondition.Never
                    }));
            }
            catch (Exception ex) { failure ??= ex; }
        }
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task<Guid> A4IntakeC(A4Session s,
        App02EventInput? c = null)
    {
        var result = await IntakeA3(s.Cashier, c ?? s.C);
        return Assert.IsType<Guid>(result.ReceiptId);
    }

    private static async Task A4IntakeP(A4Session s,
        App02EventInput? p = null)
    {
        await IntakeA3(s.Sales, p ?? s.P);
    }

    private static async Task<string> A4Process(A4Session s, Guid receipt,
        DateTimeOffset? now = null)
    {
        using var scope = s.Factory.Services.CreateScope();
        var worker = scope.ServiceProvider.GetRequiredService<App02a3ProcessingService>();
        return await worker.ProcessAsync(s.Tenant, receipt,
            now ?? DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
    }

    private static async Task<string> A4Dispatch(A4Session s, Guid message,
        bool loseAck, DateTimeOffset? now = null)
    {
        using var scope = s.Factory.Services.CreateScope();
        var outbound = scope.ServiceProvider.GetRequiredService<App02a3OutboxService>();
        return await outbound.DispatchOneAsync(s.Tenant,
            now ?? DateTimeOffset.UtcNow, stopAfterConsumerCommit: loseAck,
            TestContext.Current.CancellationToken, onlyMessageId: message);
    }

    private Guid A4Message(A4Session s)
    {
        using var db = Open(s.Tenant);
        return db.QuerySingle<Guid>("""
            SELECT a.MessageId FROM dbo.App02AcceptanceApplication a
            WHERE a.TenantId=@Tenant AND a.TripId=@Trip
            """, new { Tenant = s.Tenant, Trip = s.Scene.Trip });
    }
}
