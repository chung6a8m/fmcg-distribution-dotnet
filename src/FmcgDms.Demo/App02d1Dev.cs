using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal sealed partial class App02d1Dev(string root, DemoRun run, string source, string apiSha256,
    Func<string, string, string, string> sign)
{
    private const string D1 = "/api/synthetic/app02d1";
    private const string C1 = "/api/synthetic/app02c1";
    private const string C2 = "/api/synthetic/app02c2";
    private const string C3 = "/api/synthetic/app02c3";
    private const string Unit = "SYNTHETIC_UNITLESS";
    private static readonly DateTimeOffset From = new(2026, 11, 22, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Until = From.AddHours(2);
    internal static DateTimeOffset StartTime { get; } = From.AddHours(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string evidencePath = Path.Combine(root, "TestResults", "app02d1", run.Id);
    internal string ClockPath => Path.Combine(evidencePath, "clock.utc");
    private string Checkpoints => Path.Combine(evidencePath, "checkpoints");
    private readonly Dictionary<string, Scene> canonical = [];
    private readonly List<Result> results = [];
    private readonly List<HttpObservation> http = [];
    private HttpClient client = null!;
    // Set only for the scoped D2-16 investigation, never the regular D.1 matrix.
    internal D2PeerWitness? D2PeerDiagnostic { get; set; }
    private string url = "";
    private CancellationToken ct;

    private sealed record Scene(string Tenant, Guid Trip, Guid Customer, Guid ShipTo, Guid Sku, Guid Main, Guid Truck);
    private sealed record Result(string CaseId, string Scope, string Status, object[] Comparisons, string? Error);
    private sealed record HttpObservation(string Tenant, string Role, string Method, string Path, int Status, string? Outcome, Guid? SourceId);
    private sealed record Reply(HttpStatusCode Status, JsonElement Body)
    {
        internal Guid Source => Body.GetProperty("sourceId").GetGuid();
        internal string? Reason => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("reasonCode", out var value)
            ? value.GetString() : null;
        internal bool Replayed => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("replayed", out var value) && value.GetBoolean();
    }
    private static readonly AsyncLocal<List<object>?> Comparisons = new();

    private static string Actor(string tenant, string role) => $"d1-{tenant[^1]}-{role}";
    internal string Credential(string tenant, string role) => sign(tenant, role == "other-warehouse" ? "warehouse" : role, Actor(tenant, role));
    private static string Slot(string tenant) => tenant == "tenant-a" ? "A" : "B";
    private static string Tenant(string slot) => slot == "A" ? "tenant-a" : "tenant-b";
    private static string Key() => Guid.NewGuid().ToString("N");
    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    private const string MatrixSha = "E776C40ADEA1262F2C1B0052B32549747195C128EF2A9560EACC18E521EB9BCF";
    private const string ContractSha = "0AAAAE73A161C0C3ECF17F34D7D747F84AF3031CA6430CBB57298832EC73F30B";

    internal void VerifyDocuments()
    {
        Verify("app-02d0-dev-contract.md", ContractSha);
        Verify("app-02d1-dev-acceptance-matrix.md", MatrixSha);

        void Verify(string name, string expected)
        {
            string content = File.ReadAllText(Path.Combine(root, "docs", "roadmap", name)).Replace("\r\n", "\n", StringComparison.Ordinal);
            string observed = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));
            if (observed != expected) throw new InvalidDataException($"Approved D.1 document changed: {name}.");
        }
    }

    internal async Task SetClock(DateTimeOffset value)
    {
        Directory.CreateDirectory(evidencePath);
        string pending = ClockPath + ".pending";
        await File.WriteAllTextAsync(pending, value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        File.Move(pending, ClockPath, overwrite: true);
    }

    internal async Task Prepare(string tenant)
    {
        await using var db = await Write(tenant);
        await db.OpenAsync(ct);
        await App02c2DevFixture.Seed(db, tenant, Actor(tenant, "warehouse"), StartTime.UtcDateTime);
        await App02c3DevFixture.Seed(db, tenant, Actor(tenant, "warehouse"), false);
        foreach (string role in new[] { "sales", "finance", "credit_maker", "credit_checker", "other-warehouse" })
            await db.ExecuteAsync("""
                INSERT dbo.App02ActorRoleGrant(TenantId,ActorId,RoleCode,IsActive,ChangedBy)
                    VALUES(@tenant,@actor,@role,1,'d1-fixture')
                """, new { tenant, actor = Actor(tenant, role), role = role == "other-warehouse" ? "warehouse" : role });
        await db.ExecuteAsync("""
            CREATE USER App02C6SqlOracle WITHOUT LOGIN;
            GRANT SELECT ON SCHEMA::dbo TO App02C6SqlOracle;
            DENY INSERT,UPDATE,DELETE ON SCHEMA::dbo TO App02C6SqlOracle;
            """);
        var ids = await db.QuerySingleAsync<Scene>("""
            SELECT @tenant AS Tenant,(SELECT Id FROM dbo.Trip WHERE Code='C2-TRIP') AS Trip,
                (SELECT Id FROM dbo.Customer WHERE Code='C2-FIXTURE') AS Customer,
                CAST('00000000-0000-0000-0000-000000000000' AS uniqueidentifier) AS ShipTo,
                (SELECT Id FROM dbo.Sku WHERE Code='C2-SKU') AS Sku,
                (SELECT Id FROM dbo.StockLocation WHERE Code='C2-MAIN') AS Main,
                (SELECT Id FROM dbo.StockLocation WHERE Code='C2-TRUCK') AS Truck
            """, new { tenant });
        canonical.Add(tenant, await SeedCredit(ids));
    }

    internal async Task Initialize(HttpClient client, string webUrl, CancellationToken cancellationToken)
    {
        this.client = client;
        url = webUrl;
        ct = cancellationToken;
        Directory.CreateDirectory(Checkpoints);
        foreach (var scene in canonical.Values)
        {
            Expect(await SnapshotOf(scene), new Snapshot(100,1000,0,0,0,0,0,0,0,0,0,0), "opening");
        }
        await Save(Path.Combine(evidencePath,"fixture.json"), new
        {
            version = "D1-v0.1", clock = StartTime, effectiveFrom = From, effectiveUntil = Until,
            reviewed = From.AddMinutes(-5), tenants = canonical.Values,
            expected = new[] { "MAIN100/1000", "A40", "B30", "credit100", "B10", "B20", "A40", "MAIN30/TRIP70", "zero-financial" }
        });
    }

    internal void ConfigureBrowser(ProcessStartInfo process)
    {
        process.Environment["APP02D1_CHECKPOINTS"] = Checkpoints;
        foreach (var pair in canonical)
        {
            string slot = Slot(pair.Key);
            process.Environment["APP02D1_TRIP_" + slot] = pair.Value.Trip.ToString();
            process.Environment["APP02D1_CUSTOMER_" + slot] = pair.Value.Customer.ToString();
            process.Environment["APP02D1_SHIPTO_" + slot] = pair.Value.ShipTo.ToString();
            process.Environment["APP02D1_SALES_TOKEN_" + slot] = Credential(pair.Key,"sales");
            process.Environment["APP02D1_WAREHOUSE_TOKEN_" + slot] = Credential(pair.Key,"warehouse");
            process.Environment["APP02D1_FINANCE_TOKEN_" + slot] = Credential(pair.Key,"finance");
        }
    }

    private sealed record Checkpoint(string Slot, string Phase);
    internal async Task Serve(Process browser)
    {
        while (!browser.HasExited)
        {
            foreach (string path in Directory.GetFiles(Checkpoints,"*.request.json"))
            {
                string output = path[..^".request.json".Length] + ".response.json";
                if (File.Exists(output)) continue;
                var ask = JsonSerializer.Deserialize<Checkpoint>(await File.ReadAllTextAsync(path,ct),Json);
                if (ask is null || ask.Slot is not ("A" or "B") ||
                    !System.Text.RegularExpressions.Regex.IsMatch(ask.Phase, "^[a-z0-9-]+$") ||
                    Path.GetFileName(path) != $"{ask.Slot}-{ask.Phase}.request.json")
                    throw new InvalidDataException("Invalid D.1 browser checkpoint.");
                var scene=canonical[Tenant(ask.Slot)];
                if(ask.Phase is "revoke-warehouse" or "restore-warehouse")
                    await ChangeWarehouseAuthority(scene,ask.Phase=="restore-warehouse");
                await Save(output,await SnapshotOf(scene));
            }
            await Task.Delay(75,ct);
        }
    }

    private async Task ChangeWarehouseAuthority(Scene s,bool active)
    {
        await using var db=await Write(s.Tenant);
        Expect(await db.ExecuteAsync("""
            UPDATE dbo.App02ActorRoleGrant SET IsActive=@active,ChangedBy='d1-fixture',ChangedAtUtc=SYSUTCDATETIME()
            WHERE TenantId=@Tenant AND ActorId=@actor AND RoleCode='warehouse'
            """,new {s.Tenant,actor=Actor(s.Tenant,"warehouse"),active}),1,"Canonical browser Warehouse role");
    }

    private async Task<Scene> Seed(string tenant, string code, decimal cap = 100, string decision = "APPROVED",
        DateTimeOffset? from = null, DateTimeOffset? until = null, string? maker = null, string? checker = null,
        decimal openingQuantity = 100, decimal openingValue = 1000, DateTimeOffset? fixtureTime = null)
    {
        var s = new Scene(tenant,Guid.NewGuid(),Guid.NewGuid(),Guid.Empty,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());
        await using var db = await Write(tenant);
        await db.ExecuteAsync("""
            INSERT dbo.Customer(Id,TenantId,Code,Name) VALUES(@Customer,@Tenant,@code,@code);
            INSERT dbo.Sku(Id,TenantId,Code,Name,BaseUom) VALUES(@Sku,@Tenant,@code,@code,'EA');
            INSERT dbo.StockLocation(Id,TenantId,Code,Kind)
                VALUES(@Main,@Tenant,@code+'-MAIN','WAREHOUSE'),(@Truck,@Tenant,@code+'-TRIP','VEHICLE');
            INSERT dbo.Trip(Id,TenantId,Code,BusinessDate,VehicleLocationId)
                VALUES(@Trip,@Tenant,@code,@businessDate,@Truck);
            INSERT dbo.StockPool(TenantId,StockLocationId,SkuId,OnHandBaseQuantity)
                VALUES(@Tenant,@Main,@Sku,@openingQuantity);
            INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,SnapshotCode,BaseQuantity,CostAmount)
                VALUES(@Tenant,@Main,@Sku,'D1-v0.1',@openingQuantity,@openingValue),(@Tenant,@Truck,@Sku,'D1-v0.1',0,0);
            INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,OnHandBaseQuantity,OnHandCostAmount)
                VALUES(@Tenant,@Main,@Sku,@openingQuantity,@openingValue),(@Tenant,@Truck,@Sku,0,0);
            INSERT dbo.C3TripWriter VALUES(@Tenant,@Trip,@writer);
            INSERT dbo.C2CommercialPolicy(TenantId,Id,CustomerId,SkuId,Version,Decision,EffectiveFromUtc,EffectiveUntilUtc,
                Maker,Checker,ApprovedAtUtc,EvidenceRef,UnitPrice,DiscountPerUnit,TaxRate,TermsDays,CreditCap,SellerTimeZone)
                VALUES(@Tenant,NEWID(),@Customer,@Sku,1,'APPROVED','20261121',@priceUntil,
                    'd1-price-maker','d1-price-checker','20261121','D1-v0.1',15,0,0,7,1000,'Asia/Ho_Chi_Minh');
            """,new { s.Tenant,s.Trip,s.Customer,s.Sku,s.Main,s.Truck,code,openingQuantity,openingValue,writer=Actor(tenant,"warehouse"),
                businessDate=(fixtureTime ?? From).ToOffset(TimeSpan.FromHours(7)).Date,
                priceUntil=fixtureTime?.AddDays(1).UtcDateTime ?? new DateTime(2026,11,23) });
        return await SeedCredit(s,cap,decision,from ?? fixtureTime?.AddHours(-1),until ?? fixtureTime?.AddHours(2),maker,checker);
    }

    private async Task<Scene> SeedCredit(Scene s, decimal cap = 100, string decision = "APPROVED",
        DateTimeOffset? from = null, DateTimeOffset? until = null, string? maker = null, string? checker = null)
    {
        s = s with { ShipTo = Guid.NewGuid() };
        await using var db = await Write(s.Tenant);
        await db.ExecuteAsync("""
            INSERT dbo.ShipToSnapshot(Id,TenantId,CustomerId,DisplayName,Address)
                VALUES(@ShipTo,@Tenant,@Customer,N'D1 Customer',N'01 Duong DEV, HCM, VN');
            INSERT dbo.BCreditPool(TenantId,TripId,CustomerId,PoolCode,CurrencyUnit,ApprovedLimit)
                VALUES(@Tenant,@Trip,@Customer,'B','SYNTHETIC_UNITLESS',@cap);
            """,new { s.ShipTo,s.Tenant,s.Trip,s.Customer,cap });
        if (decision != "MISSING") await Approval(s,cap,decision,from,until,maker,checker);
        return s;
    }

    private async Task Approval(Scene s, decimal cap, string decision, DateTimeOffset? from = null,
        DateTimeOffset? until = null, string? maker = null, string? checker = null)
    {
        await using var db = await Write(s.Tenant);
        await db.ExecuteAsync("""
            INSERT dbo.CreditPolicyApproval(Id,TenantId,TripId,CustomerId,PoolCode,CurrencyUnit,CaseNumber,PolicyVersion,
                ProposedBy,CheckedBy,Decision,Reason,EvidenceRef,ApprovedLimit,EffectiveFromUtc,EffectiveUntilUtc,ReviewedAtUtc)
            VALUES(NEWID(),@Tenant,@Trip,@Customer,'B','SYNTHETIC_UNITLESS',@number,'1',
                @maker,@checker,@decision,'D1 approved matrix fixture','D1-v0.1',@cap,@from,@until,@reviewed)
            """,new { s.Tenant,s.Trip,s.Customer,number=Key(),maker=maker??Actor(s.Tenant,"credit_maker"),
                checker=checker??Actor(s.Tenant,"credit_checker"), decision,cap,
                from=(from??From).UtcDateTime,until=(until??Until).UtcDateTime,reviewed=From.AddMinutes(-5).UtcDateTime });
    }

    private async Task<Guid> ReserveA(Scene s,decimal quantity)
    {
        var selection = await Post(s,"sales",C1+"/ship-to-selections",
            new C1SelectionInput(Guid.NewGuid(),s.Customer,null,null,null,new("VN","HCM","Khu DEV","01 Duong DEV","Nguoi Nhan")));
        var order = (await Post(s,"sales",C2+"/drafts",new C2DraftInput(Guid.NewGuid(),s.Customer,s.Trip,
            selection.Body.GetProperty("id").GetGuid(),[new(s.Sku,quantity)]))).Source;
        var confirm=(await Post(s,"sales",C2+"/confirm",new C2ConfirmInput(Guid.NewGuid(),order))).Source;
        var orders=await Send(s,"sales",HttpMethod.Get,C2+"/orders");
        Guid line=orders.Body.EnumerateArray().Single(x=>x.GetProperty("id").GetGuid()==order)
            .GetProperty("lines")[0].GetProperty("lineId").GetGuid();
        await Post(s,"warehouse",C2+"/reserve",new C2ReserveInput(Guid.NewGuid(),order,line,s.Main,quantity,confirm));
        await using var db=await App02c6Dev.ReadOnly(run,s.Tenant);
        return await db.QuerySingleAsync<Guid>("SELECT StockRightId FROM dbo.AReservation WHERE AOrderLineId=@line",new { line });
    }

    private async Task<SqlConnection> Write(string tenant)
    {
        await run.Verify(tenant);
        return new SqlConnection(run.Connection(tenant));
    }

    private static GrantBQuotaCommand Quota(Scene s,decimal qty) => new("server",Key(),Key(),s.Trip,s.Main,s.Sku,qty);
    private static GrantBCreditCommand Credit(Scene s,decimal amount) => new("server",Key(),Key(),s.Trip,s.Customer,s.ShipTo,"B",Unit,amount);
    private static C3LoadInput Load(Scene s,Guid right,decimal held,decimal qty) => new(Guid.NewGuid(),s.Trip,[new(right,held,qty)]);

    private async Task<Guid> Right(Scene s)
    {
        await using var reader=await App02c6Dev.ReadOnly(run,s.Tenant);
        return await reader.QuerySingleAsync<Guid>("SELECT Id FROM dbo.StockRight WHERE TripId=@Trip AND SkuId=@Sku AND RightKind='B'",s);
    }
    private async Task<Reply> Post(Scene s,string role,string path,object payload,HttpStatusCode expected=HttpStatusCode.OK)
    {
        var reply=await Send(s,role,HttpMethod.Post,path,payload);
        Expect((int)reply.Status,(int)expected,path+" HTTP ("+reply.Reason+")");
        return reply;
    }
    private async Task<Reply> Send(Scene s,string role,HttpMethod method,string path,object? payload=null,
        string? credential = null, bool forged = false)
    {
        using var request=new HttpRequestMessage(method,url+path);
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",credential??Credential(s.Tenant,role));
        if(forged) request.Headers.Add("X-Tenant-Id",s.Tenant=="tenant-a"?"tenant-b":"tenant-a");
        if(payload is not null) request.Content=JsonContent.Create(payload);
        var diagnostic = D2PeerDiagnostic;
        var ticket = diagnostic?.Begin(s.Tenant, request, client.Timeout);
        int? diagnosticStatus = null;
        Exception? diagnosticFailure = null;
        try
        {
            using var response=await client.SendAsync(request,ct);
            diagnosticStatus = (int)response.StatusCode;
            string body=await response.Content.ReadAsStringAsync(ct);
            var parsed=body.TrimStart().StartsWith('{')||body.TrimStart().StartsWith('[') ? JsonSerializer.Deserialize<JsonElement>(body) : default;
            var result=new Reply(response.StatusCode,parsed);
            Guid? sourceId=parsed.ValueKind==JsonValueKind.Object && parsed.TryGetProperty("sourceId",out var value) &&
                value.ValueKind==JsonValueKind.String && value.TryGetGuid(out Guid id) ? id : null;
            lock(http) http.Add(new(s.Tenant,role,method.Method,path,(int)response.StatusCode,result.Reason,sourceId));
            return result;
        }
        catch (Exception ex) { diagnosticFailure = ex; throw; }
        finally { diagnostic?.End(ticket, diagnosticStatus, diagnosticFailure, ct); }
    }

    private static void Expect<T>(T actual,T expected,string label)
    {
        bool matches=EqualityComparer<T>.Default.Equals(actual,expected);
        Comparisons.Value?.Add(new { label,expected,actual,diff=matches?null:"MISMATCH" });
        if(!matches) throw new InvalidOperationException($"{label}: expected {expected}, observed {actual}.");
    }

    private static async Task Save(string path,object value)
    {
        string pending=path+".pending";
        await File.WriteAllTextAsync(pending,JsonSerializer.Serialize(value,Json));
        File.Move(pending,path,overwrite:true);
    }
}
