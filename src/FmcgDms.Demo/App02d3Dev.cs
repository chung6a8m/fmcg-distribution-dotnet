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

internal sealed record D3StockFixture(decimal A = 40, decimal B = 30, decimal FirstB = 10,
    decimal SecondB = 20, bool LoadA = true, decimal OpeningQuantity = 100, decimal OpeningValue = 1000, bool DeferReservations = false);
internal sealed record D3Offer(string ProducerId, string CommandId, string SourceNumber, Guid TripId, decimal OfferedAmount);
internal sealed record D3Accept(string ProducerId, string CommandId, string SourceNumber, Guid TripId, Guid OfferId, string CountEvidenceRef);
internal sealed record D3Unload(string ProducerId, string CommandId, string SourceNumber, Guid TripId);

internal sealed partial class App02d3Dev(string root, DemoRun run, string source, string apiSha,
    App02d1Dev d1, Func<string,string,string,string> sign)
{
    private const string Path3 = "/api/synthetic/app02d3";
    private const string BSale = "/api/synthetic/app02d2/sales";
    private const string ADeliver = "/api/synthetic/app02c4/deliver";
    private const string ContractSha = "EEABE9CF574836054287A7CB2CE11367395C7BA8AF86028B75FE9A002D62D1D4";
    private const string MatrixSha = "573A81A0106F2D2B1CE444D55E0C36CAB8619E9F5ABFC629104A17FAAFAB9BFF";
    private const string FixtureSha = "C453346BCDF5AE74403FCB3E2057CFD49D44ECD8B7D05A0804C308EEF463B0A5";
    private const string OracleSha = "8273CE3114EBFF437A616B3E77022707CD3629EE743870AA745ACCE75E1562CF";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly AsyncLocal<List<object>?> Current = new();
    private readonly string evidence = Path.Combine(root,"TestResults","app02d3",run.Id);
    private string Checkpoints => Path.Combine(evidence,"checkpoints");
    private readonly List<CaseResult> results = [];
    private readonly List<object> http = [];
    private readonly Dictionary<string, BrowserScene> canonical = [];
    private readonly Dictionary<string, UpgradeState> upgrades = [];
    private readonly Dictionary<(string Tenant,string Phase), D3State> phases = [];
    private readonly Dictionary<string,List<object>> t2Checks = [];
    private HttpClient client = null!;
    private string url = "";
    private CancellationToken ct;
    private Func<string,Task> restart = null!;
    private JsonElement oracle;

    private sealed record BrowserScene(D2Scene Scene, string CashWitness, string CashierEvidence);
    private sealed record UpgradeState(D2Scene History, long BeforeVersion, long AfterVersion, string Before, string After);
    private sealed record Checkpoint(string Slot, string Phase);
    private sealed record CaseResult(string CaseId, string Scope, string Status, object[] Comparisons, string? Error);
    private sealed record Reply(int Http, string? Status, string? Reason, Guid? Source, bool Replayed)
    {
        internal bool Applied => Http == 200 && Status == "APPLIED" && Source.HasValue;
        internal bool Denied => !Applied && (Status is "REJECTED" or "CONFLICT" || Http is >= 400 and < 500);
    }

    internal void VerifyDocuments()
    {
        Guard("docs/roadmap/app-02d3-dev-contract.md",ContractSha);
        Guard("docs/roadmap/app-02d3-dev-acceptance-matrix.md",MatrixSha);
        Guard("scripts/fixtures/app02d3/fixture-v0.1.json",FixtureSha);
        oracle = JsonSerializer.Deserialize<JsonElement>(Guard("business/reference-data/expected-results/golden-oracles.json",OracleSha))
            .GetProperty("AC-03");
    }

    private string Guard(string file, string expected)
    {
        string text = File.ReadAllText(Path.Combine(root,file)).Replace("\r\n","\n",StringComparison.Ordinal);
        if (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))) != expected)
            throw new InvalidDataException($"Approved D.3 input changed: {file}.");
        return text;
    }

    internal async Task Initialize(HttpClient httpClient,string webUrl,CancellationToken cancellation,Func<string,Task> restartApi)
    {
        client=httpClient;
        url=webUrl;
        ct=cancellation;
        restart=restartApi;
        Directory.CreateDirectory(Checkpoints);
        foreach (string tenant in DemoRun.Tenants)
        {
            var historical=await d1.SeedD2(tenant,"D3-HISTORY");
            Applied(await Post(tenant,BSale,await Sale(historical,10,100)), "D.2 price10 predecessor");
            long from=await Version(tenant);
            string before=await D2Sql.Fingerprint(run,tenant);
            ScratchMigrator.Migrate(run.Connection(tenant),tenant,run.Id,LogLevel.Warning,true);
            await run.Verify(tenant);
            long to=await Version(tenant);
            string after=await D2Sql.Fingerprint(run,tenant);
            upgrades.Add(tenant,new(historical,from,to,before,after));
        }

        await restart("on");
        foreach (string tenant in DemoRun.Tenants)
        {
            var scene=await Seed(tenant,"D3-CANON",new(FirstB:0,SecondB:0,LoadA:false,DeferReservations:true));
            string cash=await d1.SeedD2Cash(scene,200);
            string count=await Count(scene,200);
            canonical.Add(tenant,new(scene,cash,count));
        }

        await Save(Path.Combine(evidence,"fixture.json"),new
        {
            version="D3-v0.1",clock=App02d1Dev.StartTime,contractSha256=ContractSha,matrixSha256=MatrixSha,
            fixtureDefinitionSha256=FixtureSha,oracleSha256=OracleSha,upgrades,
            scenes=canonical.Select(pair => new
            {
                slot=Slot(pair.Key),pair.Value.Scene.Trip,pair.Value.Scene.Customer,pair.Value.Scene.ShipTo,
                pair.Value.Scene.Selection,pair.Value.Scene.Outlet,pair.Value.Scene.Sku,pair.Value.Scene.Main,
                pair.Value.Scene.Truck,pair.Value.Scene.Order,pair.Value.Scene.ARight,pair.Value.Scene.BRight,
                cashWitness=pair.Value.CashWitness,cashierEvidence=pair.Value.CashierEvidence
            })
        });
    }

    internal void ConfigureBrowser(ProcessStartInfo browser)
    {
        browser.Environment["APP02D3_BROWSER_FIXTURE"]=Path.Combine(evidence,"fixture.json");
        browser.Environment["APP02D3_CHECKPOINTS"]=Checkpoints;
        foreach (string tenant in DemoRun.Tenants)
        foreach (string role in new[]{"sales","warehouse","cashier","finance"})
            browser.Environment[$"APP02D3_{role.ToUpperInvariant()}_TOKEN_{Slot(tenant)}"]=Credential(tenant,role);
    }

    internal async Task Serve(Process process)
    {
        while (!process.HasExited)
        {
            foreach (string request in Directory.GetFiles(Checkpoints,"*.request.json"))
            {
                string output=request[..^".request.json".Length]+".response.json";
                if (File.Exists(output)) continue;
                Checkpoint ask=JsonSerializer.Deserialize<Checkpoint>(await File.ReadAllTextAsync(request,ct),Json)
                    ?? throw new InvalidDataException("Missing D3 checkpoint.");
                string tenant=ask.Slot switch {"A"=>"tenant-a","B"=>"tenant-b",_=>throw new InvalidDataException("Invalid tenant slot.")};
                if (Path.GetFileName(request)!=$"{ask.Slot}-{ask.Phase}.request.json")
                    throw new InvalidDataException("Invalid checkpoint filename.");
                var scene=canonical[tenant].Scene;
                if (ask.Phase.StartsWith("revoke-",StringComparison.Ordinal) || ask.Phase.StartsWith("restore-",StringComparison.Ordinal))
                {
                    string[] parts=ask.Phase.Split('-');
                    await Assign(scene,parts[1],parts[0]=="restore");
                    await Save(output,new {ask.Slot,ask.Phase,ok=true});
                    continue;
                }
                if (ask.Phase is not ("t0" or "t1" or "t2" or "t3" or "t4" or "offer" or "t5" or "t6"))
                    throw new InvalidDataException($"Unknown D.3 checkpoint {ask.Phase}.");
                if (ask.Phase=="t2")
                {
                    var comparisons=new List<object>();
                    t2Checks[tenant]=comparisons;
                    Current.Value=comparisons;
                    try { await RejectedB(await Locate(scene)); }
                    finally { Current.Value=null; }
                }
                if (ask.Phase=="t0")
                {
                    scene=await ResolveRights(scene);
                    canonical[tenant]=canonical[tenant] with {Scene=scene};
                }
                scene=await Locate(scene);
                canonical[tenant]=canonical[tenant] with {Scene=scene};
                var state=await Read(scene);
                phases.Add((tenant,ask.Phase),state);
                await Save(output,new {ask.Slot,ask.Phase,ok=true,state,scene.ALine,scene.B10,scene.B20,scene.ARight,scene.BRight});
            }
            await Task.Delay(55,ct);
        }
    }

    private async Task<D2Scene> Seed(string tenant,string code,D3StockFixture? fixture=null,bool price=true)
    {
        var s=await d1.SeedD2(tenant,code,stock:fixture);
        await using var db=await Owner(tenant);
        foreach (string role in new[]{"cashier","warehouse","sales"})
        {
            string actor=Actor(tenant,"other-"+role);
            await db.ExecuteAsync("""
                IF NOT EXISTS(SELECT 1 FROM dbo.App02ActorRoleGrant WHERE TenantId=@tenant AND ActorId=@actor AND RoleCode=@role)
                  INSERT dbo.App02ActorRoleGrant(TenantId,ActorId,RoleCode,IsActive,ChangedBy)
                  VALUES(@tenant,@actor,@role,1,'d3-fixture');
                """, new {tenant,actor,role});
        }
        string cashier=Actor(tenant,"cashier");
        await db.ExecuteAsync("""
            IF NOT EXISTS(SELECT 1 FROM dbo.App02ActorRoleGrant WHERE TenantId=@Tenant AND ActorId=@cashier AND RoleCode='cashier')
              INSERT dbo.App02ActorRoleGrant(TenantId,ActorId,RoleCode,IsActive,ChangedBy)
              VALUES(@Tenant,@cashier,'cashier',1,'d3-fixture');
            INSERT dbo.D3CashierTrip(TenantId,TripId,ActorId) VALUES(@Tenant,@Trip,@cashier);
            """,new {s.Tenant,s.Trip,cashier});
        if (price)
            await db.ExecuteAsync("""
                INSERT dbo.D3PriceProfile(TenantId,TripId,SkuId,PolicyVersion,UnitPrice,Decision,
                    EffectiveFromUtc,EffectiveUntilUtc,ApprovedAtUtc,Maker,Checker,EvidenceRef)
                VALUES(@Tenant,@Trip,@Sku,1,15,'APPROVED','2026-11-22T03:00:00','2026-11-22T05:00:00',
                    '2026-11-22T02:55:00',@maker,@checker,'D3-v0.1');
                """,new {s.Tenant,s.Trip,s.Sku,maker=Actor(tenant,"credit_maker"),checker=Actor(tenant,"credit_checker")});
        return s;
    }

    private async Task Assign(D2Scene s,string role,bool current)
    {
        string table=role switch {"sales"=>"C4DeliveryWriter","warehouse"=>"C3TripWriter","cashier"=>"D3CashierTrip",
            _=>throw new ArgumentOutOfRangeException(nameof(role))};
        string actor=Actor(s.Tenant,current?role:"other-"+role);
        await using var db=await Owner(s.Tenant);
        Require(await db.ExecuteAsync($"UPDATE dbo.{table} SET ActorId=@actor WHERE TenantId=@Tenant AND TripId=@Trip;",
            new {s.Tenant,s.Trip,actor}),1,$"{role} assignment changed on exact trip");
    }

    private async Task<string> Count(D2Scene s,decimal amount,string? by=null,DateTime? at=null)
    {
        string proof="D3-COUNT-"+Key();
        await using var db=await Owner(s.Tenant);
        await db.ExecuteAsync("""
            INSERT dbo.CashierCountEvidence(TenantId,EvidenceRef,TripId,CountedAmount,CountedBy,VerificationStatus,CountedAtUtc)
            VALUES(@Tenant,@proof,@Trip,@amount,@actor,'VERIFIED',@time);
            """,new {s.Tenant,s.Trip,proof,amount,actor=by??Actor(s.Tenant,"cashier"),
                time=at??App02d1Dev.StartTime.AddMinutes(-2).UtcDateTime});
        return proof;
    }

    private async Task<D2Scene> ResolveRights(D2Scene s)
    {
        await using var db=await App02c6Dev.ReadOnly(run,s.Tenant);
        var rights=(await db.QueryAsync<(string Kind,Guid Id)>("""
            SELECT RightKind Kind,Id FROM dbo.StockRight WHERE TenantId=@Tenant AND TripId=@Trip AND SkuId=@Sku;
            """,s)).ToArray();
        var grant=await db.QuerySingleAsync<(Guid Grant,Guid Approval)>("""
            SELECT Id AS [Grant],ApprovalId AS Approval FROM dbo.BCreditGrant
            WHERE TenantId=@Tenant AND TripId=@Trip AND CustomerId=@Customer AND ShipToSnapshotId=@ShipTo;
            """,s);
        return s with {ARight=rights.Single(r=>r.Kind=="A").Id,BRight=rights.Single(r=>r.Kind=="B").Id,
            Grant=grant.Grant,Approval=grant.Approval};
    }

    private async Task<D2Scene> Locate(D2Scene s)
    {
        await using var db=await App02c6Dev.ReadOnly(run,s.Tenant);
        var b=(await db.QueryAsync<Guid>("""
            SELECT Id FROM dbo.TripManifestLine WHERE TenantId=@Tenant AND TripId=@Trip AND StockRightId=@BRight
            ORDER BY LoadedBaseQuantity,Id;
            """,s)).ToArray();
        var a=await db.QuerySingleOrDefaultAsync<Guid>("""
            SELECT Id FROM dbo.TripManifestLine WHERE TenantId=@Tenant AND TripId=@Trip AND StockRightId=@ARight;
            """,s);
        return s with {B10=b.ElementAtOrDefault(0),B20=b.ElementAtOrDefault(1),ALine=a};
    }

    private async Task<SqlConnection> Owner(string tenant)
    {
        await run.Verify(tenant);
        return new SqlConnection(run.Connection(tenant));
    }

    private async Task<long> Version(string tenant)
    {
        await using var db=await App02c6Dev.ReadOnly(run,tenant);
        return await db.ExecuteScalarAsync<long>("SELECT MAX(Version) FROM dbo.VersionInfo");
    }

    private static string Slot(string tenant) => tenant=="tenant-a"?"A":"B";
    private static string Actor(string tenant,string role) => $"d1-{tenant[^1]}-{role}";
    private static string Key()=>Guid.NewGuid().ToString("N");
    private string Credential(string tenant,string role) =>
        role is "sales" or "warehouse" or "finance" ? d1.Credential(tenant,role) :
        sign(tenant,role=="sales-cashier"?"cashier":role.StartsWith("other-",StringComparison.Ordinal)?role[6..]:role,
            Actor(tenant,role=="sales-cashier"?"sales":role));

    private D3Offer Offer(D2Scene s,decimal amount)=>new(Actor(s.Tenant,"sales"),Key(),Key(),s.Trip,amount);
    private D3Accept Accept(D2Scene s,Guid offer,string proof)=>new(Actor(s.Tenant,"cashier"),Key(),Key(),s.Trip,offer,proof);
    private D3Unload Unload(D2Scene s)=>new(Actor(s.Tenant,"warehouse"),Key(),Key(),s.Trip);
    private static C4DeliveryInput Delivery(D2Scene s,decimal qty=25)=>new(Guid.NewGuid(),s.Trip,s.ALine,1,qty);
    private async Task<D2Request> Sale(D2Scene s,decimal qty,decimal cash,Guid? line=null,string? pending=null) =>
        new(Actor(s.Tenant,"sales"),Key(),Key(),s.Trip,s.Customer,s.ShipTo,s.Selection,s.Outlet,line??s.B20,s.Sku,
            qty,cash,cash>0?await d1.SeedD2Cash(s,cash):"",pending);

    private Task<Reply> Post(string tenant,string path,object input,string role="sales") =>
        Send(tenant,HttpMethod.Post,path,input,role);
    private async Task<Reply> Send(string tenant,HttpMethod method,string path,object? input=null,string role="sales")
    {
        using var request=new HttpRequestMessage(method,url+path);
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",Credential(tenant,role));
        if (input is not null) request.Content=JsonContent.Create(input,options:Json);
        using var response=await client.SendAsync(request,ct);
        string body=await response.Content.ReadAsStringAsync(ct);
        JsonElement result=body.TrimStart().StartsWith('{')?JsonSerializer.Deserialize<JsonElement>(body):default;
        string? Value(string key)=>result.ValueKind==JsonValueKind.Object&&result.TryGetProperty(key,out var x)&&x.ValueKind==JsonValueKind.String?x.GetString():null;
        var reply=new Reply((int)response.StatusCode,Value("status"),Value("reasonCode")??Value("code"),
            Guid.TryParse(Value("sourceId"),out var id)?id:null,
            result.ValueKind==JsonValueKind.Object&&result.TryGetProperty("replayed",out var repeat)&&repeat.ValueKind==JsonValueKind.True);
        lock (http) http.Add(new {tenant,role,method=method.Method,path,reply});
        return reply;
    }

    private static Guid Applied(Reply reply,string label)
    {
        Require(reply.Applied,true,$"{label} (HTTP {reply.Http}, {reply.Status}/{reply.Reason})");
        return reply.Source!.Value;
    }

    private static void Require<T>(T actual,T expected,string label)
    {
        bool equal=EqualityComparer<T>.Default.Equals(actual,expected);
        Current.Value?.Add(new {label,expected,actual,diff=equal?null:"MISMATCH"});
        if (!equal) throw new InvalidOperationException($"{label}: expected {expected}, observed {actual}.");
    }

    private static async Task Save(string path,object value)
    {
        await File.WriteAllTextAsync(path+".pending",JsonSerializer.Serialize(value,Json));
        File.Move(path+".pending",path,overwrite:true);
    }

    private async Task Record(string id,string scope,Func<Task> verify)
    {
        var comparisons=new List<object>();
        Current.Value=comparisons;
        try
        {
            await verify();
            results.Add(new(id,scope,"PASS",comparisons.ToArray(),null));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"{id}/{scope}: {ex.Message}");
            results.Add(new(id,scope,"FAIL",comparisons.ToArray(),ex.Message));
        }
        finally { Current.Value=null; }
    }
}
