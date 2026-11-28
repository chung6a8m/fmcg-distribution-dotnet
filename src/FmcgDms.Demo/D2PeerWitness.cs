using System.Diagnostics;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

// Diagnostic only; owned LocalDB scratch, read-only DMV observations, no business retry.
// Temporal SQL brackets do NOT establish a unique HTTP-to-SPID mapping.
internal sealed class D2PeerWitness : IAsyncDisposable
{
    private readonly string output, source, runId, direction;
    private readonly Func<(int ApiPid, int VitePid)> pids;
    private readonly CancellationTokenSource deadline;
    private readonly TaskCompletionSource<bool> stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool>[] ready =
    [
        new(TaskCreationOptions.RunContinuationsAsynchronously),
        new(TaskCreationOptions.RunContinuationsAsynchronously)
    ];
    private readonly Task[] observers;
    private int ordinal;
    private string phase = "origin-seed";

    internal static bool Enabled => OperatingSystem.IsWindows() &&
        Environment.GetEnvironmentVariable("FMCG_D2_PEER_WITNESS") == "1" &&
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" &&
        Environment.GetEnvironmentVariable("RUNNER_OS") == "Windows";

    private D2PeerWitness(string output, string source, DemoRun run, string tenant,
        Func<(int ApiPid, int VitePid)> pids, CancellationToken ct)
    {
        this.output = output; this.source = source; runId = run.Id; this.pids = pids;
        direction = tenant == "tenant-a" ? "A-to-B" : "B-to-A";
        deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        observers = [ObserveSql(run, "tenant-a", ready[0]), ObserveSql(run, "tenant-b", ready[1])];
    }

    internal static async Task<D2PeerWitness?> TryStart(string root, string output, string source,
        DemoRun run, string tenant, Func<(int ApiPid, int VitePid)>? pids, CancellationToken ct)
    {
        if (!Enabled || pids is null || tenant is not ("tenant-a" or "tenant-b")) return null;
        try
        {
            string expected = Path.GetFullPath(root);
            foreach (string part in new[] { "TestResults", "app02d2", run.Id })
            {
                expected = Path.Combine(expected, part);
                if (!Directory.Exists(expected) || File.GetAttributes(expected).HasFlag(FileAttributes.ReparsePoint))
                    return null;
            }
            if (!string.Equals(Path.GetFullPath(output), expected, StringComparison.OrdinalIgnoreCase)) return null;
            var witness = new D2PeerWitness(expected, source, run, tenant, pids, ct);
            bool?[] availability = [null, null];
            try
            {
                await Task.WhenAll(witness.ready.Select(x => x.Task)).WaitAsync(TimeSpan.FromSeconds(4), ct);
            }
            catch { /* Missing startup proof stays incomplete; do not alter the case. */ }
            for (int i = 0; i < availability.Length; i++)
                if (witness.ready[i].Task.IsCompletedSuccessfully) availability[i] = witness.ready[i].Task.Result;
            witness.Write($"d2-peer-{witness.direction}-startup.json", new { schema = "D2_PEER_STARTUP_V1",
                source, runId = run.Id, direction = witness.direction, utc = DateTimeOffset.UtcNow,
                tenantAReady = availability[0], tenantBReady = availability[1] });
            return witness;
        }
        catch
        {
            SafeNotice();
            return null;
        }
    }

    internal void SelectPhase(string value)
    {
        if (value is not ("origin-seed" or "peer-seed" or "peer-checks")) return;
        phase = value;
        ordinal = 0;
    }

    internal sealed record Ticket(string Id, string Tenant, string Route, string CasePhase, int Ordinal, int ApiPid, int VitePid,
        DateTimeOffset StartedUtc, long Stamp, double TimeoutMs);

