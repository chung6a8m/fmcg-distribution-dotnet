using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FmcgDms.Api.Tests;

internal sealed record Rel756bTechnical(string ErrorKind, int? Number, byte? State,
    byte? Severity, long UtcMs);

internal sealed record Rel756bProcessProjection(bool ScopeValid, bool Live, bool StreamsUntruncated,
    Rel756bTechnical[] Technical);

// Consume only the already-redacted LAB-E02 projection. Do not treat arbitrary
// SQL errors, another tenant, a sweep-level Guid.Empty or another receipt as
// the error that caused this C's committed retry.
internal static class Rel756bProcessEvidence
{
    internal static Rel756bProcessProjection Project(JsonElement root, string tenant,
        Guid receipt, int pid, string nonce, string testRunId, string source, string workflow,
        DateTimeOffset start, DateTimeOffset outcome)
    {
        var context = root.GetProperty("context");
        var process = root.GetProperty("process");
        bool scope = context.GetProperty("tenantId").GetString() == tenant
            && context.GetProperty("group").GetString() == "A11-09"
            && context.GetProperty("fixtureNonce").GetString() == nonce
            && context.GetProperty("testRunId").GetString() == testRunId
            && context.GetProperty("sourceHeadSha").GetString() == source
            && context.GetProperty("workflowSha").GetString() == workflow
            && root.GetProperty("stage").GetString() == "rel756b-live"
            && process.GetProperty("pid").GetInt32() == pid
            && process.GetProperty("workerEnabled").GetBoolean()
            && process.GetProperty("startedUtc").GetDateTimeOffset() == start
            && root.GetProperty("capturedUtc").GetDateTimeOffset() >= outcome;
        bool live = !process.GetProperty("exited").GetBoolean();
        bool untruncated = true;
        var selected = new List<Rel756bTechnical>();
        foreach (string name in new[] { "stdout", "stderr" })
        {
            var stream = root.GetProperty(name);
            untruncated &= stream.GetProperty("discardedChars").GetInt64() == 0
                && !stream.GetProperty("recoveryObservationsTruncated").GetBoolean()
                && stream.GetProperty("captureError").ValueKind == JsonValueKind.Null;
            foreach (var row in stream.GetProperty("recoveryObservations").EnumerateArray())
            {
                if (row.GetProperty("kind").GetString() != "technical"
                    || row.GetProperty("tenant").GetString() != tenant
                    || row.GetProperty("phase").GetString() != "processing"
                    || row.GetProperty("id").GetGuid() != receipt)
                    continue;
                long time = row.GetProperty("utcMs").GetInt64();
                if (time < start.ToUnixTimeMilliseconds() || time > outcome.ToUnixTimeMilliseconds())
                    continue;
                string? kind = row.GetProperty("errorKind").GetString();
                if (kind == "sql")
                    selected.Add(new("sql", row.GetProperty("sqlErrorNumber").GetInt32(),
                        row.GetProperty("state").GetByte(), row.GetProperty("severity").GetByte(), time));
                else if (kind == "timeout")
                    selected.Add(new("timeout", null, null, null, time));
                else
                    untruncated = false;
            }
        }
        return new(scope, live, untruncated, scope
            ? selected.Distinct().OrderBy(x => x.UtcMs).ToArray() : []);
    }
}

public sealed partial class App01e2CrossCommandRaceTests
{
    private sealed class Rel756bTimeline
    {
        public string Stage { get; set; } = "post-restart-ready";
        public Stopwatch Elapsed { get; } = new();
        public int Polls { get; set; }
        public DateTimeOffset? PAckUtc { get; set; }
        public DateTimeOffset? RecoveryDeadlineUtc { get; set; }
        public DateTimeOffset? RecoveredObservedUtc { get; set; }
    }

