using System.Diagnostics;
using System.Text.Json;

namespace FmcgDms.Demo;

// Diagnostic only; owned LocalDB scratch, read-only DMV observations, no business retry.
// Temporal SQL brackets do NOT establish a unique HTTP-to-SPID mapping.
internal sealed class D2PeerWitness : IAsyncDisposable
{
    private readonly string output, source, runId, direction;
    private readonly Func<(int ApiPid, int VitePid)> pids;
    private readonly DevSqlObserver?[] observers;
    private int ordinal;
    private string phase = "initial";
    private readonly AsyncLocal<string?> parentId = new();

    internal static bool Enabled => DevSqlObserver.Hosted &&
        Environment.GetEnvironmentVariable("FMCG_D2_PEER_WITNESS") == "1";

    private D2PeerWitness(string output, string source, DemoRun run, string scope,
        Func<(int ApiPid, int VitePid)> pids, DevSqlObserver?[] observers)
    {
        this.output = output; this.source = source; runId = run.Id; this.pids = pids;
        direction = scope; this.observers = observers;
    }

    internal static async Task<D2PeerWitness?> TryStart(string root, string source, DemoRun run,
        string profile, string scope, Func<(int ApiPid, int VitePid)>? pids, CancellationToken ct)
    {
        if (!Enabled || pids is null) return null;
        string? output = DevSqlObserver.EvidenceDirectory(root, run.Id, profile, scope, create: true);
        if (output is null) return null;
        var observers = await Task.WhenAll(DemoRun.Tenants.Select(tenant =>
            DevSqlObserver.Start(root, source, run.Id, profile, scope, tenant, ct)));
        var witness = new D2PeerWitness(output, source, run, scope, pids, observers);
        witness.Write($"d2-peer-{scope}-startup.json", new { schema = "D2_PEER_STARTUP_V1",
            source, runId = run.Id, direction = scope, profile, utc = DateTimeOffset.UtcNow,
            tenantAStarted = observers[0] is not null, tenantBStarted = observers[1] is not null });
        return witness;
    }

    internal void SelectPhase(string value)
    {
        if (!DevSqlObserverProtocol.IsD2Phase(value)) return;
        phase = value;
        Interlocked.Exchange(ref ordinal, 0);
    }

    internal sealed record Ticket(string Id, string Tenant, string Route, string CasePhase, int Ordinal, int ApiPid, int VitePid, string? ParentId,
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
                "/drop-ack" when request.RequestUri?.IsLoopback == true => "LOCAL_DROP_ACK",
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
                : request.Method == HttpMethod.Get && path == "/api/synthetic/app02c1/session" ? "C1_SESSION"
                : request.Method == HttpMethod.Get && path?.StartsWith("/api/synthetic/app02d2/sales/", StringComparison.Ordinal) == true
                    && path["/api/synthetic/app02d2/sales/".Length..].Split('/') is { Length: 2 } parts
                    && parts.All(p => p.Length != 0) ? "D2_LOOKUP" : null;
            if (route is null || tenant is not ("tenant-a" or "tenant-b")) return null;
            var pid = pids();
            string id = Guid.NewGuid().ToString("N");
            if (!request.Headers.TryAddWithoutValidation("X-Fmcg-D2-Peer-Witness", id)) return null;
            int index = Interlocked.Increment(ref ordinal);
            var ticket = new Ticket(id, tenant, route, phase, index, pid.ApiPid, pid.VitePid, parentId.Value,
                DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), timeout.TotalMilliseconds);
            Write($"d2-peer-{direction}-{id}-start.json", new { schema = "D2_PEER_CLIENT_V1", phase = "start",
                source, runId, direction, id, tenant, route, casePhase = phase, ordinal = index, apiPid = pid.ApiPid,
                vitePid = pid.VitePid, parentId = ticket.ParentId, startedUtc = ticket.StartedUtc, timeoutMs = ticket.TimeoutMs });
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
            parentId = ticket.ParentId,
            startedUtc = ticket.StartedUtc, endedUtc = DateTimeOffset.UtcNow,
            elapsedMs = Stopwatch.GetElapsedTime(ticket.Stamp).TotalMilliseconds,
            timeoutMs = ticket.TimeoutMs, status, outcome, callerCancelled = caller.IsCancellationRequested });
    }

    internal async Task ObserveAckProxy(HttpContext context, Func<Task> next)
    {
        string? prior = parentId.Value;
        string? id = context.Request.Headers["X-Fmcg-D2-Peer-Witness"].ToString();
        if (!Guid.TryParseExact(id, "N", out var parsed) || parsed.ToString("N") != id)
        { await next(); return; }
        parentId.Value = id;
        Emit("start");
        try { await next(); }
        finally { Emit("end"); parentId.Value = prior; }

        void Emit(string phase)
        {
            try { Console.WriteLine("D2_LOCAL_ACK " + JsonSerializer.Serialize(new { schema = "D2_LOCAL_ACK_V1",
                id, phase, pid = Environment.ProcessId, utc = DateTimeOffset.UtcNow })); } catch { SafeNotice(); }
        }
    }

    internal void Mark(string milestone)
    {
        if (milestone is not ("restart-start" or "restart-end")) return;
        Write($"d2-peer-{direction}-{phase}-{milestone}.json", new { schema = "D2_PEER_PHASE_V1",
            source, runId, direction, casePhase = phase, milestone, utc = DateTimeOffset.UtcNow });
    }

    public async ValueTask DisposeAsync()
    {
        // Each managed child has its own 4+2+2-second bounded reap. Unconfirmed
        // termination fences scratch removal, not merely a cancellation request.
        await Task.WhenAll(observers.Where(x => x is not null).Select(x => x!.DisposeAsync().AsTask()));
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
