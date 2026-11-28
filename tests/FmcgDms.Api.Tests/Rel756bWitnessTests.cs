using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Xunit;

namespace FmcgDms.Api.Tests;

public sealed partial class App01e2CrossCommandRaceTests
{
    [Fact]
    public async Task Rel756b_actual_recovered_retry_keeps_original_oracle_failure_and_live_witness()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        // Independent physical databases; no shared-suite work or extra
        // A11-09 oracle output that could overwrite the original 24 groups.
        await using var isolated = new SqlScratchFixture();
        await isolated.InitializeAsync();
        var test = new App01e2CrossCommandRaceTests(isolated);
        const string tenant = "tenant-b";
        using var factory = isolated.CreateFactory();
        using var setup = SqlScratchFixture.WithTenant(factory.CreateClient(), tenant);
        using var sales = Client(factory, tenant, SalesActor, "sales");
        using var cashier = Client(factory, tenant, CashierActor, "cashier");
        using var maker = Client(factory, tenant, "a3-maker", "cashier_supervisor");
        using var checker = Client(factory, tenant, "a3-checker", "case_checker");
        var scene = await test.SetupA3(tenant, setup, sales);
        var p = NewPEvent(scene.Scene.Trip, scene.Offer);
        var c = NewCEvent(scene.Scene.Trip, p, scene.Count);
        var report = new A4Report
        {
            Group = "A11-09", TenantId = tenant,
            TestRunId = "rel756b-" + (Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? "local")
                + "-" + (Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") ?? "local"),
            SourceHeadSha = Environment.GetEnvironmentVariable("APP02A4_SOURCE_HEAD_SHA") ?? "LOCAL",
            WorkflowSha = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "LOCAL",
            OracleSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(A4FixturePath())))
        };
        var s = new A4Session(tenant, factory, setup, sales, cashier, maker, checker,
            scene.Scene, scene.Offer, scene.Count, p, c, report);
        string peerBefore = (await test.FullSql("tenant-a")).Sha256;
        Guid receipt;
        var before = await App01e4ApiProcess.StartAsync(isolated, ct, app02WorkerEnabled: false);
        await using (before)
        {
            using var realCashier = before.Client(tenant, CashierActor, "cashier");
            receipt = Assert.IsType<Guid>((await A4RestartIntake(s, before, realCashier, c,
                "pre-restart-C")).ReceiptId);
            await test.A4Capture(s, "os-pending");
            using var realSales = before.Client(tenant, SalesActor, "sales");
            await A4RestartIntake(s, before, realSales, p, "worker-off-P");
        }
        RecoveryProcessState previous = before.SnapshotState();
        Assert.True(previous.Exited);
        var retryScene = new Rel756Scene(tenant, scene.Scene.Trip, receipt);
        using var db = test.Open(tenant);
        DateTime due = default;
        Exception? error;
        try
        {
            Rel756InstallFault(db, receipt);
            error = await Record.ExceptionAsync(() => test.A4ObserveRestartAsync(
                s, receipt, previous, "controlled-rollback", async (host, timeline) =>
                {
                    timeline.Stage = "controlled-rollback";
                    // Real SQL 51220 rollback in the normal worker transaction.
                    // No fabricated 1205, no new HTTP intake or manual sweep.
                    var scheduled = await test.Rel756WaitForScheduleAsync(retryScene, ct);
                    due = Rel756AssertScheduled(scheduled);
                    db.Execute("DROP TRIGGER dbo.REL756_DueFault");
                    await test.Rel756WaitForAckAsync(retryScene, due, ct);
                    timeline.RecoveredObservedUtc = DateTimeOffset.UtcNow;
                    // Same original zero-retry oracle and wrapper as ordinary
                    // A11-09. Here we deliberately expect its assertion to fail.
                    // Controlled due+8s test is NOT the ordinary 30s gate.
                    timeline.Stage = "os-recovered-oracle";
                    await test.A4Capture(s, "os-recovered");
                }));
        }
        finally
        {
            db.Execute("DROP TRIGGER IF EXISTS dbo.REL756_DueFault");
        }

        Assert.NotNull(error);
        Assert.Contains("Retries: expected=0 actual=1", error.Message);
        Assert.Equal(48, report.CheckedCells);
        Assert.Equal(47, report.MatchedCells);
        var mismatch = Assert.Single(report.Checkpoints.SelectMany(x => x.Cells), x => !x.Match);
        Assert.Equal("Retries", mismatch.Metric);
        Assert.Equal(0m, mismatch.Expected);
        Assert.Equal(1m, mismatch.Actual);
        Assert.Null(report.UnexpectedRetryWitnessError);
        Assert.Null(report.RecoveryDiagnosticError);
        Assert.NotNull(report.RecoveryDiagnostic);
        string reference = Assert.IsType<string>(report.UnexpectedRetryWitness);
        string text = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "TestResults", reference), ct);
        using JsonDocument document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Assert.Equal("REL756B_UNEXPECTED_RETRY_V1", root.GetProperty("schema").GetString());
        Assert.Equal("controlled-rollback", root.GetProperty("mode").GetString());
        Assert.Equal("FAIL", root.GetProperty("originalOutcome").GetString());
        Assert.Equal("os-recovered-oracle", root.GetProperty("stage").GetString());
        Assert.Equal("CAPTURED", root.GetProperty("evidenceStatus").GetString());
        Assert.Equal(report.RecoveryDiagnostic, root.GetProperty("recoveryDiagnostic").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("recoveryDiagnosticError").ValueKind);
        Assert.Equal(isolated.Nonce, root.GetProperty("fixtureNonce").GetString());
        Assert.Equal(receipt, root.GetProperty("cReceiptId").GetGuid());
        Assert.Equal(scene.Scene.Trip, root.GetProperty("tripId").GetGuid());
        Assert.Equal(report.SourceHeadSha, root.GetProperty("sourceHeadSha").GetString());
        Assert.Equal(report.WorkflowSha, root.GetProperty("workflowSha").GetString());
        Assert.Equal(previous.Pid, root.GetProperty("previousProcess").GetProperty("pid").GetInt32());
        var process = root.GetProperty("process");
        Assert.NotEqual(previous.Pid, process.GetProperty("pid").GetInt32());
        Assert.False(process.GetProperty("exited").GetBoolean());
        Assert.True(process.GetProperty("workerEnabled").GetBoolean());
        Assert.True(root.GetProperty("historyAgrees").GetBoolean());
        var state = root.GetProperty("sql").GetProperty("state");
        Assert.Equal(receipt, state.GetProperty("cReceiptId").GetGuid());
        Assert.Equal("APPLIED", state.GetProperty("cStatus").GetString());
        Assert.Equal("ACKED", state.GetProperty("deliveryStatus").GetString());
        Assert.Equal(1, state.GetProperty("technicalRetryCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("nextAttemptAtUtc").ValueKind);
        var retry = Assert.Single(root.GetProperty("history").GetProperty("rows").EnumerateArray(),
            x => x.GetProperty("reasonCode").GetString() == "TECHNICAL_RETRY");
        Assert.Equal(due, retry.GetProperty("nextAttemptAtUtc").GetDateTime());
        var candidate = Assert.Single(root.GetProperty("retryCandidates").EnumerateArray());
        Assert.Equal(1, candidate.GetProperty("retryCount").GetInt32());
        Assert.Equal(due, candidate.GetProperty("nextAttemptAtUtc").GetDateTime());
        Assert.Contains(candidate.GetProperty("candidateErrors").EnumerateArray(),
            x => x.GetProperty("number").ValueKind == JsonValueKind.Number
                && x.GetProperty("number").GetInt32() == 51220);
        var denied = await Rel756bRetryHistory.CaptureAsync(isolated, "tenant-a",
            scene.Scene.Trip, receipt, c, ct);
        Assert.Equal("UNAVAILABLE", denied.Status);
        Assert.Empty(denied.Rows);
        var wrong = await Rel756bRetryHistory.CaptureAsync(isolated, tenant,
            scene.Scene.Trip, Guid.NewGuid(), c, ct);
        Assert.Equal("UNAVAILABLE", wrong.Status);
        Assert.Empty(wrong.Rows);
        var projection = root.GetProperty("projection");
        Assert.True(projection.GetProperty("scopeValid").GetBoolean());
        Assert.True(projection.GetProperty("live").GetBoolean());
        Assert.Contains(projection.GetProperty("technical").EnumerateArray(),
            x => x.GetProperty("number").ValueKind == JsonValueKind.Number
                && x.GetProperty("number").GetInt32() == 51220);
        Assert.DoesNotContain(projection.GetProperty("technical").EnumerateArray(),
            x => x.GetProperty("number").ValueKind == JsonValueKind.Number
                && x.GetProperty("number").GetInt32() == 1205);
        Assert.Equal("CAPTURED", root.GetProperty("xeStatus").GetString());
        Assert.True(root.GetProperty("xeObservation").GetProperty("complete").GetBoolean());
        Assert.Empty(root.GetProperty("xeObservation").GetProperty("graphs").EnumerateArray());
        Assert.Equal("REQUIRES_INDEPENDENT_DISPOSITION", root.GetProperty("rootCause").GetString());
        Assert.DoesNotContain("REL756 controlled precommit rollback", text);
        Assert.Equal(peerBefore, (await test.FullSql("tenant-a")).Sha256);
    }
}

