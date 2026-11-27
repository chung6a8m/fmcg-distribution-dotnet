using System.Text.Json;
using FmcgDms.Api.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FmcgDms.Api.Tests;

public sealed partial class App01e2CrossCommandRaceTests
{
    [Theory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    public async Task Rel75_recovery_sql_observation_distinguishes_missing_pending_applied_and_acked(string tenant)
    {
        using var factory = fixture.CreateFactory();
        using var setup = SqlScratchFixture.WithTenant(factory.CreateClient(), tenant);
        using var sales = Client(factory, tenant, SalesActor, "sales");
        using var cashier = Client(factory, tenant, CashierActor, "cashier");
        var scene = await SetupA3(tenant, setup, sales);
        var p = NewPEvent(scene.Scene.Trip, scene.Offer);
        var c = NewCEvent(scene.Scene.Trip, p, scene.Count);
        string peer = tenant == "tenant-a" ? "tenant-b" : "tenant-a";
        string peerBefore = (await FullSql(peer)).Sha256;

        async Task<RecoverySqlState> Observe()
        {
            string before = (await FullSql(tenant)).Sha256;
            var capture = await RecoveryDiagnostics.CaptureSqlAsync(fixture, tenant,
                scene.Scene.Trip, scene.Offer, p, c, TestContext.Current.CancellationToken);
            Assert.Equal("CAPTURED", capture.Status);
            Assert.Null(capture.Error);
            Assert.Equal(before, (await FullSql(tenant)).Sha256);
            return Assert.IsType<RecoverySqlState>(capture.State);
        }

        var missing = await Observe();
        Assert.Null(missing.PReceiptId);
        Assert.Null(missing.CReceiptId);
        Assert.Equal(0, missing.ApplicationCount);
        Assert.Equal(0, missing.ConsumerCount);

        Guid receipt = Assert.IsType<Guid>((await IntakeA3(cashier, c)).ReceiptId);
        var pending = await Observe();
        Assert.Equal(receipt, pending.CReceiptId);
        Assert.NotNull(pending.CReceivedAtUtc);
        Assert.Equal("PENDING_DEPENDENCY", pending.CStatus);
        Assert.Null(pending.PReceiptId);
        Assert.Equal(0, pending.ApplicationCount);
        Assert.Equal(0, pending.OutboxCount);

        await IntakeA3(sales, p);
        using var scope = factory.Services.CreateScope();
        var worker = scope.ServiceProvider.GetRequiredService<App02a3ProcessingService>();
        var outbound = scope.ServiceProvider.GetRequiredService<App02a3OutboxService>();
        Assert.Equal("APPLIED", await worker.ProcessAsync(tenant, receipt,
            DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
        var applied = await Observe();
        Assert.NotNull(applied.PReceiptId);
        Assert.Equal("APPLIED", applied.CStatus);
        Assert.Equal(1, applied.ApplicationCount);
        Assert.Equal(1, applied.AcceptanceCount);
        Assert.Equal(1, applied.CustodyLegCount);
        Assert.Equal(1, applied.FundLegCount);
        Assert.True(applied.LedgerBalanced);
        Assert.Equal(1, applied.OutboxCount);
        Assert.Equal("PENDING", applied.DeliveryStatus);
        Assert.Equal(0, applied.ConsumerCount);
        Guid message = Assert.IsType<Guid>(applied.MessageId);
        Assert.Equal("CONSUMER_COMMITTED_ACK_LOST", await outbound.DispatchOneAsync(
            tenant, DateTimeOffset.UtcNow, true, TestContext.Current.CancellationToken, message));
        var leased = await Observe();
        Assert.Equal("LEASED", leased.DeliveryStatus);
        Assert.NotNull(leased.LeaseExpiresAtUtc);
        Assert.Equal(1, leased.ConsumerCount);
        Assert.Null(leased.AckAtUtc);
        Assert.Equal("ACKED", await outbound.DispatchOneAsync(tenant,
            DateTimeOffset.UtcNow.AddMinutes(3), false, TestContext.Current.CancellationToken, message));
        var acked = await Observe();
        Assert.Equal("ACKED", acked.DeliveryStatus);
        Assert.NotNull(acked.AckAtUtc);
        Assert.Equal(1, acked.ConsumerCount);
        Assert.Equal(peerBefore, (await FullSql(peer)).Sha256);
    }

    [Fact]
    public async Task Rel75_recovery_failure_path_writes_live_process_and_owned_sql_reference()
    {
        const string tenant = "tenant-a";
        using var factory = fixture.CreateFactory();
        using var setup = SqlScratchFixture.WithTenant(factory.CreateClient(), tenant);
        using var sales = Client(factory, tenant, SalesActor, "sales");
        using var cashier = Client(factory, tenant, CashierActor, "cashier");
        using var maker = Client(factory, tenant, "a3-maker", "cashier_supervisor");
        using var checker = Client(factory, tenant, "a3-checker", "case_checker");
        var scene = await SetupA3(tenant, setup, sales);
        var p = NewPEvent(scene.Scene.Trip, scene.Offer);
        var c = NewCEvent(scene.Scene.Trip, p, scene.Count);
        var report = new A4Report
        {
            Group = "REL75-REGRESSION", TenantId = tenant,
            TestRunId = "rel75-" + (Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? "local")
                + "-" + (Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") ?? "local"),
            SourceHeadSha = Environment.GetEnvironmentVariable("APP02A4_SOURCE_HEAD_SHA") ?? "LOCAL",
            WorkflowSha = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "LOCAL"
        };
        var s = new A4Session(tenant, factory, setup, sales, cashier, maker, checker,
            scene.Scene, scene.Offer, scene.Count, p, c, report);
        await using var host = await App01e4ApiProcess.StartAsync(fixture,
            TestContext.Current.CancellationToken, app02WorkerEnabled: false);
        using var realCashier = host.Client(tenant, CashierActor, "cashier");
        Guid receipt = Assert.IsType<Guid>((await A4RestartIntake(s, host, realCashier,
            c, "pre-restart-C")).ReceiptId);
        string before = (await FullSql(tenant)).Sha256;
        var original = new TimeoutException("fixture-secret-not-for-artifact");
        TimeoutException observed = await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            try { throw original; }
            catch (Exception ex)
            {
                await A4CaptureRestartFailure(s, host, "injected-regression", ex, null, 17, 1);
                throw;
            }
        });
        Assert.Same(original, observed);
        Assert.Null(report.RecoveryDiagnosticError);
        string reference = Assert.IsType<string>(report.RecoveryDiagnostic);
        Assert.Equal(reference, report.FailureDiagnostic);
        using JsonDocument artifact = JsonDocument.Parse(await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "TestResults", reference),
            TestContext.Current.CancellationToken));
        JsonElement root = artifact.RootElement;
        Assert.Equal("injected-regression", root.GetProperty("stage").GetString());
        Assert.False(root.GetProperty("process").GetProperty("exited").GetBoolean());
        Assert.Equal(host.Pid, root.GetProperty("process").GetProperty("pid").GetInt32());
        Assert.Equal(tenant, root.GetProperty("context").GetProperty("tenantId").GetString());
        JsonElement sql = root.GetProperty("recovery").GetProperty("sql");
        Assert.Equal("CAPTURED", sql.GetProperty("status").GetString());
        Assert.Equal(receipt, sql.GetProperty("state").GetProperty("cReceiptId").GetGuid());
        Assert.Equal("PENDING_DEPENDENCY", sql.GetProperty("state").GetProperty("cStatus").GetString());
        Assert.DoesNotContain("fixture-secret-not-for-artifact", root.GetRawText());
        Assert.Equal(before, (await FullSql(tenant)).Sha256);
        // Leave no pending unrelated work for later tests sharing this fixture.
        await IntakeA3(sales, p);
        using var scope = factory.Services.CreateScope();
        Assert.Equal("APPLIED", await scope.ServiceProvider.GetRequiredService<App02a3ProcessingService>()
            .ProcessAsync(tenant, receipt, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
        using var db = Open(tenant);
        Guid message = Dapper.SqlMapper.QuerySingle<Guid>(db, """
            SELECT MessageId FROM dbo.App02AcceptanceApplication
            WHERE TenantId=@Tenant AND CReceiptId=@Receipt
            """, new { Tenant = tenant, Receipt = receipt });
        Assert.Equal("ACKED", await scope.ServiceProvider.GetRequiredService<App02a3OutboxService>()
            .DispatchOneAsync(tenant, DateTimeOffset.UtcNow, false, TestContext.Current.CancellationToken, message));
    }

    [Fact]
    public async Task Rel75_recovery_sql_capture_rejects_foreign_tenant_and_honors_cancellation()
    {
        Guid offer = Guid.NewGuid();
        var p = NewPEvent(Guid.NewGuid(), offer);
        var c = NewCEvent(p.TripId, p, "unused");
        var denied = await RecoveryDiagnostics.CaptureSqlAsync(fixture, "foreign",
            p.TripId, offer, p, c, TestContext.Current.CancellationToken);
        Assert.Equal("UNAVAILABLE", denied.Status);
        Assert.Null(denied.State);
        Assert.Equal("UnauthorizedAccessException", denied.Error?.Type);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var stopped = await RecoveryDiagnostics.CaptureSqlAsync(fixture, "tenant-a",
            p.TripId, offer, p, c, canceled.Token);
        Assert.Equal("UNAVAILABLE", stopped.Status);
        Assert.Null(stopped.State);
        Assert.Equal("OperationCanceledException", stopped.Error?.Type);
    }
}

public sealed class Rel75RecoveryRedactionTests
{
    [Fact]
    public void Synthetic_worker_emits_parseable_sweep_and_technical_markers_without_throwing_sink_failures()
    {
        var logger = new CaptureLogger();
        App02a3RecoveryTelemetry.Sweep(logger, "tenant-a", "processing", "end", 2, 17);
        App02a3RecoveryTelemetry.Technical(logger, "tenant-a", "outbox", Guid.Empty,
            new TimeoutException("do-not-export-message"));
        using var doc = JsonSerializer.SerializeToDocument(
            FailureDiagnostics.RedactedStream((string.Join("\n", logger.Lines) + "\n", 0, true, null)));
        JsonElement rows = doc.RootElement.GetProperty("recoveryObservations");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("sweep", rows[0].GetProperty("kind").GetString());
        Assert.Equal("timeout", rows[1].GetProperty("errorKind").GetString());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("sqlErrorNumber").ValueKind);
        Assert.DoesNotContain("do-not-export-message", doc.RootElement.GetRawText());
        logger.Fail = true;
        App02a3RecoveryTelemetry.Sweep(logger, "tenant-a", "processing", "start", 0, 0);
        App02a3RecoveryTelemetry.Technical(logger, "tenant-a", "outbox", Guid.Empty, new TimeoutException());
    }

    private sealed class CaptureLogger : ILogger
    {
        public List<string> Lines { get; } = [];
        public bool Fail { get; set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (Fail) throw new IOException("test sink failure");
            Lines.Add(formatter(state, exception));
        }
    }

    [Fact]
    public void Structured_worker_observations_keep_only_exact_numeric_signatures()
    {
        const string marker = "never-export-secret";
        string text = "REL75_SWEEP tenant=tenant-b phase=processing point=end count=2 elapsedMs=123 utcMs=1790000000000\n"
            + "REL75_TECH tenant=tenant-b phase=processing id=00000000-0000-0000-0000-000000000000 kind=sql number=1205 state=45 severity=13 utcMs=1790000000001\n"
            + "REL75_TECH tenant=tenant-b phase=outbox id=00000000-0000-0000-0000-000000000000 kind=sql number=1205 state=45 severity=13 utcMs=1790000000001 " + marker + "\n"
            + "REL75_SWEEP tenant=" + marker + " phase=processing point=end count=2 elapsedMs=123 utcMs=1790000000000\n";
        using JsonDocument doc = JsonSerializer.SerializeToDocument(
            FailureDiagnostics.RedactedStream((text, 0, true, null)));
        var rows = doc.RootElement.GetProperty("recoveryObservations");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal(2, rows[0].GetProperty("count").GetInt32());
        Assert.Equal(1205, rows[1].GetProperty("sqlErrorNumber").GetInt32());
        Assert.DoesNotContain(marker, doc.RootElement.GetRawText());

        var state = new RecoverySqlState { CStatus = "PENDING_DEPENDENCY",
            CReasonCode = "TECHNICAL_RETRY", LastErrorCode = marker, PStatus = marker };
        state.Redact();
        string json = JsonSerializer.Serialize(state);
        Assert.Contains("TECHNICAL_RETRY", json);
        Assert.DoesNotContain(marker, json);
        Assert.Equal("[redacted]", state.LastErrorCode);
        Assert.Equal("[redacted]", state.PStatus);
        Assert.DoesNotContain(marker, JsonSerializer.Serialize(
            RecoveryException.From(new TimeoutException(marker))));

        // Only fixed runtime reason codes are useful diagnostic evidence.
        // Free-form maker/checker reasons are still redacted.
        foreach (string reason in new[]
        {
            "MISSING_P_24H", "LATE_P_AFTER_24H", "ACCEPT_REFERENCE_INVALID",
            "SOURCE_ALREADY_USED", "TRIP_NOT_ELIGIBLE", "OFFER_NOT_FOUND_OR_INVALID",
            "CASHIER_MUST_BE_INDEPENDENT", "CASHIER_COUNT_NOT_VERIFIED",
            "CASHIER_COUNT_ALREADY_USED", "CUSTODY_SOURCE_DRIFT", "CUSTODY_INSUFFICIENT"
        })
        {
            var known = new RecoverySqlState { CReasonCode = reason };
            known.Redact();
            Assert.Equal(reason, known.CReasonCode);
        }

        // Actual HTTP transport timeout shape: cancellation alone is
        // indistinguishable from user cancellation without the inner type.
        var wrapped = RecoveryException.From(new TaskCanceledException(marker,
            new TimeoutException(marker, new IOException(marker))));
        Assert.Equal("TaskCanceledException", wrapped.Type);
        Assert.Equal(new[] { "TimeoutException", "IOException" }, wrapped.InnerTypes);
        Assert.Empty(wrapped.SqlErrors);
        Assert.Empty(RecoveryException.From(new TaskCanceledException(marker)).InnerTypes);
        Assert.DoesNotContain(marker, JsonSerializer.Serialize(wrapped));

        Exception chain = new TimeoutException(marker);
        for (int i = 0; i < 12; i++)
            chain = new IOException(marker, chain);
        var bounded = RecoveryException.From(chain);
        Assert.Equal(7, bounded.InnerTypes.Length);
        Assert.DoesNotContain(marker, JsonSerializer.Serialize(bounded));
    }
}
