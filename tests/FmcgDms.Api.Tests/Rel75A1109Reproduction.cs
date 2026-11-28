using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FmcgDms.Api.Tests;

public sealed partial class App01e2CrossCommandRaceTests
{
    private sealed class Rel75Sample
    {
        public string Schema { get; init; } = "REL75_A11_09_V1";
        public string Arm { get; init; } = "";
        public string Tenant { get; init; } = "";
        public int Iteration { get; init; }
        public string Nonce { get; init; } = "";
        public string SourceSha { get; init; } = "";
        public string WorkflowSha { get; init; } = "";
        public string Run { get; init; } = "";
        public string Attempt { get; init; } = "";
        public string Stage { get; set; } = "setup";
        public string Status { get; set; } = "FAIL";
        public string? FailureType { get; set; }
        public int? OldPid { get; set; }
        public int? IntakePid { get; set; }
        public int? RecoveryPid { get; set; }
        public int? HttpStatus { get; set; }
        public string? Ack { get; set; }
        public long? IntakeAckUtcMs { get; set; }
        public long? RecoveryUtcMs { get; set; }
        public int RecoveryPolls { get; set; }
        public int BacklogCount { get; set; }
        public bool OverlapObserved { get; set; }
        public string GraphCapture { get; set; } = "NOT_RUN";
        public string? GraphError { get; set; }
        public List<Rel75Graph> Graphs { get; } = [];
        public List<Rel75Boundary> Boundaries { get; } = [];
        public string? HttpDiagnostic { get; set; }
        public string? RecoveryDiagnostic { get; set; }
        public string? ProcessObservation { get; set; }
        public string? DiagnosticError { get; set; }
        public int[] HttpLogSqlNumbers { get; set; } = [];
        public int OracleChecked { get; set; }
        public int OracleMatched { get; set; }
        public bool PeerUnchanged { get; set; }
    }

    private sealed record Rel75SqlSession(int Spid, int BlockingSpid);
    private sealed record Rel75Boundary(string Phase, string Point, long UtcMs, Rel75SqlSession[] Sessions);

    private sealed class Rel75Gates : IDisposable
    {
        private readonly Dictionary<string, (EventWaitHandle Reached, EventWaitHandle Release)> gates = [];
        public Rel75Gates(string nonce, string tenant, string[] phases)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            try
            {
                foreach (string phase in phases)
                {
                    string prefix = $@"Local\FmcgRel75-{nonce}-{tenant}-{phase}";
                    var reached = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-reached", out bool newReached);
                    EventWaitHandle? release = null;
                    try
                    {
                        release = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-release", out bool newRelease);
                        if (!newReached || !newRelease) throw new InvalidOperationException("REL75_GATE_ALREADY_EXISTS");
                        gates.Add(phase, (reached, release));
                    }
                    catch { reached.Dispose(); release?.Dispose(); throw; }
                }
            }
            catch { Dispose(); throw; }
        }

