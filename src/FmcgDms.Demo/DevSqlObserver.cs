using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

// One managed child per tenant. A child cannot open a database until its parent
// attaches the kill-on-close job and sends the start handshake. No arbitrary
// server, database, query, destination or secrets are accepted by the child.
internal sealed class DevSqlObserver : IAsyncDisposable
{
    private const string Marker = "DEV_SQL_OBSERVER_V1 ";
    private static readonly ConcurrentDictionary<string, byte> UnsafeRuns = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Stages = ["entered", "guarded", "handshake", "file-open",
        "connection-open", "ownership-verified", "visibility-verified", "sample-loop", "ready", "startedWithoutSample"];
    private readonly Process child;
    private readonly App03ChildJob job;
    private readonly string directory, runId, source, profile, scope, tenant, receipt;
    private readonly object gate = new();
    private readonly List<string> observed = [];
    private readonly TaskCompletionSource<string> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool stderrObserved, disposed;
    private int? exitCode;
    private string cleanup = "notStarted";

    private DevSqlObserver(Process child, App03ChildJob job, string directory, string source,
        string runId, string profile, string scope, string tenant, string receipt)
    {
        this.child = child; this.job = job; this.directory = directory; this.source = source;
        this.runId = runId; this.profile = profile; this.scope = scope; this.tenant = tenant; this.receipt = receipt;
    }

    internal static bool Hosted => OperatingSystem.IsWindows() &&
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" &&
        Environment.GetEnvironmentVariable("RUNNER_OS") == "Windows";

    internal static void RequireCleanupSafe(string runId)
    {
        if (UnsafeRuns.ContainsKey(runId))
            throw new InvalidOperationException("Diagnostic child exit unconfirmed; retain owned scratch for safe cleanup.");
    }

