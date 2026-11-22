using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal sealed record D2Request(string ProducerId, string CommandId, string SourceNumber, Guid TripId,
    Guid CustomerId, Guid ShipToSnapshotId, Guid SelectionId, Guid? OutletId, Guid TripManifestLineId,
    Guid SkuId, decimal BaseQuantity, decimal ReceivedCashAmount, string VerificationEvidenceRef,
    string? PendingBankEvidenceRef = null, Guid? CustomerCreditReservationId = null);

internal sealed partial class App02d2Dev(string root, DemoRun run, string source, string apiSha, App02d1Dev d1)
{
    private const string Sales = "/api/synthetic/app02d2/sales";
    private const string ContractSha = "71E6D77200289D6FE15425586AB6B8B90630EA2CAC62B8C0C331E4FE5CC05699";
    private const string MatrixSha = "210757E3799F300737F6D6BE63196A3934C1C89B0F3E8388D7699AA717344165";
    private const string FixtureSha = "17812B16601E1DCBDB6477C6EE4C8F0FE5E8FB62A10F4B0A136076713ECBBFCD";
    private const string ExpectedSha = "06630EC3464F1058BAC1509E2BE46E230CD52B49F17D2B9E59598C159A92285D";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly AsyncLocal<List<object>?> Current = new();
    private readonly string evidence = Path.Combine(root, "TestResults", "app02d2", run.Id);
    private string Checkpoints => Path.Combine(evidence, "checkpoints");
    private readonly Dictionary<(string Tenant, string CaseId), BrowserScene> browserScenes = [];
    private readonly Dictionary<string, Upgrade> upgrades = [];
    private readonly List<CaseResult> results = [];
    private readonly List<object> observations = [];
    private HttpClient client = null!;
    private CancellationToken ct;
    private Func<string, Task> restart = null!;
    private string url = "";
    private Dictionary<string, Expected> expected = null!;

    private sealed record Expected(decimal Qty, decimal Ar, decimal Cash, decimal CashApplied, decimal Credit);
    private sealed record BrowserScene(D2Scene Scene, D2Request Request, D2Snapshot Opening);
    private sealed record CaseResult(string CaseId, string Scope, string Status, object[] Comparisons, string? Error);
    private sealed record Upgrade(D2Scene Scene, long BeforeVersion, long AfterVersion, string Before, string After);
    private sealed record BrowserResult(string ProducerId, string CommandId, string SourceNumber, int HttpStatus,
        string Status, Guid SourceId, int OfflinePostCount, bool QueuedAfterOffline,
        int RevokedLookupStatus, int RevokedReplayStatus, int RevokedSubmitStatus);
    private sealed record Reply(int Http, string? Status, string? Reason, Guid? Source, bool Replayed)
    {
        internal bool Applied => Http == 200 && Status == "APPLIED" && Source.HasValue;
        internal bool Denied => !Applied && (Status is "REJECTED" or "CONFLICT" || Http is >= 400 and < 500);
    }

    internal void VerifyDocuments()
    {
        Guard("docs/roadmap/app-02d2-dev-contract.md", ContractSha);
        Guard("docs/roadmap/app-02d2-dev-acceptance-matrix.md", MatrixSha);
        Guard("scripts/fixtures/app02d2/fixture-v0.1.json", FixtureSha);
        string expectations = Guard("scripts/fixtures/app02d2/expected-v0.1.json", ExpectedSha);
        expected = JsonSerializer.Deserialize<Dictionary<string, Expected>>(expectations, Json)!;
    }

