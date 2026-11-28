using System.Globalization;
using System.Xml.Linq;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Tests;

internal sealed record Rel75Edge(int OwnerSpid, int WaiterSpid, string OwnerMode, string WaiterMode);
internal sealed record Rel75Resource(string Kind, string Table, long? HobtId, Rel75Edge[] Edges);
internal sealed record Rel75Graph(int HostPid, int DatabaseId, int[] Spids, int[] VictimSpids, Rel75Resource[] Resources, bool Cycle)
{
    public long? EventUtcMs { get; init; }
}
internal sealed record Rel75XeObservation(int DatabaseId, long TotalEventsProcessed, long EventCount,
    long TargetDroppedCount, int SessionDroppedEvents, int SessionDroppedBuffers,
    int LargestDroppedEventSize, int UnprojectedReports, bool Truncated, Rel75Graph[] Graphs)
{
    public bool Complete => !Truncated && TotalEventsProcessed == EventCount && TargetDroppedCount == 0
        && SessionDroppedEvents == 0 && SessionDroppedBuffers == 0 && LargestDroppedEventSize == 0
        && UnprojectedReports == 0 && Graphs.LongLength == EventCount;
}

/// <summary>Project the engine's XML witness in memory; never persist XML, input
/// buffers, SQL text, host names, resource keys, file paths or arbitrary attributes.</summary>
internal static class Rel75DeadlockWitness
{
    private static readonly string[] Modes =
        ["S", "U", "X", "IS", "IU", "IX", "SIX", "SIU", "UIX", "Sch-S", "Sch-M",
        "RangeS-S", "RangeS-U", "RangeI-N", "RangeI-S", "RangeI-U", "RangeI-X",
        "RangeX-S", "RangeX-U", "RangeX-X", "BU", "NL"];
    private static readonly string[] Kinds =
        ["keylock", "pagelock", "objectlock", "ridlock", "applicationlock",
        "exchangeEvent", "metadatalock", "rowgrouplock", "hobtlock"];
    private static readonly string[] Tables =
        ["App02EventReceipt", "App02EventProgress", "App02ActorRoleGrant",
        "App02AcceptanceApplication", "App02OutboxMessage", "App02OutboxDelivery",
        "App02SyntheticConsumerInbox", "CashHandoverOffer", "BusinessSource",
        "BusinessCommand", "CashierCountEvidence", "Trip", "LedgerEffect"];

    internal static Rel75Graph[] Project(string xml, int dbId, int hostPid)
    {
        if (xml.Length > 4_000_000) throw new InvalidOperationException("REL75_XE_LIMIT");
        return XDocument.Parse(xml).Descendants("deadlock")
            .Select(g => ProjectOne(g, dbId, hostPid))
            .OfType<Rel75Graph>().ToArray();
    }

