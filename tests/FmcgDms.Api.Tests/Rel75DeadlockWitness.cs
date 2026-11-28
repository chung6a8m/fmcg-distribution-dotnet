using System.Xml.Linq;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Tests;

internal sealed record Rel75Edge(int OwnerSpid, int WaiterSpid, string OwnerMode, string WaiterMode);
internal sealed record Rel75Resource(string Kind, string Table, long? HobtId, Rel75Edge[] Edges);
internal sealed record Rel75Graph(int[] Spids, int[] VictimSpids, Rel75Resource[] Resources, bool Cycle);

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
        var ids = processes.Select(p => (string)p.Attribute("id")!).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) return null;
        var spids = processes.ToDictionary(p => (string)p.Attribute("id")!, p => Number(p, "spid"));
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
        return new(spids.Values.Distinct().Order().ToArray(), victims.Distinct().Order().ToArray(),
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

    internal async Task<Rel75Graph[]> CaptureAsync(int hostPid)
    {
        // Give asynchronous XE dispatch its own bounded observation window;
        // this is AFTER the original outcome/deadline, never an extension of it.
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        string? raw = await db.ExecuteScalarAsync<string?>(Cmd("""
            SELECT CAST(t.target_data AS nvarchar(max))
            FROM sys.dm_xe_session_targets t JOIN sys.dm_xe_sessions s
              ON t.event_session_address=s.address
            WHERE s.name=@Name AND t.target_name=N'ring_buffer'
            """, new { Name = name }));
        if (raw is null) throw new InvalidOperationException("REL75_XE_TARGET_UNAVAILABLE");
        var root = XDocument.Parse(raw).Root;
        if (root is null || (string?)root.Attribute("truncated") == "1")
            throw new InvalidOperationException("REL75_XE_TARGET_TRUNCATED");
        return Rel75DeadlockWitness.Project(raw, DatabaseId, hostPid);
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