    internal Ticket? Begin(string tenant, HttpRequestMessage request, TimeSpan timeout)
    {
        try
        {
            // Includes the existing C/D HTTP requests while building both PeerCase fixtures.
            string? path = request.RequestUri?.AbsolutePath;
            string? route = request.Method == HttpMethod.Post ? path switch
            {
                "/api/synthetic/app02d2/sales" => "D2_POST",
                "/api/synthetic/app02c1/ship-to-selections" => "C1_SHIP_TO",
                "/api/synthetic/app02c2/drafts" => "C2_DRAFT",
                "/api/synthetic/app02c2/confirm" => "C2_CONFIRM",
                "/api/synthetic/app02c2/reserve" => "C2_RESERVE",
                "/api/synthetic/app02d1/quota" => "D1_QUOTA",
                "/api/synthetic/app02d1/credit" => "D1_CREDIT",
                "/api/synthetic/app02d1/load" => "D1_LOAD",
                "/api/synthetic/app02c3/load" => "C3_LOAD",
                _ => null
            }
                : request.Method == HttpMethod.Get && path?.StartsWith("/api/synthetic/app02d2/sales/", StringComparison.Ordinal) == true
                    && path["/api/synthetic/app02d2/sales/".Length..].Split('/') is { Length: 2 } parts
                    && parts.All(p => p.Length != 0) ? "D2_LOOKUP" : null;
            if (route is null || tenant is not ("tenant-a" or "tenant-b")) return null;
            var pid = pids();
            string id = Guid.NewGuid().ToString("N");
            if (!request.Headers.TryAddWithoutValidation("X-Fmcg-D2-Peer-Witness", id)) return null;
            int index = ++ordinal;
            var ticket = new Ticket(id, tenant, route, phase, index, pid.ApiPid, pid.VitePid,
                DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), timeout.TotalMilliseconds);
            Write($"d2-peer-{direction}-{id}-start.json", new { schema = "D2_PEER_CLIENT_V1", phase = "start",
                source, runId, direction, id, tenant, route, casePhase = phase, ordinal = index, apiPid = pid.ApiPid,
                vitePid = pid.VitePid, startedUtc = ticket.StartedUtc, timeoutMs = ticket.TimeoutMs });
            return ticket;
        }
        catch { SafeNotice(); return null; }
    }

    internal void End(Ticket? ticket, int? status, Exception? failure, CancellationToken caller)
    {
        if (ticket is null) return;
        string outcome = failure switch
        {
            null => "RESPONSE_PARSED",
            OperationCanceledException when caller.IsCancellationRequested => "CALLER_CANCELLED",
            OperationCanceledException ex when ex.InnerException is TimeoutException => "HTTP_TIMEOUT",
            OperationCanceledException => "CANCELLED_UNKNOWN",
            HttpRequestException ex when ex.StatusCode.HasValue => "HTTP_STATUS_FAILURE",
            HttpRequestException => "HTTP_TRANSPORT_FAILURE",
            _ when status.HasValue => "RESPONSE_PROCESSING_FAILURE",
            _ => "UNEXPECTED_FAILURE"
        };
        Write($"d2-peer-{direction}-{ticket.Id}-end.json", new { schema = "D2_PEER_CLIENT_V1", phase = "end",
            source, runId, direction, id = ticket.Id, tenant = ticket.Tenant, route = ticket.Route,
            casePhase = ticket.CasePhase, ordinal = ticket.Ordinal, apiPid = ticket.ApiPid, vitePid = ticket.VitePid,
            startedUtc = ticket.StartedUtc, endedUtc = DateTimeOffset.UtcNow,
            elapsedMs = Stopwatch.GetElapsedTime(ticket.Stamp).TotalMilliseconds,
            timeoutMs = ticket.TimeoutMs, status, outcome, callerCancelled = caller.IsCancellationRequested });
    }

    private async Task ObserveSql(DemoRun run, string tenant, TaskCompletionSource<bool> started)
    {
        StreamWriter? writer = null;
        string phase = "file", reason = "unavailable";
        CancellationToken ct = deadline.Token;
        try
        {
            writer = new StreamWriter(File.Open(Path.Combine(output, $"d2-peer-{direction}-{tenant}-sql.ndjson"),
                FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            Row(new { @event = "start", schema = "D2_PEER_SQL_V1", source, runId, direction, tenant,
                observerPid = Environment.ProcessId, utc = DateTimeOffset.UtcNow });
            phase = "connection";
            var connection = new SqlConnectionStringBuilder(run.Connection(tenant))
            { ConnectTimeout = 2, ConnectRetryCount = 0, Pooling = false, ApplicationName = "D2 peer read observer" };
            await using var db = new SqlConnection(connection.ConnectionString);
            await db.OpenAsync(ct);
            phase = "ownership";
            int owner = await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT CASE WHEN DB_NAME()=@name
                    AND (SELECT COUNT(*) FROM dbo.LocalDemoOwner o JOIN dbo.App00ScratchMarker m ON m.Nonce=o.RunId
                         WHERE o.RunId=@id)=1
                    AND (SELECT COUNT(*) FROM dbo.AppTenantBinding WHERE Slot=1 AND TenantId=@tenant)=1
                    THEN 1 ELSE 0 END
                """, new { name = run.Name(tenant), id = run.Id, tenant }, commandTimeout: 1, cancellationToken: ct));
            if (owner != 1) throw new InvalidOperationException();
            phase = "visibility";
            int visibility = await db.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT CASE WHEN IS_SRVROLEMEMBER('sysadmin')=1 OR
                    CASE WHEN TRY_CONVERT(int,SERVERPROPERTY('ProductMajorVersion'))>=16
                    THEN HAS_PERMS_BY_NAME(NULL,NULL,'VIEW SERVER PERFORMANCE STATE')
                    ELSE HAS_PERMS_BY_NAME(NULL,NULL,'VIEW SERVER STATE') END=1 THEN 1 ELSE 0 END
                """, commandTimeout: 1, cancellationToken: ct));
            if (visibility != 1) throw new InvalidOperationException();
            phase = "sample";
            Row(new { @event = "available", sampleIntervalMs = 250, commandTimeoutMs = 1000, utc = DateTimeOffset.UtcNow });
            bool first = true;
            reason = "deadlineOrCancellation";
            while (!ct.IsCancellationRequested)
            {
                if (writer.BaseStream.Position >= 2 * 1024 * 1024 - 64 * 1024) { reason = "sizeLimit"; break; }
                bool finalSample = stop.Task.IsCompleted;
                var begin = DateTimeOffset.UtcNow;
                try
                {
                    var rows = (await db.QueryAsync<SqlRow>(new CommandDefinition(SampleSql,
                        commandTimeout: 1, cancellationToken: ct))).ToArray();
                    Row(new { @event = "sample", startedUtc = begin, utc = DateTimeOffset.UtcNow,
                        truncated = rows.Length > 32, finalSample, rows = rows.Take(32).ToArray() });
                    if (first) { started.TrySetResult(rows.Length <= 32); first = false; }
                }
                catch (SqlException ex) when (ex.Number == -2 && db.State == System.Data.ConnectionState.Open)
                {
                    Row(new { @event = "sampleUnavailable", startedUtc = begin, utc = DateTimeOffset.UtcNow,
                        sqlNumber = ex.Number, finalSample });
                    if (first) { started.TrySetResult(false); first = false; }
                }
                if (finalSample) { reason = "parentEnded"; break; }
                await Task.WhenAny(stop.Task, Task.Delay(250, ct));
            }
        }
        catch (Exception ex)
        {
            reason = ct.IsCancellationRequested ? "deadlineOrCancellation" : "unavailable";
            try { Row(new { @event = "unavailable", phase, sqlNumber = ex is SqlException sql ? (int?)sql.Number : null,
                utc = DateTimeOffset.UtcNow }); } catch { }
        }
        finally
        {
            started.TrySetResult(false);
            try { Row(new { @event = "stop", reason, utc = DateTimeOffset.UtcNow }); } catch { }
            try { writer?.Dispose(); } catch { SafeNotice(); }
        }

        void Row(object row)
        {
            if (writer is null) return;
            writer.WriteLine(JsonSerializer.Serialize(row));
            writer.Flush();
        }
    }

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
        public string FamilyHint { get; set; } = "";
    }

    private const string SampleSql = """
        SELECT TOP (33) CAST(r.session_id AS int) Spid, r.request_id RequestId,
            COALESCE(r.wait_type,'') WaitType, r.wait_time WaitMs, r.total_elapsed_time ElapsedMs,
            r.cpu_time CpuMs, r.logical_reads LogicalReads,
            CASE WHEN COALESCE(r.blocking_session_id,0)=0 THEN 'none'
                 WHEN b.session_id IS NULL THEN 'unresolved' ELSE 'sameDatabase' END BlockerScope,
            CAST(b.session_id AS int) BlockerSpid, b.open_transaction_count BlockerTransactions,
            CASE WHEN txt.text LIKE '%dbo.App02ActorRoleGrant%' THEN 'RoleCheck'
                 WHEN txt.text LIKE '%dbo.TripManifestLine%' THEN 'Manifest'
                 WHEN txt.text LIKE '%dbo.SaleDelivery%' THEN 'SaleDelivery'
                 WHEN txt.text LIKE '%dbo.ReceiptAllocation%' THEN 'ReceiptAllocation'
                 WHEN txt.text LIKE '%dbo.Customer%' THEN 'Customer'
                 WHEN txt.text LIKE '%dbo.Trip%' THEN 'Trip'
                 ELSE 'Other' END FamilyHint
        FROM sys.dm_exec_requests r
        LEFT JOIN sys.dm_exec_sessions b ON b.session_id=r.blocking_session_id AND b.database_id=DB_ID()
        OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) txt
        WHERE r.database_id=DB_ID() AND r.session_id<>@@SPID AND r.session_id>0
        ORDER BY CASE WHEN r.blocking_session_id<>0 THEN 0 ELSE 1 END,r.total_elapsed_time DESC,r.session_id;
        """;

    public async ValueTask DisposeAsync()
    {
        stop.TrySetResult(true);
        // At most one final bounded read per live observer. Wait before scratch cleanup.
        try
        {
            try { await Task.WhenAll(observers).WaitAsync(TimeSpan.FromSeconds(4)); }
            catch (TimeoutException) { deadline.Cancel(); await Task.WhenAll(observers); }
        }
        catch { SafeNotice(); }
        finally { deadline.Dispose(); }
    }

    private void Write(string filename, object row)
    {
        try
        {
            using var file = File.Open(Path.Combine(output, filename), FileMode.CreateNew);
            JsonSerializer.Serialize(file, row);
        }
        catch { SafeNotice(); }
    }

    private static void SafeNotice()
    {
        try { Console.Error.WriteLine("D2 peer diagnostic evidence unavailable."); } catch { }
    }
}