    internal static string? EvidenceDirectory(string root, string runId, string profile, string scope, bool create = false)
    {
        if (!Hosted || !DevSqlObserverProtocol.IsScope(profile, scope)) return null;
        try
        {
            _ = new DemoRun(runId);
            string full = Path.GetFullPath(root);
            string assembly = typeof(DevSqlObserver).Assembly.Location;
            string expected = Path.Combine(full, "src", "FmcgDms.Demo", "bin", "Release", "net10.0", "FmcgDms.Demo.dll");
            if (!string.Equals(assembly, expected, StringComparison.OrdinalIgnoreCase)) return null;
            foreach (string part in new[] { "TestResults", profile, runId })
            {
                full = Path.Combine(full, part);
                if (create && !Directory.Exists(full)) Directory.CreateDirectory(full);
                if (!Directory.Exists(full) || File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint)) return null;
            }
            return full;
        }
        catch { return null; }
    }

    private static (string Sample, string Receipt) Names(string profile, string scope, string tenant)
    {
        if (scope == "resume")
        {
            if (profile == "app03-6" && tenant == "tenant-a")
                return ("sql-reads.ndjson", "sql-catalog-observer.json");
            string suffix = tenant == "tenant-a" ? "a" : "b";
            return ($"sql-resume-{suffix}.ndjson", $"sql-resume-{suffix}-observer.json");
        }
        return ($"d2-peer-{scope}-{tenant}-sql.ndjson", $"d2-peer-{scope}-{tenant}-observer.json");
    }

    internal static async Task<DevSqlObserver?> Start(string root, string source, string runId,
        string profile, string scope, string tenant, CancellationToken ct)
    {
        string? directory = EvidenceDirectory(root, runId, profile, scope);
        if (directory is null || tenant is not ("tenant-a" or "tenant-b")) return null;
        var names = Names(profile, scope, tenant);
        if (File.Exists(Path.Combine(directory, names.Sample)) || File.Exists(Path.Combine(directory, names.Receipt))) return null;
        var startedUtc = DateTimeOffset.UtcNow;
        long stamp = Stopwatch.GetTimestamp();
        Process? process = null;
        App03ChildJob? job = null;
        DevSqlObserver? observer = null;
        string status = "unavailable", failure = "none";
        int? pid = null;
        try
        {
            string dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? throw new InvalidOperationException();
            var info = new ProcessStartInfo(Path.Combine(dotnetRoot, "dotnet.exe"))
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string arg in new[] { typeof(DevSqlObserver).Assembly.Location, "DevSqlObserver",
                root, source, runId, profile, scope, tenant })
                info.ArgumentList.Add(arg);
            info.Environment.Clear();
            foreach (string name in new[] { "SystemRoot", "LOCALAPPDATA", "USERPROFILE", "TEMP", "TMP", "DOTNET_ROOT" })
                if (Environment.GetEnvironmentVariable(name) is string value) info.Environment[name] = value;
            info.Environment["GITHUB_ACTIONS"] = "true";
            info.Environment["RUNNER_OS"] = "Windows";
            info.Environment["FMCG_DEV_SQL_CHILD"] = "1";
            job = new App03ChildJob();
            process = Process.Start(info) ?? throw new InvalidOperationException();
            pid = process.Id;
            job.Attach(process);
            observer = new(process, job, directory, source, runId, profile, scope, tenant, names.Receipt);
            process.OutputDataReceived += (_, e) => observer.OnOutput(e.Data);
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) lock (observer.gate) observer.stderrObserved = true;
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startup.CancelAfter(TimeSpan.FromSeconds(scope == "resume" ? 8 : 4));
            await process.StandardInput.WriteLineAsync("start").WaitAsync(startup.Token);
            await process.StandardInput.FlushAsync().WaitAsync(startup.Token);
            var exit = process.WaitForExitAsync(startup.Token);
            if (await Task.WhenAny(observer.ready.Task, exit).WaitAsync(startup.Token) == observer.ready.Task &&
                !process.HasExited && DevSqlObserverProtocol.VerifyStartup(Path.Combine(directory, names.Sample), runId, profile, scope, tenant, process.Id,
                    out string sampleStatus) && sampleStatus == observer.ready.Task.Result)
            {
                status = sampleStatus;
                return observer;
            }
            failure = "missingStartupProof";
        }
        catch (OperationCanceledException) { status = ct.IsCancellationRequested ? "callerCancelled" : "startupTimeout"; }
        catch (Exception ex)
        {
            failure = ex switch
            {
                System.ComponentModel.Win32Exception => "nativeProcess",
                IOException => "io",
                UnauthorizedAccessException => "access",
                _ => "other"
            };
        }
        finally
        {
            if (status is not ("ready" or "startedWithoutSample"))
            {
                if (observer is not null) await observer.DisposeAsync();
                else if (process is not null)
                {
                    job?.Dispose();
                    try
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                    }
                    catch { UnsafeRuns.TryAdd(runId, 0); }
                    finally { process.Dispose(); }
                }
                else job?.Dispose();
            }
            try
            {
                string[] stages = [];
                bool stderr = false;
                if (observer is not null)
                    lock (observer.gate) { stages = observer.observed.ToArray(); stderr = observer.stderrObserved; }
                using var file = File.Open(Path.Combine(directory, names.Receipt), FileMode.CreateNew);
                JsonSerializer.Serialize(file, new
                {
                    schema = scope == "resume" ? "APP03_SETTLEMENT_SQL_OBSERVER_V1" : "DEV_SQL_OBSERVER_V1",
                    source, runId, profile, scope, tenant, pid, status, startedUtc,
                    endedUtc = DateTimeOffset.UtcNow, elapsedMs = Stopwatch.GetElapsedTime(stamp).TotalMilliseconds,
                    startupStages = stages, stderrObserved = stderr, startupFailure = failure,
                    cleanup = observer?.cleanup ?? "notStarted", cleanupExitCode = observer?.exitCode,
                    implementation = "managedChild"
                });
            }
            catch { Notice(); }
        }
        return null;
    }

    private void OnOutput(string? line)
    {
        if (line is null || !line.StartsWith(Marker, StringComparison.Ordinal)) return;
        string stage = line[Marker.Length..];
        if (!Stages.Contains(stage, StringComparer.Ordinal)) return;
        lock (gate) { if (!observed.Contains(stage, StringComparer.Ordinal)) observed.Add(stage); }
        if (stage is "ready" or "startedWithoutSample") ready.TrySetResult(stage);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            cleanup = await DevSqlObserverProtocol.Reap(
                () => child.StandardInput.Close(), job.Dispose,
                () => { if (!child.HasExited) child.Kill(entireProcessTree: true); },
                () => child.WaitForExitAsync(), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2));
            if (cleanup == "unconfirmed") { UnsafeRuns.TryAdd(runId, 0); Notice(); }
            else exitCode = child.ExitCode;
        }
        catch
        {
            cleanup = "unconfirmed";
            UnsafeRuns.TryAdd(runId, 0);
            Notice();
        }
        finally
        {
            job.Dispose();
            child.Dispose();
            try
            {
                using var file = File.Open(Path.Combine(directory, receipt.Replace("-observer.json", "-cleanup.json", StringComparison.Ordinal)), FileMode.CreateNew);
                JsonSerializer.Serialize(file, new { schema = "DEV_SQL_OBSERVER_CLEANUP_V1", source, runId, profile, scope, tenant,
                    cleanup, exitCode, utc = DateTimeOffset.UtcNow });
            }
            catch { Notice(); }
        }
    }

    internal static async Task<int> RunChild(string root, string source, string runId, string profile, string scope, string tenant)
    {
        if (!Hosted || Environment.GetEnvironmentVariable("FMCG_DEV_SQL_CHILD") != "1") return 2;
        Emit("entered");
        string? directory = EvidenceDirectory(root, runId, profile, scope);
        if (directory is null || tenant is not ("tenant-a" or "tenant-b")) return 2;
        Emit("guarded");
        if (Console.ReadLine() != "start") return 2;
        Emit("handshake");
        // Read the parent pipe off the SQL sampling path. EOF means graceful stop;
        // the job object handles parent death/forced cleanup.
        var stop = Task.Run(Console.ReadLine);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(scope == "resume" ? 240 : 90));
        var ct = deadline.Token;
        var run = new DemoRun(runId);
        var names = Names(profile, scope, tenant);
        StreamWriter? writer = null;
        string phase = "file", reason = "unavailable", finalSampleStatus = "notAttempted";
        long stamp = Stopwatch.GetTimestamp();
        try
        {
            writer = new StreamWriter(File.Open(Path.Combine(directory, names.Sample), FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            Row(new { @event = "start", schema = "DEV_SQL_OBSERVER_V1", source, runId, profile, scope, tenant,
                pid = Environment.ProcessId, observerPid = Environment.ProcessId, utc = DateTimeOffset.UtcNow, observerMs = 0 });
            Emit("file-open");
            phase = "connection";
            var settings = new SqlConnectionStringBuilder(run.Connection(tenant))
            { ConnectTimeout = 2, ConnectRetryCount = 0, Pooling = false, ApplicationName = "DEV read observer" };
            await using var db = new SqlConnection(settings.ConnectionString);
            await db.OpenAsync(ct);
            Emit("connection-open");
            phase = "ownership";
            const string ownership = """
                SELECT CASE WHEN DB_NAME()=@name
                    AND (SELECT COUNT(*) FROM dbo.LocalDemoOwner o JOIN dbo.App00ScratchMarker m ON m.Nonce=o.RunId WHERE o.RunId=@id)=1
                    AND (SELECT COUNT(*) FROM dbo.AppTenantBinding WHERE Slot=1 AND TenantId=@tenant)=1
                    THEN 1 ELSE 0 END
                """;
            var identity = new { name = run.Name(tenant), id = run.Id, tenant };
            if (await db.ExecuteScalarAsync<int>(new CommandDefinition(ownership, identity, commandTimeout: 1, cancellationToken: ct)) != 1)
                throw new InvalidOperationException();
            if (scope == "resume" &&
                await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                    SELECT COUNT(*) FROM dbo.LocalApp03Session WHERE RunId=@id AND TenantId=@tenant
                    """, identity, commandTimeout: 1, cancellationToken: ct)) != 1)
                throw new InvalidOperationException();
            Emit("ownership-verified");
            phase = "visibility";
            if (await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT CASE WHEN IS_SRVROLEMEMBER('sysadmin')=1 OR
                    CASE WHEN TRY_CONVERT(int,SERVERPROPERTY('ProductMajorVersion'))>=16
                    THEN HAS_PERMS_BY_NAME(NULL,NULL,'VIEW SERVER PERFORMANCE STATE')
                    ELSE HAS_PERMS_BY_NAME(NULL,NULL,'VIEW SERVER STATE') END=1 THEN 1 ELSE 0 END
                """, commandTimeout: 1, cancellationToken: ct)) != 1)
                throw new InvalidOperationException();
            Emit("visibility-verified");
            phase = "sample";
            Row(new { @event = "available", sampleIntervalMs = 250, commandTimeoutMs = 1000, utc = DateTimeOffset.UtcNow });
            Emit("sample-loop");
            bool first = true;
            reason = "deadline";
            while (!ct.IsCancellationRequested)
            {
                if (writer.BaseStream.Position >= 2 * 1024 * 1024 - 64 * 1024) { reason = "sizeLimit"; break; }
                bool finalSample = stop.IsCompleted;
                DateTimeOffset begin = DateTimeOffset.UtcNow;
                string sampleStatus;
                try
                {
                    var rows = (await db.QueryAsync<SqlRow>(new CommandDefinition(SampleSql, commandTimeout: 1, cancellationToken: ct))).ToArray();
                    foreach (var row in rows)
                        if (row.WaitType.Length > 80 || row.WaitType.Any(c => !(c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'))) row.WaitType = "other";
                    bool truncated = rows.Length > 32;
                    Row(new { @event = "sample", startedUtc = begin, utc = DateTimeOffset.UtcNow,
                        observerMs = Stopwatch.GetElapsedTime(stamp).TotalMilliseconds,
                        truncated, finalSample, rows = rows.Take(32).ToArray() });
                    sampleStatus = truncated ? "startedWithoutSample" : "ready";
                    if (finalSample) finalSampleStatus = truncated ? "truncated" : "usable";
                }
                catch (SqlException ex) when (ex.Number == -2 && db.State == System.Data.ConnectionState.Open)
                {
                    Row(new { @event = "sampleUnavailable", startedUtc = begin, utc = DateTimeOffset.UtcNow,
                        sqlNumber = ex.Number, reason = "commandTimeout", finalSample });
                    sampleStatus = "startedWithoutSample";
                    if (finalSample) finalSampleStatus = "unavailable";
                }
                if (first) { Emit(sampleStatus); first = false; }
                if (finalSample) { reason = "parentEnded"; break; }
                await Task.WhenAny(stop, Task.Delay(250, ct));
            }
            Row(new { @event = "stop", reason, finalSampleStatus, utc = DateTimeOffset.UtcNow });
            return reason == "parentEnded" ? 0 : 1;
        }
        catch (Exception ex)
        {
            try { Row(new { @event = "unavailable", phase, sqlNumber = ex is SqlException sql ? (int?)sql.Number : null, utc = DateTimeOffset.UtcNow }); } catch { }
            return 1;
        }
        finally { writer?.Dispose(); }

        void Row(object row)
        {
            if (writer is null) return;
            writer.WriteLine(JsonSerializer.Serialize(row, Json));
            writer.Flush();
        }
    }

    private static void Emit(string stage) => Console.WriteLine(Marker + stage);
    private static void Notice() { try { Console.Error.WriteLine("DEV SQL observer evidence/cleanup unavailable."); } catch { } }

    private sealed class SqlRow
    {
        public int Spid { get; set; }
        public int RequestId { get; set; }
        public string WaitType { get; set; } = "";
        public int WaitMs { get; set; }
        public int ElapsedMs { get; set; }
        public int CpuMs { get; set; }
        public long LogicalReads { get; set; }
        public string BlockerScope { get; set; } = "";
        public int? BlockerSpid { get; set; }
        public int? BlockerTransactions { get; set; }
        public bool BlockerSleeping { get; set; }
        public string Family { get; set; } = "";
        public string LockKind { get; set; } = "";
        public string LockObject { get; set; } = "";
    }

    private const string SampleSql = """
        SELECT TOP (33) CAST(r.session_id AS int) AS spid, r.request_id AS requestId,
            COALESCE(r.wait_type, '') AS waitType, r.wait_time AS waitMs, r.total_elapsed_time AS elapsedMs,
            r.cpu_time AS cpuMs, r.logical_reads AS logicalReads,
            CASE WHEN COALESCE(r.blocking_session_id,0)=0 THEN 'none'
                 WHEN b.session_id IS NULL THEN 'unresolved' ELSE 'sameDatabase' END AS blockerScope,
            CAST(b.session_id AS int) AS blockerSpid, b.open_transaction_count AS blockerTransactions,
            CAST(CASE WHEN b.status='sleeping' THEN 1 ELSE 0 END AS bit) AS blockerSleeping,
            CASE WHEN txt.text LIKE '%SELECT t.Id AS TripId,t.Code AS Label,%'
                      AND txt.text LIKE '%dbo.C4DeliveryWriter%' THEN 'D2TripList'
                 WHEN txt.text LIKE '%SELECT t.Id,t.Code,CASE WHEN @Field=1%' AND txt.text LIKE '%dbo.C4DeliveryWriter%' THEN 'C4TripList'
                 WHEN txt.text LIKE '%FROM dbo.App02ActorRoleGrant WITH(UPDLOCK,HOLDLOCK)%' THEN 'RoleCheck'
                 WHEN txt.text LIKE '%SELECT Nonce FROM dbo.App00ScratchMarker%' AND txt.text LIKE '%dbo.C3TripWriter%' THEN 'D1Catalog'
                 WHEN txt.text LIKE '%FROM dbo.Customer WHERE TenantId=@Tenant AND IsActive=1 ORDER BY Code%'
                      AND txt.text LIKE '%@CurrentDayOnly%' AND txt.text LIKE '%@Settlement%' THEN 'C2Catalog'
                 WHEN txt.text LIKE '%SELECT u.SkuId,u.UomCode,u.Factor,u.Step FROM dbo.F03Unit u%' THEN 'C2Units'
                 WHEN txt.text LIKE '%FROM dbo.SaleDelivery%' THEN 'SaleDelivery'
                 WHEN txt.text LIKE '%dbo.BusinessSource%' THEN 'BusinessSource'
                 WHEN txt.text LIKE '%dbo.TripManifestLine%' THEN 'Manifest'
                 WHEN txt.text LIKE '%FROM dbo.App02ActorRoleGrant WITH (HOLDLOCK)%'
                      AND txt.text LIKE '%@Sales%' AND txt.text LIKE '%@Warehouse%' THEN 'C2RoleCheck'
                 ELSE 'Other' END AS family,
            COALESCE(l.kind, 'none') AS lockKind, COALESCE(l.obj, 'none') AS lockObject
        FROM sys.dm_exec_requests r
        LEFT JOIN sys.dm_exec_sessions b ON b.session_id=r.blocking_session_id AND b.database_id=DB_ID()
        OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) txt
        OUTER APPLY (
            SELECT TOP (1)
                CASE WHEN locks.resource_type IN ('KEY','RID','PAGE','OBJECT','APPLICATION') THEN locks.resource_type ELSE 'other' END AS kind,
                CASE COALESCE(OBJECT_NAME(p.object_id,DB_ID()),
                        CASE WHEN locks.resource_type='OBJECT' THEN OBJECT_NAME(TRY_CONVERT(int,locks.resource_associated_entity_id),DB_ID()) END)
                    WHEN 'App02ActorRoleGrant' THEN 'App02ActorRoleGrant' WHEN 'Trip' THEN 'Trip'
                    WHEN 'Customer' THEN 'Customer' WHEN 'Sku' THEN 'Sku'
                    WHEN 'StockLocation' THEN 'StockLocation' WHEN 'StockPool' THEN 'StockPool'
                    WHEN 'F03Unit' THEN 'F03Unit' ELSE 'other' END AS obj
            FROM sys.dm_tran_locks locks
            LEFT JOIN sys.partitions p ON locks.resource_type IN ('KEY','RID','PAGE')
                AND p.hobt_id=locks.resource_associated_entity_id
            WHERE locks.resource_database_id=DB_ID() AND locks.request_session_id=r.session_id
                AND locks.request_status IN ('WAIT','CONVERT','LOW_PRIORITY_WAIT','LOW_PRIORITY_CONVERT')
            ORDER BY locks.resource_type
        ) l
        WHERE r.database_id=DB_ID() AND r.session_id<>@@SPID AND r.session_id>0
        ORDER BY CASE WHEN r.blocking_session_id<>0 THEN 0 ELSE 1 END, r.total_elapsed_time DESC, r.session_id;
        """;
}