    private string Guard(string file, string sha)
    {
        string text = File.ReadAllText(Path.Combine(root, file)).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))) != sha)
            throw new InvalidDataException($"Approved D.2 input changed: {file}.");
        return text;
    }

    internal async Task Initialize(HttpClient http, string webUrl, CancellationToken cancellationToken,
        Func<string, Task> restartApi)
    {
        client = http;
        url = webUrl;
        ct = cancellationToken;
        restart = restartApi;
        Directory.CreateDirectory(Checkpoints);
        foreach (var tenant in DemoRun.Tenants)
        {
            var scene = await d1.SeedD2(tenant, "D2-UPGRADE", bindD2: false);
            long beforeVersion = await Version(tenant);
            string before = await d1.D2History(tenant);
            ScratchMigrator.Migrate(run.Connection(tenant), tenant, run.Id, LogLevel.Warning, true);
            await run.Verify(tenant);
            long afterVersion = await Version(tenant);
            string after = await d1.D2History(tenant);
            upgrades.Add(tenant, new(scene, beforeVersion, afterVersion, before, after));
            await d1.BindD2(scene);
        }
        await restart("on");
        foreach (var tenant in DemoRun.Tenants)
        {
            await AddBrowser(tenant, "D2-02", 60);
            await AddBrowser(tenant, "D2-03", 100, withOutlet: true);
            await AddBrowser(tenant, "D2-04", 0, bank: true);
            await AddBrowser(tenant, "D2-05", 60, reserve: true);
            await AddBrowser(tenant, "D2-06", 120);
            await AddBrowser(tenant, "D2-17", 60);
        }

        var scenarios = browserScenes.Select(pair => new
        {
            slot = Slot(pair.Key.Tenant), caseId = pair.Key.CaseId, request = pair.Value.Request
        });
        await Save(Path.Combine(evidence, "browser-fixture.json"), new { scenes = scenarios });
        await Save(Path.Combine(evidence, "fixture.json"), new
        {
            version = "D2-v0.1", clock = App02d1Dev.StartTime,
            approvedInputs = new { contractSha = ContractSha, matrixSha = MatrixSha, fixtureSha = FixtureSha, expectedSha = ExpectedSha },
            browserScenes = browserScenes.Select(pair => new { pair.Key.CaseId, pair.Value.Scene, pair.Value.Opening }),
            upgrades
        });
    }

    private async Task AddBrowser(string tenant, string id, decimal cash, bool withOutlet = false,
        bool bank = false, bool reserve = false)
    {
        var scene = await d1.SeedD2(tenant, id, withOutlet);
        Guid? reservation = null;
        if (reserve)
        {
            var origin = Request(scene, await d1.SeedD2Cash(scene, 120), 120) with { TripManifestLineId = scene.B10 };
            Guid sourceId = Applied(await Send(tenant, HttpMethod.Post, Sales, origin), id + " credit origin");
            reservation = await d1.ReserveD2CustomerCredit(scene, sourceId);
        }
        var request = Request(scene, cash == 0 ? "" : await d1.SeedD2Cash(scene, cash), cash) with
        {
            PendingBankEvidenceRef = bank ? await d1.SeedD2Bank(scene) : null,
            CustomerCreditReservationId = reservation
        };
        browserScenes.Add((tenant, id), new(scene, request, await D2Sql.Read(run, scene)));
    }

    internal void ConfigureBrowser(ProcessStartInfo browser)
    {
        browser.Environment["APP02D2_CHECKPOINTS"] = Checkpoints;
        browser.Environment["APP02D2_BROWSER_FIXTURE"] = Path.Combine(evidence, "browser-fixture.json");
        foreach (string tenant in DemoRun.Tenants)
            browser.Environment["APP02D2_SALES_TOKEN_" + Slot(tenant)] = d1.Credential(tenant, "sales");
    }

    internal async Task Serve(Process browser)
    {
        while (!browser.HasExited)
        {
            foreach (string tenant in DemoRun.Tenants)
            foreach (string phase in new[] { "revoke", "restore" })
            {
                string file = Path.Combine(Checkpoints, $"{Slot(tenant)}-{phase}");
                if (!File.Exists(file + ".request.json") || File.Exists(file + ".response.json")) continue;
                await Authority(tenant, phase == "restore");
                await Save(file + ".response.json", new { slot = Slot(tenant), phase, ok = true });
            }
            await Task.Delay(50, ct);
        }
    }

    private async Task Authority(string tenant, bool active)
    {
        await using var db = await Owner(tenant);
        Verify(await db.ExecuteAsync("""
            UPDATE dbo.C4DeliveryWriter SET ActorId=@actor WHERE TenantId=@tenant AND TripId=@trip
            """, new { tenant, trip = browserScenes[(tenant, "D2-17")].Scene.Trip, actor = active ? Actor(tenant) : $"d1-{tenant[^1]}-other-sales" }), 1, "Sales fixture authority change");
    }

    private async Task<SqlConnection> Owner(string tenant)
    {
        await run.Verify(tenant);
        return new SqlConnection(run.Connection(tenant));
    }

    private async Task<long> Version(string tenant)
    {
        await using var db = await App02c6Dev.ReadOnly(run, tenant);
        return await db.QuerySingleAsync<long>("SELECT MAX(Version) FROM dbo.VersionInfo");
    }

    private static string Slot(string tenant) => tenant == "tenant-a" ? "A" : "B";
    private static string Actor(string tenant) => $"d1-{tenant[^1]}-sales";
    private static string Key() => Guid.NewGuid().ToString("N");
    private static D2Request Request(D2Scene s, string witness = "", decimal cash = 0, decimal qty = 10) =>
        new(Actor(s.Tenant), Key(), Key(), s.Trip, s.Customer, s.ShipTo, s.Selection, s.Outlet, s.B20, s.Sku, qty, cash, witness);
    private static string Lookup(D2Request r) => $"{Sales}/{r.ProducerId}/{r.CommandId}?tripId={r.TripId}";

    private async Task<Reply> Send(string tenant, HttpMethod method, string path, object? data = null, string role = "sales")
    {
        using var request = new HttpRequestMessage(method, url + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", d1.D2Credential(tenant, role));
        if (data is not null) request.Content = JsonContent.Create(data, options: Json);
        using var response = await client.SendAsync(request, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        JsonElement fields = body.TrimStart().StartsWith('{') ? JsonSerializer.Deserialize<JsonElement>(body) : default;
        string? Property(string name) => fields.ValueKind == JsonValueKind.Object &&
            fields.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
        var reply = new Reply((int)response.StatusCode, Property("status"), Property("reasonCode") ?? Property("code"),
            Guid.TryParse(Property("sourceId"), out Guid parsed) ? parsed : null,
            fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("replayed", out var replay) && replay.ValueKind == JsonValueKind.True);
        lock (observations) observations.Add(new { tenant, role, method = method.Method, path, reply });
        return reply;
    }

    private static Guid Applied(Reply reply, string label)
    {
        Verify(reply.Applied, true, label + $" HTTP={reply.Http} status={reply.Status} reason={reply.Reason}");
        return reply.Source!.Value;
    }

    private static void Verify<T>(T actual, T expected, string label)
    {
        bool equal = EqualityComparer<T>.Default.Equals(actual, expected);
        Current.Value?.Add(new { label, expected, actual, diff = equal ? null : "MISMATCH" });
        if (!equal) throw new InvalidOperationException($"{label}: expected {expected}, got {actual}.");
    }

    private static async Task Save(string path, object value)
    {
        await File.WriteAllTextAsync(path + ".pending", JsonSerializer.Serialize(value, Json));
        File.Move(path + ".pending", path, overwrite: true);
    }

    private async Task Record(string id, string scope, Func<Task> test)
    {
        var comparisons = new List<object>();
        Current.Value = comparisons;
        try
        {
            await test();
            results.Add(new(id, scope, "PASS", comparisons.ToArray(), null));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"{id}/{scope}: {ex.Message}");
            results.Add(new(id, scope, "FAIL", comparisons.ToArray(), ex.Message));
        }
        finally { Current.Value = null; }
    }
}

internal sealed partial class App02d1Dev
{
    internal Task<string> D2History(string tenant) => Fingerprint(tenant);
    internal void VerifyD1History(string tenant) => VerifyUpgrade(tenant);
}