public sealed class Rel756bWitnessContractTests
{
    [Fact]
    public void Retry_history_requires_exact_receipt_and_committed_due_even_after_progress_clears_it()
    {
        Guid receipt = Guid.NewGuid();
        DateTime now = new(2026, 11, 28, 0, 0, 0, DateTimeKind.Utc);
        var history = new Rel756bHistoryCapture("CAPTURED", now, now.AddSeconds(31),
        [
            new() { HistoryId = Guid.NewGuid(), StatusTo = "RECEIVED", LoggedAtUtc = now },
            new() { HistoryId = Guid.NewGuid(), StatusFrom = "RECEIVED", StatusTo = "RECEIVED",
                TechnicalRetryCount = 1, ReasonCode = "TECHNICAL_RETRY",
                NextAttemptAtUtc = now.AddSeconds(30), LoggedAtUtc = now.AddSeconds(1) },
            new() { HistoryId = Guid.NewGuid(), StatusFrom = "RECEIVED", StatusTo = "APPLIED",
                TechnicalRetryCount = 1, LoggedAtUtc = now.AddSeconds(30) }
        ], null);
        var state = new RecoverySqlState { CReceiptId = receipt, CStatus = "APPLIED", TechnicalRetryCount = 1 };
        Assert.True(Rel756bRetryHistory.Agrees(history, state, receipt));
        Assert.False(Rel756bRetryHistory.Agrees(history, state, Guid.NewGuid()));
        Assert.False(Rel756bRetryHistory.Agrees(history with { Status = "TRUNCATED" }, state, receipt));
        Assert.False(Rel756bRetryHistory.Agrees(history with { Rows = [history.Rows[0], history.Rows[2]] }, state, receipt));
        history.Rows[1].ReasonCode = "private-reason";
        history.Rows[1].Redact();
        Assert.Equal("[redacted]", history.Rows[1].ReasonCode);
        Assert.False(Rel756bRetryHistory.Agrees(history, state, receipt));
        Assert.DoesNotContain("private-reason", JsonSerializer.Serialize(history));
    }

