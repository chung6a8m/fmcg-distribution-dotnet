using System.Diagnostics;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FmcgDms.Api.Tests;

public sealed partial class App01e2CrossCommandRaceTests
{
    private sealed class Rel756RetryState
    {
        public string Status { get; set; } = "";
        public string? ReasonCode { get; set; }
        public byte TechnicalRetryCount { get; set; }
        public DateTime? LastAttemptAtUtc { get; set; }
        public DateTime? NextAttemptAtUtc { get; set; }
        public DateTime StateChangedAtUtc { get; set; }
        public DateTime ObservedUtc { get; set; }
        public DateTime? AckAtUtc { get; set; }
        public int Applications { get; set; }
        public int Acked { get; set; }
    }

    private sealed record Rel756Scene(string Tenant, Guid Trip, Guid CReceipt);

    private async Task<Rel756Scene> Rel756SeedAsync(string tenant, TestApiFactory host)
    {
        using var setup = SqlScratchFixture.WithTenant(host.CreateClient(), tenant);
        using var sales = Client(host, tenant, SalesActor, "sales");
        using var cashier = Client(host, tenant, CashierActor, "cashier");
        var s = await SetupA3(tenant, setup, sales);
        var p = NewPEvent(s.Scene.Trip, s.Offer);
        await IntakeA3(sales, p);
        Guid c = Assert.IsType<Guid>((await IntakeA3(cashier,
            NewCEvent(s.Scene.Trip, p, s.Count))).ReceiptId);
        return new Rel756Scene(tenant, s.Scene.Trip, c);
    }

    private static void Rel756InstallFault(SqlConnection db, Guid receipt)
    {
        // Test-owned scratch trigger, exact synthetic acceptance only. Real
        // SQL rollback via 51220, NOT a manufactured SQL1205/deadlock witness.
        db.Execute($"""
            CREATE TRIGGER dbo.REL756_DueFault ON dbo.LedgerEffect AFTER INSERT AS
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM inserted i
                    JOIN dbo.BusinessSourceLine sl ON sl.TenantId=i.TenantId
                        AND sl.Id=i.BusinessSourceLineId
                    JOIN dbo.BusinessSource bs ON bs.TenantId=sl.TenantId
                        AND bs.Id=sl.BusinessSourceId
                    WHERE bs.SourceKind=N'CASH_HANDOVER_ACCEPT'
                        AND bs.SourceNumber=N'APP02:{receipt:N}')
                THROW 51220, 'REL756 controlled precommit rollback', 1;
            END
            """);
    }

    private Rel756RetryState Rel756Read(Rel756Scene scene)
    {
        using var db = Open(scene.Tenant);
        return db.QuerySingle<Rel756RetryState>("""
            SELECT p.Status,p.ReasonCode,p.TechnicalRetryCount,
                p.LastAttemptAtUtc,p.NextAttemptAtUtc,p.StateChangedAtUtc,
                SYSUTCDATETIME() ObservedUtc,
                (SELECT COUNT(*) FROM dbo.App02AcceptanceApplication a
                    WHERE a.TenantId=p.TenantId AND a.CReceiptId=p.ReceiptId) Applications,
                (SELECT COUNT(*) FROM dbo.App02AcceptanceApplication a
                    JOIN dbo.App02OutboxDelivery d ON d.TenantId=a.TenantId
                        AND d.MessageId=a.MessageId
                    JOIN dbo.App02SyntheticConsumerInbox i ON i.TenantId=d.TenantId
                        AND i.MessageId=d.MessageId
                    WHERE a.TenantId=p.TenantId AND a.CReceiptId=p.ReceiptId
                        AND d.Status=N'ACKED') Acked,
                (SELECT MAX(d.AckAtUtc) FROM dbo.App02AcceptanceApplication a
                    JOIN dbo.App02OutboxDelivery d ON d.TenantId=a.TenantId
                        AND d.MessageId=a.MessageId
                    WHERE a.TenantId=p.TenantId AND a.CReceiptId=p.ReceiptId
                        AND d.Status=N'ACKED') AckAtUtc
            FROM dbo.App02EventProgress p
            WHERE p.TenantId=@Tenant AND p.ReceiptId=@CReceipt
            """, new { scene.Tenant, scene.CReceipt });
    }