        public bool Has(string phase) => gates.ContainsKey(phase);
        public bool Reached(string phase) => gates[phase].Reached.WaitOne(0);
        public async Task WaitAsync(string phase, CancellationToken ct)
        {
            int signal = await Task.Run(() => WaitHandle.WaitAny([gates[phase].Reached, ct.WaitHandle],
                TimeSpan.FromSeconds(8)), ct);
            ct.ThrowIfCancellationRequested();
            Assert.True(signal == 0, $"REL75 checkpoint not reached: {phase}");
        }
        public void Release(string phase) => gates[phase].Release.Set();
        public void ReleaseAll()
        {
            foreach (var gate in gates.Values) gate.Release.Set();
        }
        public void Dispose()
        {
            ReleaseAll();
            foreach (var gate in gates.Values)
            {
                gate.Reached.Dispose();
                gate.Release.Dispose();
            }
        }
    }

    private static string[] Rel75Phases(string arm) => arm switch
    {
        "worker-off" => [],
        "before-sweep" => ["SweepBeforeSelect"],
        "c-p-overlap" => ["CBeforePrerequisite", "PAfterRole", "PBeforeCommit"],
        "p-commit-sweep" => ["SweepBeforeSelect", "PBeforeCommit"],
        "outbox-overlap" => ["OutboxBeforeClaim"],
        "reverse-backlog" => ["SweepBeforeSelect"],
        _ => throw new ArgumentException("REL75_INVALID_ARM")
    };

    // Ordinary Full CI executes this projection/arm contract, not the experiment.
    // The dedicated hosted workflow filters to this ONE method with fresh fixture
    // ownership for each cell. No skip, fake PASS or relaxed A11-09 oracle.
    [Fact]
    public async Task Rel75_Probe_contract_and_opt_in_sample()
    {
        const string xml = """
            <RingBufferTarget><event><data><value><deadlock>
            <victim-list><victimProcess id="p1"/></victim-list>
            <process-list>
              <process id="p1" spid="51" currentdb="7" hostpid="123"><inputbuf>SECRET SQL TEXT</inputbuf></process>
              <process id="p2" spid="52" currentdb="7" hostpid="123" hostname="SECRET HOST"/>
            </process-list>
            <resource-list>
              <keylock dbid="7" hobtid="999" objectname="SECRET.dbo.App02EventReceipt"><owner-list><owner id="p1" mode="U"/></owner-list><waiter-list><waiter id="p2" mode="X"/></waiter-list></keylock>
              <keylock dbid="7" hobtid="998" objectname="SECRET.dbo.App02EventProgress"><owner-list><owner id="p2" mode="X"/></owner-list><waiter-list><waiter id="p1" mode="RangeS-U"/></waiter-list></keylock>
            </resource-list>
            </deadlock></value></data></event></RingBufferTarget>
            """;
        var graph = Assert.Single(Rel75DeadlockWitness.Project(xml, 7, 123));
        Assert.True(graph.Cycle);
        Assert.Equal(new[] { 51 }, graph.VictimSpids);
        Assert.Equal("App02EventReceipt", graph.Resources[0].Table);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(graph));
        Assert.Empty(Rel75DeadlockWitness.Project(xml, 8, 123));
        Assert.Empty(Rel75DeadlockWitness.Project(xml, 7, 124));
        Assert.Empty(Rel75DeadlockWitness.Project(xml.Replace("hostpid=\"123\" hostname", "hostpid=\"124\" hostname"), 7, 123));
        Assert.Throws<ArgumentException>(() => Rel75Phases("unapproved"));
        Assert.Equal(3, Rel75Phases("c-p-overlap").Length);

        if (Environment.GetEnvironmentVariable("REL75_A11_SAMPLE") is not { } enabled) return;
        Assert.Equal("1", enabled);
        Assert.True(OperatingSystem.IsWindows());
        Assert.Equal("true", Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));
        Assert.Equal("github-hosted", Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"));
        Assert.Equal(@"(localdb)\MSSQLLocalDB", fixture.SqlServer);

        string Env(string name) => Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException("REL75_MISSING_" + name);
        string arm = Env("REL75_A11_ARM");
        string tenant = Env("REL75_A11_TENANT");
        string[] phases = Rel75Phases(arm);
        Assert.Contains(tenant, new[] { "tenant-a", "tenant-b" });
        int iteration = int.Parse(Env("REL75_A11_ITERATION"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(iteration, 1, 5);
        var sample = new Rel75Sample
        {
            Arm = arm, Tenant = tenant, Iteration = iteration, Nonce = fixture.Nonce,
            SourceSha = Env("REL75_SOURCE_SHA"), WorkflowSha = Env("GITHUB_SHA"),
            Run = Env("GITHUB_RUN_ID"), Attempt = Env("GITHUB_RUN_ATTEMPT")
        };
        Assert.True(sample.SourceSha.Length == 40 && FailureDiagnostics.Sha(sample.SourceSha) is not null);
        Assert.True(sample.WorkflowSha.Length == 40 && FailureDiagnostics.Sha(sample.WorkflowSha) is not null);
        Assert.Equal(sample.SourceSha, Env("APP02A4_SOURCE_HEAD_SHA"));
        Assert.True(long.TryParse(sample.Run, out long run) && run > 0);
        Assert.True(int.TryParse(sample.Attempt, out int attempt) && attempt > 0);
        A4Report? report = null;
        try
        {
            await A4Run(tenant, "A11-09", async s =>
            {
                report = s.Report;
                await Rel75RunSample(s, sample, phases);
            });
            sample.Status = "PASS";
            sample.Stage = "oracle-verified";
        }
        catch (Exception ex)
        {
            sample.FailureType = FailureDiagnostics.Label(ex.GetType().Name);
            throw;
        }
        finally
        {
            if (report is not null)
            {
                sample.OracleChecked = report.CheckedCells;
                sample.OracleMatched = report.MatchedCells;
                sample.PeerUnchanged = report.PeerBeforeSha256.Length == 64
                    && report.PeerBeforeSha256 == report.PeerAfterSha256;
                sample.HttpDiagnostic = report.FailureDiagnostic;
                sample.RecoveryDiagnostic = report.RecoveryDiagnostic;
                sample.DiagnosticError ??= report.FailureDiagnosticError ?? report.RecoveryDiagnosticError;
                // This is only a numeric log observation, not attribution of the
                // victim to intake. Independent review must correlate the trace,
                // stack, SQL observation and structural engine witness.
                if (sample.HttpStatus is not null and not 202 && sample.HttpDiagnostic is { } diagnostic)
                {
                    try
                    {
                        string path = Path.Combine(AppContext.BaseDirectory, "TestResults", "LAB-E02",
                            Path.GetFileName(diagnostic));
                        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                        sample.HttpLogSqlNumbers = Rel75Numbers(doc.RootElement).Distinct().Order().ToArray();
                    }
                    catch (Exception ex) { sample.DiagnosticError ??= FailureDiagnostics.Label(ex.GetType().Name); }
                }
            }
            string folder = Path.Combine(AppContext.BaseDirectory, "TestResults", "REL75-A11");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "sample.json"),
                JsonSerializer.Serialize(sample, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static IEnumerable<int> Rel75Numbers(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in node.EnumerateObject())
            {
                if (prop.Name == "sqlErrorNumber" && prop.Value.ValueKind == JsonValueKind.Number
                    && prop.Value.TryGetInt32(out int number)) yield return number;
                foreach (int child in Rel75Numbers(prop.Value)) yield return child;
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray())
                foreach (int child in Rel75Numbers(item)) yield return child;
    }

    private async Task<Rel75SqlSession[]> Rel75Observe(string tenant, int pid, CancellationToken ct)
    {
        using var db = Open(tenant);
        var rows = await db.QueryAsync<Rel75SqlSession>(new CommandDefinition("""
            SELECT DISTINCT CONVERT(int,s.session_id) AS Spid,
                CONVERT(int,CASE WHEN r.blocking_session_id>0 THEN r.blocking_session_id ELSE 0 END) AS BlockingSpid
            FROM sys.dm_exec_sessions s
            JOIN sys.dm_tran_session_transactions t ON t.session_id=s.session_id AND t.is_user_transaction=1
            LEFT JOIN sys.dm_exec_requests r ON r.session_id=s.session_id
            WHERE s.host_process_id=@Pid AND s.database_id=DB_ID()
            ORDER BY Spid,BlockingSpid
            """, new { Pid = pid }, commandTimeout: 3, cancellationToken: ct));
        var observed = rows.ToArray();
        // Only retain blocking relationships whose other endpoint is also in scope.
        return observed.Select(x => observed.Any(o => o.Spid == x.BlockingSpid)
            ? x : x with { BlockingSpid = 0 }).ToArray();
    }

    private async Task Rel75BoundaryAt(Rel75Sample sample, string phase, string point, int pid,
        CancellationToken ct)
    {
        var sessions = await Rel75Observe(sample.Tenant, pid, ct);
        sample.Boundaries.Add(new(phase, point, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), sessions));
    }

    private async Task<App02IntakeResult> Rel75PostP(A4Session s, Rel75Sample sample,
        App01e4ApiProcess host, HttpClient sales, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, A3 + "intake") { Content = JsonContent.Create(s.P) };
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{ActivityTraceId.CreateRandom()}-{ActivitySpanId.CreateRandom()}-00");
        using HttpResponseMessage response = await sales.SendAsync(request, ct);
        sample.HttpStatus = (int)response.StatusCode;
        await RequireAcceptedA3Async(request, response, async (req, res) =>
        {
            var context = new FailureContext(FailureDiagnostics.Label(s.Report.TestRunId),
                FailureDiagnostics.Sha(s.Report.SourceHeadSha) ?? "INVALID",
                FailureDiagnostics.Sha(s.Report.WorkflowSha) ?? "INVALID", s.Tenant, s.Report.Group, sample.OldPid);
            string path = await host.CaptureHttpFailureAsync($"A11-09-{s.Tenant}", "post-restart-P",
                req, res, context, CancellationToken.None);
            s.Report.FailureDiagnostic = $"LAB-E02/{Path.GetFileName(path)}";
        }, error => s.Report.FailureDiagnosticError = error);
        var result = Assert.IsType<App02IntakeResult>(await response.Content.ReadFromJsonAsync<App02IntakeResult>(ct));
        Assert.Equal("RECEIVED_COMMITTED", result.Ack);
        Assert.False(result.Replayed);
        Assert.NotNull(result.ReceiptId);
        sample.Ack = "RECEIVED_COMMITTED";
        sample.IntakeAckUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return result;
    }

    private async Task Rel75RunSample(A4Session s, Rel75Sample sample, string[] phases)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var backlog = new List<Guid>();
        if (sample.Arm is "outbox-overlap" or "reverse-backlog")
        {
            int count = sample.Arm == "outbox-overlap" ? 1 : 3;
            for (int i = 0; i < count; i++)
            {
                var scene = await SetupA3(s.Tenant, s.Setup, s.Sales);
                var p = NewPEvent(scene.Scene.Trip, scene.Offer);
                var c = NewCEvent(scene.Scene.Trip, p, scene.Count);
                await IntakeA3(s.Sales, p);
                Guid receipt = Assert.IsType<Guid>((await IntakeA3(s.Cashier, c)).ReceiptId);
                if (sample.Arm == "outbox-overlap")
                {
                    using var scope = s.Factory.Services.CreateScope();
                    Assert.Equal("APPLIED", await scope.ServiceProvider
                        .GetRequiredService<App02a3ProcessingService>().ProcessAsync(s.Tenant, receipt, DateTimeOffset.UtcNow, ct));
                }
                backlog.Add(receipt);
            }
            sample.BacklogCount = backlog.Count;
        }
        Guid cReceipt;
        void Readiness(string? reference, string? error)
        {
            s.Report.FailureDiagnostic = reference;
            s.Report.FailureDiagnosticError = error;
        }
        sample.Stage = "pre-restart-C";
        var before = await App01e4ApiProcess.StartAsync(fixture, ct, onReadinessFailure: Readiness);
        await using (before)
        {
            sample.OldPid = before.Pid;
            using var cashier = before.Client(s.Tenant, CashierActor, "cashier");
            try
            {
                cReceipt = Assert.IsType<Guid>((await A4RestartIntake(s, before, cashier, s.C, "pre-restart-C")).ReceiptId);
                await A4Capture(s, "os-pending");
            }
            catch (Exception ex)
            {
                await A4CaptureRestartFailure(s, before, sample.Stage, ex, null, 0, 0);
                throw;
            }
        }
        var previous = before.SnapshotState();
        Assert.True(previous.Exited);

        using var gates = new Rel75Gates(fixture.Nonce, s.Tenant, phases);
        var capture = new Rel75DeadlockCapture(Open(s.Tenant), fixture.Nonce);
        App01e4ApiProcess? current = null;
        Task<App02IntakeResult>? intake = null;
        var elapsed = Stopwatch.StartNew();
        try
        {
            sample.Stage = "xe-start";
            await capture.StartAsync(fixture.Nonce, s.Tenant);
            sample.GraphCapture = "STARTED";
            sample.Stage = "post-restart-ready";
            bool workerOff = sample.Arm == "worker-off";
            current = await App01e4ApiProcess.StartAsync(fixture, ct, app02WorkerEnabled: !workerOff,
                onReadinessFailure: Readiness, rel75Tenant: s.Tenant, rel75Phases: phases,
                rel75Reverse: sample.Arm == "reverse-backlog");
            sample.IntakePid = current.Pid;
            Assert.NotEqual(before.Pid, current.Pid);
            using var sales = current.Client(s.Tenant, SalesActor, "sales");

            sample.Stage = "post-restart-P";
            string? first = sample.Arm switch
            {
                "c-p-overlap" => "CBeforePrerequisite",
                "before-sweep" or "p-commit-sweep" or "reverse-backlog" => "SweepBeforeSelect",
                "outbox-overlap" => "OutboxBeforeClaim",
                _ => null
            };
            if (first is not null)
            {
                await gates.WaitAsync(first, ct);
                await Rel75BoundaryAt(sample, first, "reached", current.Pid, ct);
            }
            intake = Rel75PostP(s, sample, current, sales, ct);
            if (sample.Arm == "c-p-overlap")
            {
                await gates.WaitAsync("PAfterRole", ct);
                await Rel75BoundaryAt(sample, "PAfterRole", "reached", current.Pid, ct);
                sample.OverlapObserved = sample.Boundaries[^1].Sessions.Select(x => x.Spid).Distinct().Count() >= 2;
                Assert.True(sample.OverlapObserved, "REL75 expected two actual user transactions");
                gates.Release("PAfterRole");
                // Let the real P statements proceed while C still holds its
                // real locks. Observe either P's pre-COMMIT barrier or a
                // scoped SQL blocking edge, never manufacture an unrelated lock.
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromSeconds(3))
                {
                    var sessions = await Rel75Observe(s.Tenant, current.Pid, ct);
                    bool atCommit = gates.Reached("PBeforeCommit");
                    if (atCommit || sessions.Any(x => x.BlockingSpid > 0))
                    {
                        sample.Boundaries.Add(new("PBeforeCommit", atCommit ? "reached" : "sql-wait",
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), sessions));
                        break;
                    }
                    if (intake.IsCompleted) break;
                    await Task.Delay(50, ct);
                }
                gates.Release("CBeforePrerequisite");
                gates.Release("PBeforeCommit");
            }
            else if (sample.Arm == "p-commit-sweep")
            {
                await gates.WaitAsync("PBeforeCommit", ct);
                await Rel75BoundaryAt(sample, "PBeforeCommit", "reached", current.Pid, ct);
                gates.Release("SweepBeforeSelect");
                gates.Release("PBeforeCommit");
            }
            else if (sample.Arm == "outbox-overlap")
            {
                // The worker is inside the actual sender transaction; allow
                // intake and outbox dispatch to progress concurrently.
                gates.Release("OutboxBeforeClaim");
            }
            // In the before-sweep and reversed arms P commits BEFORE release.
            await intake;
            long ack = Assert.IsType<long>(sample.IntakeAckUtcMs);
            DateTimeOffset until = DateTimeOffset.FromUnixTimeMilliseconds(ack).AddSeconds(30);
            if (first is not null)
            {
                gates.ReleaseAll();
                sample.Boundaries.Add(new(first, "released", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), []));
            }
            if (workerOff)
            {
                var facts = A4ReadSql(s.Tenant, s.Scene.Trip);
                Assert.Equal(1, facts.PReceipts);
                Assert.Equal(1, facts.Pending);
                Assert.Equal(0, facts.Applications);
                Assert.Equal(0, facts.Outbox);
                // Negative control: committed P is not a business application.
                // Stop the second worker-OFF PID, then recover in a third real
                // PID with the same original 30s deadline from the P ACK.
                await current.DisposeAsync();
                current = null;
                sample.Stage = "worker-off-restart";
                current = await App01e4ApiProcess.StartAsync(fixture, ct, app02WorkerEnabled: true, onReadinessFailure: Readiness);
                Assert.NotEqual(sample.IntakePid, current.Pid);
            }
            sample.RecoveryPid = current.Pid;
            sample.Stage = "recovery-poll";
            bool recovered = false;
            while (DateTimeOffset.UtcNow < until)
            {
                sample.RecoveryPolls++;
                using var db = Open(s.Tenant);
                int target = await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT COUNT(*) FROM dbo.App02AcceptanceApplication a
                    JOIN dbo.App02EventProgress p ON p.TenantId=a.TenantId AND p.ReceiptId=a.CReceiptId
                    JOIN dbo.App02OutboxDelivery d ON d.TenantId=a.TenantId AND d.MessageId=a.MessageId
                    JOIN dbo.App02SyntheticConsumerInbox i ON i.TenantId=d.TenantId AND i.MessageId=d.MessageId
                    WHERE a.TenantId=@Tenant AND a.CReceiptId=@Receipt AND p.Status=N'APPLIED' AND d.Status=N'ACKED'
                    """, new { Tenant = s.Tenant, Receipt = cReceipt }, commandTimeout: 3, cancellationToken: ct));
                bool backlogDone = backlog.Count == 0 || await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT COUNT(*) FROM dbo.App02AcceptanceApplication a
                    JOIN dbo.App02EventProgress p ON p.TenantId=a.TenantId AND p.ReceiptId=a.CReceiptId
                    JOIN dbo.App02OutboxDelivery d ON d.TenantId=a.TenantId AND d.MessageId=a.MessageId
                    JOIN dbo.App02SyntheticConsumerInbox i ON i.TenantId=d.TenantId AND i.MessageId=d.MessageId
                    WHERE a.TenantId=@Tenant AND a.CReceiptId IN @Backlog AND p.Status=N'APPLIED' AND d.Status=N'ACKED'
                    """, new { Tenant = s.Tenant, Backlog = backlog }, commandTimeout: 3, cancellationToken: ct)) == backlog.Count;
                recovered = target == 1 && backlogDone && DateTimeOffset.UtcNow <= until;
                if (recovered) break;
                await Task.Delay(150, ct);
            }
            sample.Stage = "recovery-deadline";
            Assert.True(recovered, "New OS PID failed to resume C and ACK outbox");
            sample.RecoveryUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        catch (Exception ex)
        {
            gates.ReleaseAll();
            if (intake is not null)
            {
                try { await intake.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { /* preserve original failure; HTTP diagnostic captured by intake */ }
            }
            if (current is not null)
                await A4CaptureRestartFailure(s, current, sample.Stage, ex, previous, elapsed.ElapsedMilliseconds, sample.RecoveryPolls);
            throw;
        }
        finally
        {
            gates.ReleaseAll();
            // Read the structural engine witness before disposing the active PID;
            // no capture failure can replace an original assertion.
            if (sample.GraphCapture == "STARTED")
            {
                try
                {
                    foreach (int pid in new[] { sample.IntakePid, sample.RecoveryPid }.OfType<int>().Distinct())
                        sample.Graphs.AddRange(await capture.CaptureAsync(pid));
                    sample.GraphCapture = "CAPTURED";
                }
                catch (Exception ex)
                {
                    sample.GraphCapture = "UNAVAILABLE";
                    sample.GraphError = FailureDiagnostics.Label(ex.GetType().Name);
                }
            }
            if (current is not null)
            {
                try
                {
                    string path = await current.CaptureFailureAsync($"rel75-{s.Tenant}", "rel75-observation", CancellationToken.None);
                    sample.ProcessObservation = $"LAB-E02/{Path.GetFileName(path)}";
                }
                catch (Exception ex) { sample.DiagnosticError ??= FailureDiagnostics.Label(ex.GetType().Name); }
                await current.DisposeAsync();
            }
            try { await capture.DisposeAsync(); }
            catch (Exception ex)
            {
                sample.GraphCapture = "CLEANUP_FAILED";
                sample.GraphError = FailureDiagnostics.Label(ex.GetType().Name);
            }
        }
        sample.Stage = "witness-and-oracle";
        Assert.Equal("CAPTURED", sample.GraphCapture);
        Assert.NotNull(sample.ProcessObservation);
        await A4Capture(s, "os-recovered");
    }
}
