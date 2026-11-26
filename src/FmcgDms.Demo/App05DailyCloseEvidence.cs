using System.Data;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

[SupportedOSPlatform("windows")]
internal static class App05DailyCloseEvidence
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static async Task Run(string root, string source)
    {
        var run = new DemoRun(Guid.NewGuid().ToString("N"));
        string folder = Directory.CreateDirectory(Path.Combine(root, "TestResults", "app05-7", run.Id)).FullName;
        string fixture = Path.Combine(root, "docs", "roadmap", "app-05-7-fixture.json");
        using (var input = JsonDocument.Parse(await File.ReadAllTextAsync(fixture)))
            if (input.RootElement.GetProperty("version").GetString() != "APP-05.7-DEV-v1")
                throw new InvalidOperationException("Unknown APP-05.7 fixture version.");
        File.Copy(fixture, Path.Combine(folder, "fixture.json"));
        bool direct = Environment.GetEnvironmentVariable("APP05_DAILY_DIRECT") == "1";
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(direct ? 14 : 8));
        var ct = deadline.Token;
        var snapshots = new List<App05DailyCloseOracle.Snapshot>();
        App05DailyCloseOracle.Comparison[] comparisons = [];
        var hosts = new List<Host>();
        var directSteps = new List<string>();
        Task? session = null;
        bool interrupted = false, verified = false, removed = false;
        Console.WriteLine($"APP-05.7 evidence {run.Id}: {folder}");

        try
        {
            var first = await Open("Start");
            await AddDayOneTrips(run, ct);
            await Capture("opening");
            await Browser(first, "day-one");
            await Stop();

            DateOnly secondDay = App03DayBootstrap.FirstDay.AddDays(1);
            await App03DevSession.Run("NextDay", root, source, run.Id, businessDate: secondDay);
            await App03DevSession.Run("NextDay", root, source, run.Id, businessDate: secondDay);
            await InterruptUpgrade();
            var next = await Open("Resume");
            if (!await HasSuccessor("tenant-a") || !await HasSuccessor("tenant-b") || next.WebUrl != first.WebUrl)
                throw new InvalidOperationException("Plain Resume did not recover both tenant upgrades at the same origin.");
            await Capture("after-upgrade");
            await Browser(next, "day-two");

            comparisons =
            [
                .. App05DailyCloseOracle.CompareUpgrade(snapshots[1], snapshots[2]),
                .. App05DailyCloseOracle.Compare(snapshots[0], snapshots[1], snapshots[3])
            ];
            if (comparisons.Length == 0 || comparisons.Any(row => !row.Match))
                throw new InvalidOperationException("APP-05.7 SQL evidence is incomplete or differs from expectations.");
            if (direct && !directSteps.SequenceEqual(new[] { "direct-close", "direct-successor" }))
                throw new InvalidOperationException("Direct browser checkpoints are incomplete.");
            verified = true;
        }
        finally
        {
            try
            {
                try { await Stop(); }
                finally
                {
                    if (App03DevSession.HasManifest(run.Id))
                        await App03DevSession.Run("Remove", root, source, run.Id);
                }
                removed = !App03DevSession.HasManifest(run.Id) && !await run.Exists("tenant-a") && !await run.Exists("tenant-b");
                if (!removed) throw new InvalidOperationException("APP-05.7 nonce-owned removal could not be confirmed.");
            }
            finally
            {
                await Write("sql-review.json", new { runId = run.Id, source, snapshots, comparisons });
                var artifacts = new List<Artifact>();
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    await using var content = File.OpenRead(file);
                    artifacts.Add(new(Path.GetRelativePath(folder, file).Replace('\\', '/'),
                        Convert.ToHexString(await SHA256.HashDataAsync(content))));
                }
                await Write("run.json", new
                {
                    runId = run.Id, source, tree = Environment.GetEnvironmentVariable("APP05_EVIDENCE_TREE"),
                    command = "pwsh -NoProfile -File ./scripts/Run-App03Dev.ps1 -Action VerifyDailyClose",
                    scope = "synthetic DEV", instance = DemoRun.Instance, os = Environment.OSVersion.VersionString,
                    runtime = Environment.Version.ToString(), hosts, interruptedUpgrade = interrupted, removed,
                    result = verified && removed ? "PASS" : "FAIL",
                    operatorCheckpoints = direct ? verified && removed ? "PASS" : "FAIL" : "NOT_RUN",
                    ownerDirectShift = "NOT_RUN", directSteps, artifacts
                });
            }
        }
        Console.WriteLine($"APP-05.7 verified: {folder}");

        async Task Capture(string phase)
        {
            snapshots.Add(await App05DailyCloseOracle.Capture(run, phase, ct));
        }

        Task Write<T>(string file, T value) =>
            File.WriteAllTextAsync(Path.Combine(folder, file), JsonSerializer.Serialize(value, Json));

        async Task<App03DevHost> Open(string action, bool successor = false)
        {
            var started = new TaskCompletionSource<App03DevHost>(TaskCreationOptions.RunContinuationsAsynchronously);
            session = App03DevSession.Run(action, root, source, run.Id, host =>
            {
                started.SetResult(host);
                return Task.CompletedTask;
            }, tripClosure: true, successor: successor);
            if (await Task.WhenAny(started.Task, session).WaitAsync(ct) == session) await session;
            var ready = await started.Task.WaitAsync(ct);
            hosts.Add(new(action, ready.WebUrl, ready.ApiPid, ready.VitePid));
            return ready;
        }

        async Task Stop()
        {
            if (session is not { } active) return;
            if (!active.IsCompleted && EventWaitHandle.TryOpenExisting($@"Local\FmcgDms.APP03.{run.Id}", out var signal))
                using (signal) signal.Set();
            session = null;
            await active;
        }

        async Task<bool> HasSuccessor(string tenant)
        {
            await run.Verify(tenant, app03Session: true);
            await using var db = new SqlConnection(run.Connection(tenant));
            return await db.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT IIF(OBJECT_ID('dbo.App05ASuccessor','U') IS NULL,0,1);", cancellationToken: ct)) == 1;
        }

        async Task InterruptUpgrade()
        {
            if (await HasSuccessor("tenant-a") || await HasSuccessor("tenant-b"))
                throw new InvalidOperationException("Day-one predecessor already has the successor migration.");
            await run.Verify("tenant-b", app03Session: true);
            await using var blocker = new SqlConnection(run.Connection("tenant-b"));
            await blocker.OpenAsync(ct);
            await blocker.ExecuteAsync(new CommandDefinition("""
                DECLARE @result int;
                EXEC @result=sys.sp_getapplock @Resource=N'APP-03.1a migration',
                    @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=0;
                IF @result<0 THROW 51781, 'Could not hold the contrast tenant migration lock.', 1;
                """, cancellationToken: ct));
            try
            {
                await Open("Resume", successor: true);
                throw new InvalidOperationException("The guarded upgrade unexpectedly passed with its second tenant locked.");
            }
            catch (SqlException ex) when (ex.Number == 51000 &&
                ex.Message.Contains("Could not lock APP-03.1a migration.", StringComparison.Ordinal))
            {
                session = null;
                interrupted = await HasSuccessor("tenant-a") && !await HasSuccessor("tenant-b");
                if (!interrupted)
                    throw new InvalidOperationException("Expected the interrupted upgrade to commit only the first tenant.", ex);
            }
            finally
            {
                await blocker.ExecuteAsync("""
                    EXEC sys.sp_releaseapplock @Resource=N'APP-03.1a migration', @LockOwner='Session';
                    """);
            }
        }

        async Task Browser(App03DevHost host, string stage)
        {
            string checkpoint = Path.Combine(folder, stage);
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = Path.Combine(root, "web"), UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var arg in new[] { "node_modules/@playwright/test/cli.js", "test", "--config", "playwright.app05-7.config.ts", "--grep", stage + " " })
                start.ArgumentList.Add(arg);
            start.Environment["APP02C1_WEB_URL"] = host.WebUrl;
            start.Environment["APP02C1_RUN_ID"] = run.Id;
            start.Environment["APP05_DAILY_STAGE"] = stage;
            start.Environment["APP05_EVIDENCE_DIR"] = folder;
            start.Environment["APP05_DAILY_DIRECT"] = direct ? "1" : "0";
            foreach (var (role, token) in host.CloseCredentials!)
                start.Environment["APP03_" + role + "_TOKEN"] = token;
            foreach (var (role, token) in host.FinanceCredentials!)
                start.Environment["APP03_" + role + "_TOKEN"] = token;
            start.Environment["APP03_SOURCE_TOKEN"] = host.SourceToken;
            start.Environment["APP03_RECEIVER_TOKEN"] = host.ReceiverToken;
            start.Environment["APP03_PEER_WAREHOUSE_TOKEN"] = host.PeerToken;
            start.Environment["APP03_CASHIER_TOKEN"] = host.CashierToken;

            using var child = Process.Start(start) ?? throw new InvalidOperationException("Cannot start APP-05.7 Chromium.");
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            var exited = child.WaitForExitAsync();
            string? copiedCredential = null;
            string directStep = stage == "day-one" ? "direct-close" : "direct-successor";
            try
            {
                while (!File.Exists(checkpoint + ".ready") && !exited.IsCompleted)
                {
                    if (direct && copiedCredential is null && File.Exists(Path.Combine(folder, directStep + ".ready")))
                    {
                        using var prompt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, directStep + ".json"), ct));
                        Guid trip = prompt.RootElement.GetProperty("tripId").GetGuid();
                        string path = "/work/trip-close?trip=" + trip;
                        if (prompt.RootElement.GetProperty("path").GetString() != path)
                            throw new InvalidOperationException("Unexpected direct browser action URL.");
                        copiedCredential = host.CloseCredentials!["OPS"];
                        await App02c1Dev.CopyCredential(copiedCredential);
                        directSteps.Add(directStep);
                        Console.WriteLine($"Direct browser {directStep}: {host.WebUrl}{path}");
                    }
                    await Task.Delay(200, ct);
                }
                if (File.Exists(checkpoint + ".ready"))
                {
                    await Capture(stage);
                    await File.WriteAllTextAsync(checkpoint + ".checked", "", ct);
                }
                await exited.WaitAsync(ct);
                if (child.ExitCode != 0 || !File.Exists(checkpoint + ".checked"))
                    throw new InvalidOperationException($"APP-05.7 Chromium {stage} failed or omitted its SQL checkpoint; see {folder}.");
            }
            finally
            {
                try
                {
                    if (!child.HasExited) { child.Kill(entireProcessTree: true); await exited; }
                    await File.WriteAllTextAsync(checkpoint + ".log", (await stdout) + Environment.NewLine + await stderr);
                }
                finally
                {
                    if (copiedCredential is not null) await App02c1Dev.CopyCredential(copiedCredential, clear: true);
                }
            }
        }
    }

    private static async Task AddDayOneTrips(DemoRun run, CancellationToken ct)
    {
        const string tenant = "tenant-a";
        await run.Verify(tenant, app03Session: true);
        await using var db = new SqlConnection(run.Connection(tenant));
        await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        foreach (string slot in new[] { "Z", "U" })
        {
            await db.ExecuteAsync(new CommandDefinition("""
                DECLARE @vehicle uniqueidentifier=NEWID(), @trip uniqueidentifier=NEWID();
                INSERT dbo.StockLocation(Id,TenantId,Code,Kind) VALUES(@vehicle,@tenant,@VehicleCode,'VEHICLE');
                INSERT dbo.Trip(Id,TenantId,Code,BusinessDate,VehicleLocationId) VALUES(@trip,@tenant,@TripCode,@Day,@vehicle);
                INSERT dbo.PhysicalStockOpening
                    (TenantId,StockLocationId,SkuId,SnapshotCode,BaseQuantity,CostAmount,CapturedAtUtc)
                    SELECT @tenant,@vehicle,Id,'APP-05.7-DEV-v1',0,0,@Opening FROM dbo.Sku WHERE TenantId=@tenant;
                INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,OnHandBaseQuantity,OnHandCostAmount)
                    SELECT @tenant,@vehicle,Id,0,0 FROM dbo.Sku WHERE TenantId=@tenant;
                INSERT dbo.C3TripWriter(TenantId,TripId,ActorId) VALUES(@tenant,@trip,@Warehouse);
                INSERT dbo.C4DeliveryWriter(TenantId,TripId,ActorId) VALUES(@tenant,@trip,@Sales);
                INSERT dbo.D3CashierTrip(TenantId,TripId,ActorId) VALUES(@tenant,@trip,@Cashier);
                INSERT d4app.TripProfile(TripId) VALUES(@trip);
                """, new
            {
                tenant, VehicleCode = "DAY-CLOSE-TRUCK-" + slot, TripCode = "DEV-20261123-" + slot,
                Day = App03DayBootstrap.FirstDay.ToDateTime(TimeOnly.MinValue),
                Opening = App03DayBootstrap.Clock(App03DayBootstrap.FirstDay), Warehouse = DevF05Evidence.Receiver,
                Sales = App03DayBootstrap.Actor, App03SettlementBootstrap.Cashier
            }, tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
        await App05CloseBootstrap.Prepare(run, tenant, ct);
    }

    private sealed record Host(string Action, string WebUrl, int ApiPid, int VitePid);
    private sealed record Artifact(string File, string Sha256);
}