    private static DateTime Rel756AssertScheduled(Rel756RetryState row)
    {
        Assert.Equal("RECEIVED", row.Status);
        Assert.Equal("TECHNICAL_RETRY", row.ReasonCode);
        Assert.Equal((byte)1, row.TechnicalRetryCount);
        Assert.Equal(0, row.Applications);
        Assert.Equal(0, row.Acked);
        DateTime last = Assert.IsType<DateTime>(row.LastAttemptAtUtc);
        DateTime due = Assert.IsType<DateTime>(row.NextAttemptAtUtc);
        Assert.Equal(TimeSpan.FromSeconds(30), due - last);
        return due;
    }

    private async Task<Rel756RetryState> Rel756WaitForScheduleAsync(Rel756Scene scene,
        CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            Rel756RetryState row = Rel756Read(scene);
            if (row.ReasonCode == "TECHNICAL_RETRY")
                return row;
            Assert.Equal(0, row.Applications);
            await Task.Delay(100, ct);
        } while (watch.Elapsed < TimeSpan.FromSeconds(15));
        throw new TimeoutException("REL756 did not observe the committed technical retry.");
    }

    private async Task<Rel756RetryState> Rel756WaitForAckAsync(Rel756Scene scene,
        DateTime due, CancellationToken ct)
    {
        // Separate scheduler-specific acceptance window. The original
        // A11-09 recovery deadline and zero-retry oracle remain unchanged.
        DateTime stop = due.AddSeconds(8);
        Rel756RetryState row;
        do
        {
            row = Rel756Read(scene);
            if (row.ObservedUtc < due)
            {
                Assert.Equal(due, Rel756AssertScheduled(row));
            }
            if (row.Status == "APPLIED" && row.Acked == 1)
            {
                Assert.Equal((byte)1, row.TechnicalRetryCount);
                Assert.Null(row.NextAttemptAtUtc);
                Assert.Equal(1, row.Applications);
                Assert.True(row.StateChangedAtUtc >= due, "REL756 C applied before its persisted due time.");
                DateTime ack = Assert.IsType<DateTime>(row.AckAtUtc);
                Assert.InRange(ack, row.StateChangedAtUtc, stop);
                return row;
            }
            await Task.Delay(100, ct);
        } while (DateTime.UtcNow <= stop);
        throw new TimeoutException("REL756 no event-free APPLIED/ACKED recovery after persisted due time.");
    }

    [Fact]
    public async Task Rel756_due_retry_recovers_without_new_intake_on_startup_and_live_worker_both_tenants()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // Fresh pair avoids prior suite rows, peer sweeps, or previous
        // in-memory hints accidentally satisfying the event-free assertion.
        await using var isolated = new SqlScratchFixture();
        await isolated.InitializeAsync();
        var test = new App01e2CrossCommandRaceTests(isolated);
        using var setup = isolated.CreateFactory();
        Rel756Scene a = await test.Rel756SeedAsync("tenant-a", setup);
        Rel756Scene b = await test.Rel756SeedAsync("tenant-b", setup);
        using var dbA = test.Open(a.Tenant);
        using var dbB = test.Open(b.Tenant);
        Rel756RetryState scheduledA;
        Rel756RetryState scheduledB;
        Rel756RetryState finalA;
        Rel756RetryState finalB;
        int workerPid;
        try
        {
            Rel756InstallFault(dbA, a.CReceipt);
            Rel756InstallFault(dbB, b.CReceipt);
            // A: actual rollback and schedule committed while no hosted worker
            // runs. Its new OS process must rediscover the durable future due.
            using (var scope = setup.Services.CreateScope())
            {
                var processor = scope.ServiceProvider.GetRequiredService<App02a3ProcessingService>();
                Assert.Equal("RETRY_SCHEDULED", await processor.ProcessAsync(
                    a.Tenant, a.CReceipt, DateTimeOffset.UtcNow, ct));
            }
            scheduledA = test.Rel756Read(a);
            DateTime dueA = Rel756AssertScheduled(scheduledA);
            dbA.Execute("DROP TRIGGER dbo.REL756_DueFault");

            // B: let the real worker's startup sweep encounter a SQL rollback
            // and create its own durable timer hint. No HTTP requests after
            // this process starts except the harness's read-only health probe.
            await using (var worker = await App01e4ApiProcess.StartAsync(
                isolated, ct, app02WorkerEnabled: true))
            {
                workerPid = worker.Pid;
                Rel756RetryState beforeDue = test.Rel756Read(a);
                Assert.True(beforeDue.ObservedUtc < dueA,
                    "REL756 startup must occur before A's due time to exercise timer rediscovery.");
                Assert.Equal(dueA, Rel756AssertScheduled(beforeDue));
                scheduledB = await test.Rel756WaitForScheduleAsync(b, ct);
                DateTime dueB = Rel756AssertScheduled(scheduledB);
                Assert.True(dueB > dueA);
                Assert.True(scheduledB.ObservedUtc < dueB);
                dbB.Execute("DROP TRIGGER dbo.REL756_DueFault");

                // No new intake, manual sweep, business retry, or fake clock.
                // Both cases must reach committed acceptance + consumer ACK.
                // Concurrent read-only polling observes each due independently.
                Task<Rel756RetryState> waitA = test.Rel756WaitForAckAsync(a, dueA, ct);
                Task<Rel756RetryState> waitB = test.Rel756WaitForAckAsync(b, dueB, ct);
                await Task.WhenAll(waitA, waitB);
                finalA = await waitA;
                finalB = await waitB;
            }
        }
        finally
        {
            dbA.Execute("DROP TRIGGER IF EXISTS dbo.REL756_DueFault");
            dbB.Execute("DROP TRIGGER IF EXISTS dbo.REL756_DueFault");
        }

        // Reuse the independent SQL queries and the unchanged acked fixture.
        // This NEW explicit rollback regression expects one historical retry;
        // it does not edit the original A11-09 zero-retry expected values.
        using var oracle = JsonDocument.Parse(File.ReadAllBytes(A4FixturePath()));
        decimal[] expected = oracle.RootElement.GetProperty("states").GetProperty("acked")
            .EnumerateArray().Select(x => x.GetDecimal()).ToArray();
        expected[Array.IndexOf(A4Columns, "Retries")] = 1m;
        var rows = new List<object>();
        foreach (var item in new[] { (Scene: a, Scheduled: scheduledA, Final: finalA, Foreign: b),
                                    (Scene: b, Scheduled: scheduledB, Final: finalB, Foreign: a) })
        {
            decimal[] actual = test.A4ReadSql(item.Scene.Tenant, item.Scene.Trip).Values();
            Assert.Equal(expected, actual);
            using var db = test.Open(item.Scene.Tenant);
            Assert.Equal(2, db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.App02EventReceipt"));
            Assert.Equal(0, db.ExecuteScalar<int>("""
                SELECT COUNT(*) FROM dbo.App02EventReceipt
                WHERE TenantId<>@Tenant OR TripId=@ForeignTrip
                """, new { item.Scene.Tenant, ForeignTrip = item.Foreign.Trip }));
            rows.Add(new {
                item.Scene.Tenant, item.Scene.Trip, item.Scene.CReceipt,
                item.Scheduled.LastAttemptAtUtc, item.Scheduled.NextAttemptAtUtc,
                item.Final.StateChangedAtUtc, item.Final.AckAtUtc,
                item.Final.TechnicalRetryCount,
                Cells = A4Columns.Select((metric, i) => new {
                    Metric = metric, Expected = expected[i], Actual = actual[i],
                    Match = expected[i] == actual[i]
                }).ToArray()
            });
        }
        string folder = Path.Combine(AppContext.BaseDirectory, "TestResults", "REL75-6A");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "due-recovery.json"), JsonSerializer.Serialize(new {
            Schema = "REL756_DUE_RECOVERY_V1",
            Scope = "SYNTHETIC_NONPRODUCTION",
            SourceHeadSha = Environment.GetEnvironmentVariable("APP02A4_SOURCE_HEAD_SHA") ?? "LOCAL",
            WorkflowSha = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "LOCAL",
            RunId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? "LOCAL",
            RunAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") ?? "LOCAL",
            FixtureNonce = isolated.Nonce, WorkerPid = workerPid,
            FaultNumber = 51220, BackoffSeconds = 30, SchedulerWindowSeconds = 8,
            Rows = rows, Status = "PASS"
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