    [Theory]
    [InlineData(false, null, null, true)]
    [InlineData(true, "LAB-E02/recovery.json", null, true)]
    [InlineData(true, null, null, false)]
    [InlineData(true, null, "IOException", false)]
    [InlineData(true, "LAB-E02/recovery.json", "IOException", false)]
    [InlineData(false, null, "IOException", false)]
    public void Required_recovery_reference_fails_closed(bool failed, string? reference,
        string? error, bool expected)
    {
        Assert.Equal(expected, Rel756bProcessEvidence.RequiredRecoveryReferencePresent(failed, reference, error));
    }

    [Fact]
    public void Technical_projection_excludes_foreign_receipt_tenant_pid_phase_and_time()
    {
        const string tenant = "tenant-b";
        const string nonce = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string run = "app02a4-local-local";
        Guid receipt = Guid.NewGuid();
        DateTimeOffset now = new(2026, 11, 28, 0, 0, 0, TimeSpan.Zero);
        object Row(string t, Guid id, string phase, int number, DateTimeOffset time) => new
        {
            kind = "technical", tenant = t, id, phase, errorKind = "sql",
            sqlErrorNumber = number, state = 51, severity = 13, utcMs = time.ToUnixTimeMilliseconds()
        };
        object Stream(bool truncated, object[] rows) => new
        {
            discardedChars = truncated ? 1 : 0, recoveryObservationsTruncated = truncated,
            captureError = (string?)null, recoveryObservations = rows
        };
        object Artifact(bool truncated, string source = "LOCAL", string workflow = "LOCAL") => new
        {
            stage = "rel756b-live", capturedUtc = now.AddSeconds(5),
            context = new { tenantId = tenant, group = "A11-09", fixtureNonce = nonce,
                testRunId = run, sourceHeadSha = source, workflowSha = workflow },
            process = new { pid = 123, workerEnabled = true, exited = false, startedUtc = now },
            stdout = Stream(truncated, [
                Row(tenant, receipt, "processing", 51220, now.AddSeconds(1)),
                Row("tenant-a", receipt, "processing", 1205, now.AddSeconds(1)),
                Row(tenant, Guid.NewGuid(), "processing", 1205, now.AddSeconds(1)),
                Row(tenant, Guid.Empty, "processing", 1205, now.AddSeconds(1)),
                Row(tenant, receipt, "outbox", 1205, now.AddSeconds(1)),
                Row(tenant, receipt, "processing", 1205, now.AddSeconds(-1)),
                Row(tenant, receipt, "processing", 1205, now.AddSeconds(4))
            ]),
            stderr = Stream(false, [])
        };
        using var doc = JsonSerializer.SerializeToDocument(Artifact(false));
        var valid = Rel756bProcessEvidence.Project(doc.RootElement, tenant, receipt, 123,
            nonce, run, "LOCAL", "LOCAL", now, now.AddSeconds(3));
        Assert.True(valid.ScopeValid);
        Assert.True(valid.StreamsUntruncated);
        Assert.Equal(51220, Assert.Single(valid.Technical).Number);
        var foreign = Rel756bProcessEvidence.Project(doc.RootElement, tenant, receipt, 124,
            nonce, run, "LOCAL", "LOCAL", now, now.AddSeconds(3));
        Assert.False(foreign.ScopeValid);
        Assert.Empty(foreign.Technical);
        using var invalid = JsonSerializer.SerializeToDocument(Artifact(false, "INVALID"));
        var invalidSource = Rel756bProcessEvidence.Project(invalid.RootElement, tenant, receipt, 123,
            nonce, run, "INVALID", "LOCAL", now, now.AddSeconds(3));
        Assert.False(invalidSource.ScopeValid);
        Assert.Empty(invalidSource.Technical);
        using var invalidWorkflow = JsonSerializer.SerializeToDocument(Artifact(false, workflow: "INVALID"));
        var wrongWorkflow = Rel756bProcessEvidence.Project(invalidWorkflow.RootElement, tenant, receipt, 123,
            nonce, run, "LOCAL", "INVALID", now, now.AddSeconds(3));
        Assert.False(wrongWorkflow.ScopeValid);
        Assert.Empty(wrongWorkflow.Technical);
        using var truncated = JsonSerializer.SerializeToDocument(Artifact(true));
        Assert.False(Rel756bProcessEvidence.Project(truncated.RootElement, tenant, receipt, 123,
            nonce, run, "LOCAL", "LOCAL", now, now.AddSeconds(3)).StreamsUntruncated);
    }
}