    // Ordinary and deliberate-rollback regression share the actual assertion
    // boundary. The diagnostic runs outside the outcome deadline and never
    // retries an assertion, request or command. The original exception wins.
    private async Task A4ObserveRestartAsync(A4Session s, Guid receipt,
        RecoveryProcessState previous, string mode,
        Func<App01e4ApiProcess, Rel756bTimeline, Task> run,
        Action<string?, string?>? onReadinessFailure = null)
    {
        var timeline = new Rel756bTimeline();
        Rel75DeadlockCapture? capture = null;
        DateTimeOffset? xeStarted = null;
        string xeStatus = "NOT_STARTED";
        string? xeError = null;
        string? cleanupError = null;
        App01e4ApiProcess? host = null;
        Exception? failure = null;
        try
        {
            try
            {
                // Exact existing XE owner guard: LocalDB, generated nonce,
                // database slot/binding and no pre-existing session. No files.
                var target = new SqlConnectionStringBuilder(
                    fixture.ConnectionString(s.Tenant == "tenant-a" ? fixture.TenantADb : fixture.TenantBDb))
                { ConnectTimeout = 2, Pooling = false };
                capture = new Rel75DeadlockCapture(new SqlConnection(target.ConnectionString), fixture.Nonce);
                await capture.StartAsync(fixture.Nonce, s.Tenant);
                xeStarted = DateTimeOffset.UtcNow;
                xeStatus = "STARTED";
            }
            catch (Exception ex)
            {
                xeStatus = "START_FAILED";
                xeError = FailureDiagnostics.Label(ex.GetType().Name);
            }

            host = await App01e4ApiProcess.StartAsync(fixture, TestContext.Current.CancellationToken,
                app02WorkerEnabled: true, onReadinessFailure: onReadinessFailure);
            timeline.Elapsed.Start();
            await run(host, timeline);
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            // Fix outcome timestamp BEFORE collecting anything. No delay is
            // included in, or can satisfy, the 30-second recovery assertion.
            DateTimeOffset outcome = DateTimeOffset.UtcNow;
            timeline.Elapsed.Stop();
            Rel75XeObservation? xe = null;
            DateTimeOffset? xeFinished = null;
            RecoveryProcessState? current = null;
            try
            {
                if (host is not null)
                {
                    current = host.SnapshotState();
                    if (failure is not null)
                        await A4CaptureRestartFailure(s, host, timeline.Stage, failure,
                            previous, timeline.Elapsed.ElapsedMilliseconds, timeline.Polls);
                    if (xeStatus == "STARTED" && capture is not null)
                    {
                        try
                        {
                            xe = await capture.CaptureAsync([host.Pid]);
                            xeFinished = DateTimeOffset.UtcNow;
                            xeStatus = xe.Complete && xe.Graphs.All(g =>
                                g.EventUtcMs is long ms
                                && ms >= xeStarted!.Value.ToUnixTimeMilliseconds()
                                && ms <= xeFinished.Value.ToUnixTimeMilliseconds())
                                ? "CAPTURED" : "PARTIAL";
                        }
                        catch (Exception ex)
                        {
                            xeStatus = "UNAVAILABLE";
                            xeError = FailureDiagnostics.Label(ex.GetType().Name);
                        }
                    }
                }
                else if (xeStatus == "STARTED")
                    xeStatus = "NO_LIVE_PID";
            }
            catch (Exception ex)
            {
                s.Report.UnexpectedRetryWitnessError ??= FailureDiagnostics.Label(ex.GetType().Name);
            }
            finally
            {
                if (capture is not null)
                {
                    try { await capture.DisposeAsync(); }
                    catch (Exception ex)
                    {
                        cleanupError = FailureDiagnostics.Label(ex.GetType().Name);
                        xeStatus = "CLEANUP_FAILED";
                    }
                }
                try
                {
                    await A4RecordRel756bAsync(s, receipt, previous, host, current, mode,
                        timeline, outcome, failure, xeStatus, xeError, cleanupError,
                        xeStarted, xeFinished, xe);
                }
                catch (Exception ex)
                {
                    s.Report.UnexpectedRetryWitnessError ??= FailureDiagnostics.Label(ex.GetType().Name);
                }
                finally
                {
                    if (host is not null)
                    {
                        try { await host.DisposeAsync(); }
                        catch (Exception ex) when (failure is not null)
                        {
                            // Preserve primary assertion, expose cleanup gap.
                            s.Report.UnexpectedRetryWitnessError ??= FailureDiagnostics.Label(ex.GetType().Name);
                        }
                    }
                }
            }
        }
    }