    // Read the complete bounded buffer once for all observed child PIDs.
    // No unprojectable/evicted/dropped report is silently converted into zero events.
    internal static Rel75XeObservation ProjectBuffer(string xml, int dbId, int[] hostPids,
        int droppedEvents, int droppedBuffers, int largestDroppedEventSize)
    {
        if (xml.Length > 4_000_000) throw new InvalidOperationException("REL75_XE_LIMIT");
        XElement root = XDocument.Parse(xml).Root
            ?? throw new InvalidOperationException("REL75_XE_ROOT_MISSING");
        if (root.Name.LocalName != "RingBufferTarget")
            throw new InvalidOperationException("REL75_XE_WRONG_TARGET");
        long Count(string key) => long.TryParse((string?)root.Attribute(key),
            NumberStyles.None, CultureInfo.InvariantCulture, out long n) && n >= 0
            ? n : throw new InvalidOperationException("REL75_XE_COUNTER_MISSING");
        string? truncated = (string?)root.Attribute("truncated");
        if (truncated is not ("0" or "1"))
            throw new InvalidOperationException("REL75_XE_TRUNCATION_UNKNOWN");
        long total = Count("totalEventsProcessed"), count = Count("eventCount"), dropped = Count("droppedCount");
        var events = root.Elements("event").ToArray();
        var graphs = new List<Rel75Graph>();
        int unprojected = 0;
        foreach (var e in events)
        {
            var deadlocks = e.Descendants("deadlock").ToArray();
            if ((string?)e.Attribute("name") != "xml_deadlock_report" || deadlocks.Length != 1
                || !DateTimeOffset.TryParse((string?)e.Attribute("timestamp"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var timestamp))
            { unprojected++; continue; }
            var candidates = hostPids.Distinct().Select(pid => ProjectOne(deadlocks[0], dbId, pid))
                .OfType<Rel75Graph>().ToArray();
            if (candidates.Length != 1 || !candidates[0].Cycle)
            { unprojected++; continue; }
            graphs.Add(candidates[0] with { EventUtcMs = timestamp.ToUnixTimeMilliseconds() });
        }
        if (events.LongLength != count)
            throw new InvalidOperationException("REL75_XE_EVENT_COUNT_MISMATCH");
        return new(dbId, total, count, dropped, droppedEvents, droppedBuffers,
            largestDroppedEventSize, unprojected, truncated == "1", graphs.ToArray());
    }

    private static int Number(XElement e, string name) =>
        int.TryParse((string?)e.Attribute(name), out int value) ? value : -1;

    private static string Mode(XElement node) =>
        Modes.FirstOrDefault(m => m == (string?)node.Attribute("mode")) ?? "OTHER";

    private static Rel75Graph? ProjectOne(XElement graph, int dbId, int hostPid)
    {
        var processes = graph.Element("process-list")?.Elements("process").ToArray() ?? [];
        // Strict scope: every participant must be in the exact new OS process
        // and its target database. A graph from the peer or another app is not evidence.
        if (processes.Length is < 2 or > 32
            || processes.Any(p => Number(p, "currentdb") != dbId
                || Number(p, "hostpid") != hostPid || Number(p, "spid") <= 0
                || string.IsNullOrEmpty((string?)p.Attribute("id"))))
            return null;
        var ids = processes.Select(p => p.Attribute("id")!.Value).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) return null;
        var spids = processes.ToDictionary(p => p.Attribute("id")!.Value, p => Number(p, "spid"));
        int Resolve(XElement node) => spids.GetValueOrDefault((string?)node.Attribute("id") ?? "", -1);
        var victims = graph.Element("victim-list")?.Elements("victimProcess").Select(Resolve).ToArray() ?? [];
        if (victims.Length == 0 || victims.Any(x => x < 0)) return null;
        var resources = new List<Rel75Resource>();
        foreach (var resource in graph.Element("resource-list")?.Elements() ?? [])
        {
            if (resource.Attribute("dbid") is not null && Number(resource, "dbid") != dbId)
                return null;
            var owners = resource.Element("owner-list")?.Elements("owner").ToArray() ?? [];
            var waiters = resource.Element("waiter-list")?.Elements("waiter").ToArray() ?? [];
            if (owners.Length == 0 || waiters.Length == 0
                || owners.Concat(waiters).Any(x => Resolve(x) < 0))
                return null;
            string kind = Kinds.FirstOrDefault(x => x == resource.Name.LocalName) ?? "OTHER";
            string name = (string?)resource.Attribute("objectname") ?? "";
            string table = Tables.FirstOrDefault(t => name.EndsWith(".dbo." + t, StringComparison.OrdinalIgnoreCase))
                ?? "OTHER";
            long? hobt = long.TryParse((string?)resource.Attribute("hobtid"), out long h) && h > 0 ? h : null;
            var edges = (from owner in owners from waiter in waiters
                select new Rel75Edge(Resolve(owner), Resolve(waiter), Mode(owner), Mode(waiter))).ToArray();
            resources.Add(new(kind, table, hobt, edges));
        }
        if (resources.Count == 0) return null;
        var edgesAll = resources.SelectMany(x => x.Edges).Where(x => x.OwnerSpid != x.WaiterSpid).ToArray();
        bool Reaches(int start, int goal, HashSet<int> seen) =>
            start == goal || (seen.Add(start) && edgesAll.Where(x => x.WaiterSpid == start)
                .Any(x => Reaches(x.OwnerSpid, goal, seen)));
        bool cycle = edgesAll.Any(e => Reaches(e.OwnerSpid, e.WaiterSpid, []));
        return new(hostPid, dbId, spids.Values.Distinct().Order().ToArray(), victims.Distinct().Order().ToArray(),
            resources.ToArray(), cycle);
    }
}

