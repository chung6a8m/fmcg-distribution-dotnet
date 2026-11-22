using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal sealed partial class App02c6Advanced
{
    private const string Tenant = "tenant-a";
    private const string C1 = "/api/synthetic/app02c1";
    private const string C2 = "/api/synthetic/app02c2";
    private const string C3 = "/api/synthetic/app02c3";
    private const string C4 = "/api/synthetic/app02c4";
    private const string C5 = "/api/synthetic/app02c5";
    private static readonly DateTimeOffset Start = At("2026-10-31T16:00:00Z");
    private static readonly DateTimeOffset First = At("2026-10-31T17:05:00Z");
    private static readonly DateTimeOffset Second = At("2026-11-07T17:05:00Z");
    private static readonly C1Address Address = new("VN", "HCM", "Khu DEV", "Dia chi goc C6", "Nguoi Nhan DEV");

    private readonly DemoRun run;
    private readonly HttpClient client;
    private readonly string webUrl;
    private readonly string token;
    private readonly string actor;
    private readonly string checkerToken;
    private readonly string checker;
    private readonly Func<DateTimeOffset, Task> setClock;
    private readonly CancellationToken ct;
    private readonly List<string> http = [];

    internal sealed record Result(string CaseId, string Scope, IReadOnlyList<string> Expected,
        IReadOnlyList<string> Actual, IReadOnlyList<string> Diagnostics, bool Matches);

    private sealed record Scene(Guid Customer, Guid Sku, Guid Warehouse, Guid Vehicle, Guid Trip, string Code, string Actor);
    private sealed record Order(Guid Id, Guid Line, Guid Confirm);
    private sealed record Posted(Guid Id, Guid OpenItem, Guid ShipTo);
    private sealed record Reply(HttpStatusCode Code, JsonElement Body)
    {
        internal Guid Source => Body.GetProperty("sourceId").GetGuid();
        internal string? Reason => Body.TryGetProperty("reasonCode", out var value) ? value.GetString() : null;
        internal bool Replayed => Body.GetProperty("replayed").GetBoolean();
    }

    private App02c6Advanced(DemoRun run, HttpClient client, string webUrl, string token, string actor,
        string checkerToken, string checker, Func<DateTimeOffset, Task> setClock, CancellationToken ct)
    {
        this.run = run;
        this.client = client;
        this.webUrl = webUrl;
        this.token = token;
        this.actor = actor;
        this.checkerToken = checkerToken;
        this.checker = checker;
        this.setClock = setClock;
        this.ct = ct;
    }

    internal static async Task<IReadOnlyList<Result>> Run(DemoRun run, HttpClient client, string webUrl, string tokenA,
        string actor, string checkerTokenA, string checker, Func<DateTimeOffset, Task> setClock, CancellationToken ct)
    {
        var suite = new App02c6Advanced(run, client, webUrl, tokenA, actor, checkerTokenA, checker, setClock, ct);
        var results = new List<Result>();
        foreach (var (id, body) in new (string, Func<Task<Result>>)[]
        {
            ("DEV-02", suite.Amendment),
            ("DEV-04", suite.CompetingQuota),
            ("DEV-07", suite.HistoricalTerms),
            ("DEV-08", suite.ReportCutoffs)
        })
        {
            suite.http.Clear();
            try
            {
                await setClock(Start);
                string peerBefore = await suite.PeerFingerprint();
                Result result = await body();
                string peerAfter = await suite.PeerFingerprint();
                bool peerUnchanged = string.Equals(peerBefore, peerAfter, StringComparison.Ordinal);
                results.Add(result with
                {
                    Expected = [.. result.Expected, "PEER_UNCHANGED|True"],
                    Actual = [.. result.Actual, $"PEER_UNCHANGED|{peerUnchanged}"],
                    Diagnostics = [.. result.Diagnostics, $"PEER_BEFORE|{peerBefore}", $"PEER_AFTER|{peerAfter}"],
                    Matches = result.Matches && peerUnchanged
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new Result(id, Tenant, ["All required probes complete"], [],
                    [.. suite.http, ex.Message], false));
            }
        }
        return results;
    }

    private async Task<Scene> Seed(string code, bool approvedPolicy = true)
    {
        var scene = new Scene(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), code, actor);
        await using var db = await Writable();
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT dbo.Customer(Id,TenantId,Code,Name) VALUES(@Customer,N'tenant-a',@Code,N'C6 isolated fixture');
            INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom) VALUES(@Sku,N'tenant-a',@Code,N'C6 isolated stock',N'EA');
            INSERT dbo.StockLocation(Id,TenantId,Code,Kind)
            VALUES(@Warehouse,N'tenant-a',@Code+N'-MAIN',N'WAREHOUSE'),(@Vehicle,N'tenant-a',@Code+N'-TRIP',N'VEHICLE');
            INSERT dbo.Trip(Id,TenantId,Code,BusinessDate,VehicleLocationId)
            VALUES(@Trip,N'tenant-a',@Code,'20261031',@Vehicle);
            INSERT dbo.StockPool(TenantId,StockLocationId,SkuId,OnHandBaseQuantity) VALUES(N'tenant-a',@Warehouse,@Sku,100);
            INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,SnapshotCode,BaseQuantity,CostAmount)
            VALUES(N'tenant-a',@Warehouse,@Sku,N'C6-v0.1',100,1000),(N'tenant-a',@Vehicle,@Sku,N'C6-v0.1',0,0);
            INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,OnHandBaseQuantity,OnHandCostAmount)
            VALUES(N'tenant-a',@Warehouse,@Sku,100,1000),(N'tenant-a',@Vehicle,@Sku,0,0);
            INSERT dbo.C3TripWriter VALUES(N'tenant-a',@Trip,@Actor);
            INSERT dbo.C4DeliveryWriter VALUES(N'tenant-a',@Trip,@Actor);
            """, scene, cancellationToken: ct));
        if (approvedPolicy)
            await Policy(scene, 1, 7, Start.AddDays(-1), Second.AddDays(3));
        return scene;
    }

    private async Task Policy(Scene s, int version, int terms, DateTimeOffset from, DateTimeOffset until,
        string decision = "APPROVED")
    {
        await using var db = await Writable();
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT dbo.C2CommercialPolicy(TenantId,Id,CustomerId,SkuId,Version,Decision,EffectiveFromUtc,EffectiveUntilUtc,
                Maker,Checker,ApprovedAtUtc,EvidenceRef,UnitPrice,DiscountPerUnit,TaxRate,TermsDays,CreditCap,SellerTimeZone)
            VALUES(N'tenant-a',NEWID(),@Customer,@Sku,@Version,@decision,@From,@Until,
                N'c6-fixture-maker',N'c6-fixture-checker',@From,N'c6-v0.1',15,0,0,@terms,1000,'Asia/Ho_Chi_Minh');
            """, new { s.Customer, s.Sku, version, terms, decision, From = from.UtcDateTime, Until = until.UtcDateTime },
            cancellationToken: ct));
    }

    private async Task<SqlConnection> Writable()
    {
        await run.Verify(Tenant);
        return new SqlConnection(run.Connection(Tenant));
    }

    private async Task<Guid> Select(Scene s, C1Address? address = null, Guid? registered = null, int? revision = null)
    {
        var reply = await Post(C1 + "/ship-to-selections", new C1SelectionInput(Guid.NewGuid(), s.Customer,
            null, registered, revision, registered.HasValue ? null : address ?? Address));
        return reply.Body.GetProperty("id").GetGuid();
    }

    private async Task<Guid> Draft(Scene s, Guid address, decimal quantity) =>
        (await Post(C2 + "/drafts", new C2DraftInput(Guid.NewGuid(), s.Customer, s.Trip, address, [new(s.Sku, quantity)]))).Source;

    private async Task<Order> Confirm(Guid draft)
    {
        var result = await Post(C2 + "/confirm", new C2ConfirmInput(Guid.NewGuid(), draft));
        var rows = (await Send(HttpMethod.Get, C2 + "/orders")).Body;
        Guid line = rows.EnumerateArray().Single(row => row.GetProperty("id").GetGuid() == draft)
            .GetProperty("lines")[0].GetProperty("lineId").GetGuid();
        return new Order(draft, line, result.Source);
    }

    private Task<Reply> Reserve(Scene s, Order order, decimal qty) =>
        Post(C2 + "/reserve", new C2ReserveInput(Guid.NewGuid(), order.Id, order.Line, s.Warehouse, qty, order.Confirm));

    private async Task<Guid> Load(Scene s, Order order, decimal qty)
    {
        await using var read = await App02c6Dev.ReadOnly(run, Tenant);
        Guid right = await read.QuerySingleAsync<Guid>("SELECT StockRightId FROM dbo.AReservation WHERE AOrderLineId=@Line",
            new { order.Line });
        await Post(C3 + "/load", new C3LoadInput(Guid.NewGuid(), s.Trip, [new(right, qty, qty)]));
        return await read.QuerySingleAsync<Guid>("SELECT Id FROM dbo.TripManifestLine WHERE StockRightId=@right", new { right });
    }

    private async Task<Guid> LoadOrder(Scene s, Guid address, decimal qty)
    {
        Order order = await Confirm(await Draft(s, address, qty));
        await Reserve(s, order, qty);
        return await Load(s, order, qty);
    }

    private Task<Reply> Deliver(Scene scene, Guid manifest, decimal qty, int version = 1,
        HttpStatusCode expected = HttpStatusCode.OK) =>
        Post(C4 + "/deliver", new C4DeliveryInput(Guid.NewGuid(), scene.Trip, manifest, version, qty), expected);

    private Task<Reply> Post(string path, object body, HttpStatusCode expected = HttpStatusCode.OK,
        string? auth = null) => Send(HttpMethod.Post, path, body, expected, auth);

    private async Task<Reply> Send(HttpMethod method, string path, object? body = null,
        HttpStatusCode expected = HttpStatusCode.OK, string? auth = null)
    {
        using var request = new HttpRequestMessage(method, webUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth ?? token);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, ct);
        string payload = await response.Content.ReadAsStringAsync(ct);
        lock (http) http.Add($"{method} {path} {(int)response.StatusCode}");
        if (response.StatusCode != expected)
        {
            string detail = payload.StartsWith('{') ? payload : "no JSON response";
            throw new InvalidOperationException($"{method} {path}: expected {(int)expected}, got {(int)response.StatusCode} ({detail}).");
        }
        return new Reply(response.StatusCode, payload.StartsWith('{') || payload.StartsWith('[')
            ? JsonSerializer.Deserialize<JsonElement>(payload) : default);
    }

    private async Task<string> PeerFingerprint()
    {
        await using var reader = await App02c6Dev.ReadOnly(run, "tenant-b");
        return await reader.QuerySingleAsync<string>("""
            SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONCAT(
                (SELECT * FROM dbo.Customer ORDER BY Id FOR JSON PATH),
                (SELECT * FROM dbo.C1ShipToSelection ORDER BY Id FOR JSON PATH),
                (SELECT * FROM dbo.AOrder ORDER BY Id FOR JSON PATH),
                (SELECT * FROM dbo.StockRight ORDER BY Id FOR JSON PATH),
                (SELECT * FROM dbo.TripManifest ORDER BY Id FOR JSON PATH),
                (SELECT * FROM dbo.SaleDelivery ORDER BY Id FOR JSON PATH),
                (SELECT * FROM dbo.ReceivableOpenItem ORDER BY Id FOR JSON PATH),
                (SELECT * FROM dbo.ReceiptAllocation ORDER BY Id FOR JSON PATH),
                (SELECT * FROM dbo.LedgerEffect ORDER BY Id FOR JSON PATH))),2)
            """);
    }

    private Result Compare(string id, string[] expected, List<string> actual) =>
        new(id, Tenant, expected, actual, http.ToArray(), expected.SequenceEqual(actual, StringComparer.Ordinal));

    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private static void Require(bool value, string detail)
    {
        if (!value) throw new InvalidOperationException(detail);
    }
}