    private async Task A4RecordRel756bAsync(A4Session s, Guid receipt,
        RecoveryProcessState previous, App01e4ApiProcess? host, RecoveryProcessState? current,
        string mode, Rel756bTimeline timeline, DateTimeOffset outcome, Exception? failure,
        string xeStatus, string? xeError, string? cleanupError,
        DateTimeOffset? xeStarted, DateTimeOffset? xeFinished, Rel75XeObservation? xe)
    {
        // Independent read-only budgets; failure of one source is not absence
        // of a fault. Capture after original assertion, while child still lives.
        RecoverySqlCapture sql = await RecoveryDiagnostics.CaptureSqlAsync(fixture, s.Tenant,
            s.Scene.Trip, s.Offer, s.P, s.C, CancellationToken.None);
        Rel756bHistoryCapture history = await Rel756bRetryHistory.CaptureAsync(fixture, s.Tenant,
            s.Scene.Trip, receipt, s.C, CancellationToken.None);
        bool historyAgrees = Rel756bRetryHistory.Agrees(history, sql.State, receipt);
        string? processReference = null, processError = null;
        Rel756bProcessProjection? projection = null;
        if (host is not null && current is not null)
        {
            try
            {
                var context = new FailureContext(FailureDiagnostics.Label(s.Report.TestRunId),
                    FailureDiagnostics.Sha(s.Report.SourceHeadSha) ?? "INVALID",
                    FailureDiagnostics.Sha(s.Report.WorkflowSha) ?? "INVALID",
                    s.Tenant, s.Report.Group, previous.Pid) { FixtureNonce = fixture.Nonce };
                string path = await host.CaptureRel75ObservationAsync(
                    $"rel756b-{mode}-{s.Tenant}", "rel756b-live", context, CancellationToken.None);
                processReference = $"LAB-E02/{Path.GetFileName(path)}";
                using var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
                projection = Rel756bProcessEvidence.Project(doc.RootElement, s.Tenant, receipt,
                    host.Pid, fixture.Nonce, context.TestRunId, context.SourceHeadSha,
                    context.WorkflowSha, current.StartedUtc, outcome);
            }
            catch (Exception ex) { processError = FailureDiagnostics.Label(ex.GetType().Name); }
        }

        var checkpoint = s.Report.Checkpoints.LastOrDefault(x => x.Phase == "os-recovered");
        var mismatches = checkpoint?.Cells.Where(x => !x.Match).ToArray() ?? [];
        int? retries = sql.State?.TechnicalRetryCount;
        bool complete = sql.Status == "CAPTURED" && historyAgrees
            && current is { WorkerEnabled: true, Exited: false } && previous.Exited
            && current.Pid != previous.Pid
            && projection is { ScopeValid: true, Live: true, StreamsUntruncated: true }
            && processError is null && xeStatus == "CAPTURED" && cleanupError is null
            && (retries == 0 || projection.Technical.Length > 0)
            && s.Report.UnexpectedRetryWitnessError is null;
        // A numeric worker signature and a scoped structural graph are
        // separate observations, not an automatic causal/lock-order verdict.
        string pathOut = await FailureDiagnostics.WriteAsync(AppContext.BaseDirectory,
            $"rel756b-{mode}-{s.Tenant}-witness", new
            {
                schema = "REL756B_UNEXPECTED_RETRY_V1",
                mode, tenantId = s.Tenant, group = s.Report.Group,
                sourceHeadSha = FailureDiagnostics.Sha(s.Report.SourceHeadSha) ?? "INVALID",
                workflowSha = FailureDiagnostics.Sha(s.Report.WorkflowSha) ?? "INVALID",
                testRunId = FailureDiagnostics.Label(s.Report.TestRunId),
                fixtureNonce = fixture.Nonce, tripId = s.Scene.Trip, cReceiptId = receipt,
                originalOutcome = failure is null ? "PASS" : "FAIL",
                failure = failure is null ? null : RecoveryException.From(failure),
                stage = timeline.Stage, outcomeUtc = outcome,
                elapsedMs = timeline.Elapsed.ElapsedMilliseconds, pollCount = timeline.Polls,
                pAckUtc = timeline.PAckUtc, recoveryDeadlineUtc = timeline.RecoveryDeadlineUtc,
                recoveredObservedUtc = timeline.RecoveredObservedUtc,
                previousProcess = previous, process = current, processReference, processError, projection,
                recoveryDiagnostic = s.Report.RecoveryDiagnostic,
                oracleChecked = s.Report.CheckedCells, oracleMatched = s.Report.MatchedCells,
                recoveredCheckpointPresent = checkpoint is not null, mismatches,
                sql, history, historyAgrees, xeStatus, xeError, xeCleanupError = cleanupError,
                xeStartedUtc = xeStarted, xeFinishedUtc = xeFinished, xeObservation = xe,
                capturedUtc = DateTimeOffset.UtcNow,
                evidenceStatus = complete ? "CAPTURED" : "PARTIAL",
                rootCause = "REQUIRES_INDEPENDENT_DISPOSITION"
            }, CancellationToken.None);
        s.Report.UnexpectedRetryWitness = $"LAB-E02/{Path.GetFileName(pathOut)}";
    }
}