internal sealed class Rel75DeadlockCapture : IAsyncDisposable
{
    private readonly SqlConnection db;
    private readonly string name;
    private bool created;
    internal int DatabaseId { get; private set; }

    internal Rel75DeadlockCapture(SqlConnection db, string nonce)
    {
        this.db = db;
        if (nonce.Length != 32 || nonce.Any(c => !(c is >= 'a' and <= 'f' or >= '0' and <= '9')))
            throw new InvalidOperationException("REL75_INVALID_XE_NONCE");
        name = "FmcgRel75_" + nonce;
    }

    private static CommandDefinition Cmd(string sql, object? args = null) =>
        new(sql, args, commandTimeout: 5);

    internal async Task StartAsync(string nonce, string tenant)
    {
        await db.OpenAsync();
        if (tenant is not ("tenant-a" or "tenant-b")
            || !db.DataSource.StartsWith(@"(localdb)\", StringComparison.OrdinalIgnoreCase)
            || db.Database != $"Fmcg_App00_{nonce}_{(tenant == "tenant-a" ? "A" : "B")}"
            || await db.ExecuteScalarAsync<string>(Cmd("SELECT TOP(1) Nonce FROM dbo.App00ScratchMarker")) != nonce
            || await db.ExecuteScalarAsync<string>(Cmd("SELECT TenantId FROM dbo.AppTenantBinding WHERE Slot=1")) != tenant)
            throw new InvalidOperationException("REL75_INVALID_XE_TARGET");
        DatabaseId = await db.ExecuteScalarAsync<int>(Cmd("SELECT DB_ID()"));
        if (await db.ExecuteScalarAsync<int>(Cmd("SELECT COUNT(*) FROM sys.server_event_sessions WHERE name=@Name", new { Name = name })) != 0)
            throw new InvalidOperationException("REL75_XE_NAME_EXISTS");

        // Unique temporary in-memory session, only on this disposable hosted
        // LocalDB instance; no file target or alteration to a pre-existing XE session.
        await db.ExecuteAsync(Cmd($"CREATE EVENT SESSION [{name}] ON SERVER ADD EVENT sqlserver.xml_deadlock_report ADD TARGET package0.ring_buffer WITH (MAX_DISPATCH_LATENCY=1 SECONDS, STARTUP_STATE=OFF)"));
        created = true;
        await db.ExecuteAsync(Cmd($"ALTER EVENT SESSION [{name}] ON SERVER STATE=START"));
    }

    private sealed class XeBufferRow
    {
        public string? Xml { get; set; }
        public int DroppedEvents { get; set; }
        public int DroppedBuffers { get; set; }
        public int LargestDroppedEventSize { get; set; }
    }

    internal async Task<Rel75XeObservation> CaptureAsync(int[] hostPids)
    {
        // Give asynchronous XE dispatch its own bounded observation window;
        // this is AFTER the original outcome/deadline, never an extension of it.
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        var row = await db.QuerySingleOrDefaultAsync<XeBufferRow>(Cmd("""
            SELECT CAST(t.target_data AS nvarchar(max)) AS Xml,
                s.dropped_event_count AS DroppedEvents,
                s.dropped_buffer_count AS DroppedBuffers,
                s.largest_event_dropped_size AS LargestDroppedEventSize
            FROM sys.dm_xe_session_targets t JOIN sys.dm_xe_sessions s
              ON t.event_session_address=s.address
            WHERE s.name=@Name AND t.target_name=N'ring_buffer'
            """, new { Name = name }));
        if (row?.Xml is not { } raw) throw new InvalidOperationException("REL75_XE_TARGET_UNAVAILABLE");
        return Rel75DeadlockWitness.ProjectBuffer(raw, DatabaseId, hostPids,
            row.DroppedEvents, row.DroppedBuffers, row.LargestDroppedEventSize);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (created) await db.ExecuteAsync(Cmd($"DROP EVENT SESSION [{name}] ON SERVER"));
        }
        finally { await db.DisposeAsync(); }
    }
}
