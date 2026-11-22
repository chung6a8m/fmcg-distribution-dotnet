using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal sealed partial class App02d4Dev(string root, DemoRun run, string source, string apiSha,
    App02d1Dev d1, Func<string,string,string,string> sign)
{
    private const string Prefix = "/api/synthetic/app02d4";
    private const string Contract = "D30D3E50110C2CC2FF051B1D06555E48702D532DF8F766C4D11F6AA30BBDEAB2";
    private const string Matrix = "502E7FDC58CA41FB8C94AD72E5B41DD21FAAEE3B815E7AD51DCB8A9BDCEC165E";
    private const string Fixture = "BF3DAFBB0C65397155FD625916FE30B9AEEA22E18F279F61E70131367765B987";
    private const string Oracle = "6D50CC1CED8A14F8FE4595BE04EA719580632E645B8E383ED63865EBCB226752";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] Roles = ["invoice", "accountant", "reconciler", "maker", "checker"];
    private readonly string evidence = Path.Combine(root, "TestResults", "app02d4", run.Id);
    private readonly List<CaseResult> results = [];
    private readonly List<object> calls = [];
    private readonly Dictionary<string, Scene> scenes = [];
    private HttpClient client = null!;
    private string origin = "";
    private CancellationToken ct;
    private Func<bool, Task> restart = null!;
    private JsonElement fixture;
    private JsonElement oracle;
    private DateTimeOffset At(string key) => fixture.GetProperty(key).GetDateTimeOffset();
    private string Checkpoints => Path.Combine(evidence, "checkpoints");

    private sealed record Scene(string Slot, Guid Trip, Guid Order, Guid A10, Guid A15, Guid B20, string SourceFingerprint)
    {
        internal Guid[] Deliveries => [A10, A15, B20];
    }

    private sealed record CaseResult(string CaseId, string Tenant, string Status, object[] Checks, string? Error);
    private sealed record Wire(int Http, JsonElement Body)
    {
        internal string? Status => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("status", out var x)
            ? x.GetString() : null;
        internal Guid Id(string property = "id") => Body.GetProperty(property).GetGuid();
    }
    private List<object> checks = [];
    private static string Slot(string tenant) => tenant == "tenant-a" ? "A" : "B";
    private static string Peer(string tenant) => tenant == "tenant-a" ? "tenant-b" : "tenant-a";
    private static string Actor(string role) => "d4-" + role;
    private static string Key() => Guid.NewGuid().ToString("N");

    internal async Task Execute(HttpClient http, string apiOrigin, ProcessStartInfo browserStart,
        Func<ProcessStartInfo, Process> start, Func<bool, Task> restartApi, CancellationToken cancellation)
    {
        client = http;
        origin = apiOrigin;
        ct = cancellation;
        restart = restartApi;
        Directory.CreateDirectory(Checkpoints);
        Guard("docs/roadmap/app-02d4-dev-contract.md", Contract);
        Guard("docs/roadmap/app-02d4-dev-acceptance-matrix.md", Matrix);
        fixture = JsonDocument.Parse(Guard("scripts/fixtures/app02d4/fixture-v0.1.json", Fixture)).RootElement.Clone();
        oracle = JsonDocument.Parse(Guard("scripts/fixtures/app02d4/expected-results-v0.1.json", Oracle)).RootElement.Clone();
        foreach (string tenant in DemoRun.Tenants)
            scenes.Add(tenant, await Seed(tenant));
        foreach (string tenant in DemoRun.Tenants)
            await Record(tenant, 1, () => Upgrade(tenant));
        if (results.Any(x => x.Status != "PASS"))
        {
            await SaveResult(null);
            throw new InvalidOperationException("D.4 LocalDB migration/fixture preflight failed.");
        }

        await restart(true);
        foreach (string tenant in DemoRun.Tenants)
        {
            await d1.SetClock(At("operationUtc"));
            await InvoiceCases(tenant);
            await AccountingCases(tenant);
        }
        await Save(Path.Combine(evidence, "before-browser.json"), await SqlReceipt());
        foreach (string tenant in DemoRun.Tenants) await ResetRehearsal(tenant);
        string browserFixture = Path.Combine(evidence, "browser-fixture.json");
        await Save(browserFixture, new { scenes = scenes.Values });
        browserStart.Environment["APP02D4_BROWSER_FIXTURE"] = browserFixture;
        browserStart.Environment["APP02D4_CHECKPOINTS"] = Checkpoints;
        foreach (string tenant in DemoRun.Tenants)
        foreach (string role in Roles)
            browserStart.Environment[$"APP02D4_{role.ToUpperInvariant()}_TOKEN_{Slot(tenant)}"] = sign(tenant, role, Actor(role));

        using var browser = start(browserStart);
        try
        {
            await Serve(browser);
        }
        finally
        {
            if (!browser.HasExited) browser.Kill(entireProcessTree: true);
            await browser.WaitForExitAsync(CancellationToken.None);
        }
        foreach (string tenant in DemoRun.Tenants)
        {
            await Record(tenant, 17, () => Isolation(tenant));
            await Record(tenant, 18, () => VerifyBrowser(tenant, browser.ExitCode));
        }
        await Save(Path.Combine(evidence, "browser-sql-receipt.json"), await SqlReceipt());
        await SaveResult(browser.ExitCode);
        if (results.Count != 36 || results.Any(x => x.Status != "PASS"))
            throw new InvalidOperationException("D.4 matrix did not reach 36 PASS. See " + evidence);
        Console.WriteLine("D.4 36/36 with two tenant SQL receipts: " + evidence);
    }

    private string Guard(string path, string hash)
    {
        string text = File.ReadAllText(Path.Combine(root, path)).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))) != hash)
            throw new InvalidDataException("Frozen D.4 input changed: " + path);
        return text;
    }

    private async Task<Scene> Seed(string tenant)
    {
        DateTimeOffset setup = At("setupUtc");
        await d1.SetClock(setup);
        var scene = await d1.SeedD2(tenant, "D4-CANON", stock: new(FirstB: 0, SecondB: 20), fixtureTime: setup);
        await using (var db = await Owner(tenant))
        {
            await db.ExecuteAsync("""
                INSERT dbo.D3CashierTrip(TenantId,TripId,ActorId) VALUES(@Tenant,@Trip,'d4-fixture-cashier');
                INSERT dbo.D3PriceProfile(TenantId,TripId,SkuId,PolicyVersion,UnitPrice,Decision,
                    EffectiveFromUtc,EffectiveUntilUtc,ApprovedAtUtc,Maker,Checker,EvidenceRef)
                VALUES(@Tenant,@Trip,@Sku,1,15,'APPROVED','2026-11-23T03:00:00','2026-11-23T05:00:00',
                    '2026-11-23T02:55:00','d4-price-maker','d4-price-checker','D4-v0.1');
                """, scene);
        }
        foreach (var delivery in fixture.GetProperty("deliveries").EnumerateArray())
        {
            await d1.SetClock(delivery.GetProperty("occurredUtc").GetDateTimeOffset());
            decimal qty = delivery.GetProperty("quantity").GetDecimal();
            Wire response;
            if (delivery.TryGetProperty("cash", out var cash))
            {
                decimal amount = cash.GetDecimal();
                string witness = await d1.SeedD2Cash(scene, amount, verifiedAt: setup.UtcDateTime);
                string actor = $"d1-{tenant[^1]}-sales";
                response = await Request(tenant, "sales", "/api/synthetic/app02d2/sales",
                    new D2Request(actor, Key(), Key(), scene.Trip, scene.Customer, scene.ShipTo, scene.Selection,
                        scene.Outlet, scene.B10, scene.Sku, qty, amount, witness, null), sourceApi: true);
            }
            else
            {
                response = await Request(tenant, "sales", "/api/synthetic/app02c4/deliver",
                    new C4DeliveryInput(Guid.NewGuid(), scene.Trip, scene.ALine, 1, qty), sourceApi: true);
            }
            if (response.Http != 200 || response.Status != "APPLIED")
                throw new InvalidDataException($"D.4 source {tenant}/{qty}: {response.Http} {response.Body}");
        }
        await using var reader = await App02c6Dev.ReadOnly(run, tenant);
        Guid[] ids = (await reader.QueryAsync<Guid>(
            "SELECT Id FROM dbo.SaleDelivery WHERE TripId=@Trip ORDER BY BaseQuantity", scene)).ToArray();
        if (ids.Length != 3) throw new InvalidDataException("D.4 requires three committed deliveries per tenant.");
        await SeedInvoiceNegativeCases(tenant);
        return new(Slot(tenant), scene.Trip, scene.Order, ids[0], ids[1], ids[2], await SourceHash(tenant));
    }

    private async Task<Wire> Request(string tenant, string role, string path, object? payload = null,
        bool sourceApi = false, string? peer = null, string? identity = null, bool absolute = false)
    {
        string route = (sourceApi || absolute) ? path : Prefix + "/" + path;
        using var request = new HttpRequestMessage(payload is null ? HttpMethod.Get : HttpMethod.Post, origin + route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sourceApi ? d1.Credential(tenant, role)
            : sign(tenant, role, identity ?? Actor(role)));
        if (peer is not null) request.Headers.Add("X-Tenant-Id", peer);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        using var response = await client.SendAsync(request, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        JsonElement document = body.StartsWith('{') || body.StartsWith('[')
            ? JsonSerializer.Deserialize<JsonElement>(body) : default;
        var result = new Wire((int)response.StatusCode, document);
        lock (calls) calls.Add(new { tenant, role, route, method = request.Method.Method, result.Http,
            response = document.ValueKind == JsonValueKind.Undefined ? null : (JsonElement?)document });
        return result;
    }

    private async Task Record(string tenant, int id, Func<Task> verify)
    {
        string caseId = $"D4-{id:D2}";
        checks = [];
        try
        {
            await verify();
            results.Add(new(caseId, tenant, "PASS", checks.ToArray(), null));
            Console.WriteLine($"{caseId} {tenant} PASS");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            results.Add(new(caseId, tenant, "FAIL", checks.ToArray(), e.Message));
            Console.WriteLine($"{caseId} {tenant} FAIL {e.Message}");
        }
    }

    private void Equal<T>(string field, T actual, T expected)
    {
        bool same = EqualityComparer<T>.Default.Equals(actual, expected);
        checks.Add(new { field, expected, actual, same });
        if (!same) throw new InvalidDataException($"{field}: observed {actual}, expected {expected}.");
    }

    private void Amount(string name, decimal[] values, string key) =>
        Equal(name, string.Join("/", values.Select(DecimalText)),
            string.Join("/", oracle.GetProperty(key).EnumerateArray().Select(v => DecimalText(v.GetDecimal()))));

    private static string DecimalText(decimal value) => value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
    private void Success(Wire result, string operation)
    {
        if (result.Http is < 200 or > 299)
            throw new InvalidDataException($"{operation}: HTTP {result.Http}, {result.Status} {result.Body}");
    }

    private Task SaveResult(int? browserExit) => Save(Path.Combine(evidence, "sql-matrix.json"),
        new { runId = run.Id, source, apiSha, contractSha = Contract, matrixSha = Matrix, fixtureSha = Fixture,
            oracleSha = Oracle, browserExit, cases = results.OrderBy(x => x.Tenant).ThenBy(x => x.CaseId),
            http = calls });

    private static async Task Save(string file, object value)
    {
        await File.WriteAllTextAsync(file + ".pending", JsonSerializer.Serialize(value, Json));
        File.Move(file + ".pending", file, overwrite: true);
    }
}
