using System.Xml.Linq;
using Dapper;
using FmcgDms.Api.Persistence;

namespace FmcgDms.Demo;

internal sealed partial class App02d3Dev
{
    internal async Task Complete(int browserExit)
    {
        string junit=Path.Combine(root,"web","test-results-app02d3",run.Id,"junit.xml");
        try
        {
            foreach(string tenant in DemoRun.Tenants)
            {
                string slot=Slot(tenant);
                await Record("D3-01",slot,()=>UpgradeCase(tenant));
                await Record("D3-02",slot,()=>LoadCase(tenant));
                await Record("D3-03",slot,()=>CanonReject(tenant));
                await Record("D3-04",slot,()=>SalesCase(tenant));
                await Record("D3-05",slot,()=>OfferCase(tenant));
                await Record("D3-06",slot,()=>AcceptCase(tenant));
                await Record("D3-07",slot,()=>ProofCase(tenant));
                await Record("D3-08",slot,()=>ReturnCase(tenant));
                await Record("D3-09",slot,()=>PartialCase(tenant));
                await Record("D3-10",slot,()=>BlockReturnCase(tenant));
                await Record("D3-11",slot,()=>RoundCase(tenant));
                await Record("D3-12",slot,()=>CashRace(tenant));
                await Record("D3-13",slot,()=>ReturnRace(tenant));
                await Record("D3-14",slot,()=>FaultCase(tenant));
                await Record("D3-15",slot,()=>RecoveryCase(tenant));
                string peer=tenant=="tenant-a"?"tenant-b":"tenant-a";
                await Record("D3-16",$"{slot}-to-{Slot(peer)}",()=>TenantCase(tenant,peer));
                await Record("D3-17",slot,()=>AuthorityCase(tenant,browserExit));
                await Record("D3-18",slot,()=>CashScopesCase(tenant));
            }
        }
        finally
        {
            string report=Path.Combine(evidence,"sql-review.json");
            await Save(report,new
            {
                source,apiSha256=apiSha,runId=run.Id,physical="NOT_RUN",host=Environment.MachineName,instance=DemoRun.Instance,
                contractSha256=ContractSha,matrixSha256=MatrixSha,fixtureDefinitionSha256=FixtureSha,oracleSha256=OracleSha,
                fixtureSha256=Hash(Path.Combine(evidence,"fixture.json")),browserReportSha256=File.Exists(junit)?Hash(junit):null,
                checkpoints=Directory.GetFiles(Checkpoints,"*.json").Order().Select(p=>new {file=Path.GetFileName(p),sha256=Hash(p)}),
                cases=results.OrderBy(x=>x.CaseId).ThenBy(x=>x.Scope),http
            });
            Console.WriteLine($"APP-02D.3 SQL comparison: {report}");
        }
        if (results.Count!=36||results.Any(x=>x.Status!="PASS")||browserExit!=0)
            throw new InvalidOperationException("D.3 has incomplete or failed case evidence.");
        var tests=XDocument.Load(junit).Descendants("testcase").ToArray();
        if (tests.Length<2||tests.Any(x=>x.Element("failure")!=null||x.Element("skipped")!=null||x.Element("error")!=null))
            throw new InvalidOperationException("D.3 browser evidence is incomplete.");
    }

    private async Task UpgradeCase(string tenant)
    {
        var u=upgrades[tenant];
        d1.VerifyD1History(tenant);
        Require(u.BeforeVersion,202611220008L,"Upgrade from exact D.2 boundary");
        Require(u.AfterVersion,202611220010L,"D.3 forward migrations applied");
        Require(u.After,u.Before,"Legacy C.3 and D.2 protected upgrade fingerprint");
        string before=await Fingerprint(tenant);
        ScratchMigrator.Migrate(run.Connection(tenant),tenant,run.Id,LogLevel.Warning,true);
        Require(await Fingerprint(tenant),before,"Reapplied migrations preserve protected rows");
        await using var db=await App02c6Dev.ReadOnly(run,tenant);
        Require(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.VersionInfo WHERE Version IN (202611220009,202611220010)"),2,
            "Exactly one row per D.3 migration");
        foreach(var mutate in new Action<Microsoft.Data.SqlClient.SqlConnectionStringBuilder>[]
        {
            b=>b.DataSource="refused-non-localdb", b=>b.InitialCatalog="master",
            b=>b.InitialCatalog=$"Fmcg_App00_{run.Id}_{(Slot(tenant)=="A"?"B":"A")}",
            b=>b.InitialCatalog=$"Fmcg_App00_{Key()}_{Slot(tenant)}"
        })
        {
            var target=new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(run.Connection(tenant));
            mutate(target);
            bool refused=false;
            try { ScratchMigrator.Migrate(target.ConnectionString,tenant,run.Id,LogLevel.Warning,true); }
            catch(InvalidOperationException) { refused=true; }
            Require(refused,true,"Unsafe migration target refused");
        }
        Guid manifest=await db.ExecuteScalarAsync<Guid>("SELECT TripManifestId FROM dbo.TripManifestLine WHERE Id=@B20",u.History);
        await restart("legacy");
        try
        {
            var legacy=new UnloadTripRemainderCommand(Actor(tenant,"warehouse"),Key(),Key(),u.History.Trip,manifest);
            var denied=await Post(tenant,"/api/synthetic/app01d/commands/unload-trip-remainder",legacy,"warehouse");
            Require(denied.Reason,"LOADED_MANIFEST_NOT_ELIGIBLE","Legacy unload preserves the modern fence");
        }
        finally { await restart("on"); }
        Require(await Fingerprint(tenant),before,"Legacy probe adds no protected effect");
        Guid legacyTrip=await db.QueryFirstAsync<Guid>("""
            SELECT TripId FROM dbo.TripManifest WHERE TenantId=@tenant AND IsC3=0 AND Status='LOADED' ORDER BY Id;
            """,new {tenant});
        await using(var owner=await Owner(tenant))
            await owner.ExecuteAsync("""
                INSERT dbo.C3TripWriter(TenantId,TripId,ActorId) VALUES(@tenant,@legacyTrip,@warehouse);
                INSERT dbo.D3CashierTrip(TenantId,TripId,ActorId) VALUES(@tenant,@legacyTrip,@cashier);
                """,new {tenant,legacyTrip,warehouse=Actor(tenant,"warehouse"),cashier=Actor(tenant,"cashier")});
        var protectedLegacy=u.History with {Trip=legacyTrip};
        Require((await Denied(protectedLegacy,Path3+"/unload",Unload(protectedLegacy),"warehouse",
            "D.3 cannot unload a legacy manifest")).Reason,"LOADED_MANIFEST_NOT_ELIGIBLE","D.3 enforces the reciprocal fence");
    }

    private async Task LoadCase(string tenant)
    {
        Ac03(tenant,"t0","T0");
        Ac03(tenant,"t1","T1_LOAD");
        var s=await Locate(canonical[tenant].Scene);
        foreach(var (line,qty,b) in new[]{(s.ALine,40m,false),(s.B10,10m,true),(s.B20,20m,true)})
        {
            var move=await D2Sql.LoadOrigin(run,s,line);
            Require(move.Right,b?s.BRight:s.ARight,"Load ancestry addresses exact A or B right");
            Require(move.D1Writer,b,"Correct dedicated B versus C.3 A load scope");
            Require(move.MainOutQuantity,-qty,"Signed MAIN load quantity");
            Require(move.MainOutValue,-qty*10,"Signed MAIN load value");
            Require(move.TruckInQuantity,qty,"Signed TRIP load quantity");
            Require(move.TruckInValue,qty*10,"Signed TRIP load value");
        }
    }
    private async Task RejectedB(D2Scene s)
    {
        foreach(var (qty,line,cash,label) in new[]
        {
            (31m,s.B20,465m,"B31 exceeds quota"),
            (11m,s.B10,165m,"B11 exceeds exact B10 line"),
            (1m,s.ALine,15m,"B sale cannot use A line")
        })
            await Denied(s,BSale,await Sale(s,qty,cash,line),label:label);
    }
    private Task CanonReject(string tenant)
    {
        Current.Value!.AddRange(t2Checks[tenant]);
        Ac03(tenant,"t2","T2_REJECT");
        return Task.CompletedTask;
    }
    private async Task SalesCase(string tenant)
    {
        Ac03(tenant,"t3","T3_B20");
        Ac03(tenant,"t4","T4_A25");
        var s=await Locate(canonical[tenant].Scene);
        Guid bSource=await GetSource(s,"B_SALE_DELIVERY");
        var b=(await D2Sql.Source(run,s,bSource)).Row;
        Require(b.Quantity,20m,"B quantity");
        Require(b.Payable,300m,"B15 times 20");
        Require(b.Cash,200m,"Exact verified cash");
        Require(b.ArNet,100m,"B net AR");
        Require(b.GrantConsumed,100m,"B grant use");
        Require(b.ManifestLine,s.B20,"Expected B20 manifest lineage");
        await using var db=await App02c6Dev.ReadOnly(run,tenant);
        Require(await db.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.D3SalePricing WHERE TenantId=@Tenant AND TripId=@Trip AND SkuId=@Sku
                AND SaleDeliveryId=@Delivery AND UnitPrice=15 AND PolicyVersion=1;
            """,new {s.Tenant,s.Trip,s.Sku,b.Delivery}),1,"Frozen B15 policy version/line");
        string due=await db.QuerySingleAsync<string>("""
            SELECT CONVERT(varchar(10),a.DueBusinessDate,23) FROM dbo.ReceivableOpenItem a
            JOIN dbo.SaleDelivery d ON d.TenantId=a.TenantId AND d.Id=a.SaleDeliveryId
            WHERE d.TenantId=@Tenant AND d.TripId=@Trip AND d.RightKind='A' AND d.TripManifestLineId=@ALine
                AND d.StockRightId=@ARight AND d.BaseQuantity=25 AND d.PayableAmount=375 AND d.CostAmount=250;
            """,s);
        Require(due,"2026-11-29","A frozen terms due date");
        var missing=await Seed(tenant,"D3-PRICE-MISSING",price:false);
        await Denied(missing,BSale,await Sale(missing,1,15),label:"D3 marked trip missing price");
        var forged=System.Text.Json.JsonSerializer.SerializeToNode(await Sale(s,1,15,s.B10),Json)!.AsObject();
        forged["unitPrice"]=1;
        await Denied(s,BSale,forged,label:"Request cannot set price");
    }
    private async Task OfferCase(string tenant)
    {
        var old=phases[(tenant,"t4")];
        var next=phases[(tenant,"offer")];
        Require(next.Balances,old.Balances,"Offer does not move money, stock or AR");
        Require(next.Extra.Offers,1,"One canonical offer");
        Require(next.Extra.Pending,1,"Canonical offer awaits count");
        var s=canonical[tenant].Scene;
        Guid offer=await GetSource(s,"CASH_HANDOVER_OFFER");
        Require((await Effects(tenant,offer)).Length,0,"No ledger for offer");
        var cash=await Active(tenant,"D3-OFFER-DENIAL");
        await Denied(cash,Path3+"/offer",Offer(cash,201),label:"Only confirmed custody can be offered");
        Applied(await Post(tenant,Path3+"/offer",Offer(cash,200)),"First pending offer");
        await Denied(cash,Path3+"/offer",Offer(cash,1),label:"Second pending offer denied");
        var bank=await Seed(tenant,"D3-BANK");
        Applied(await Post(tenant,BSale,await Sale(bank,1,0,pending:await d1.SeedD2Bank(bank,15))),"Bank pending sale");
        await Denied(bank,Path3+"/offer",Offer(bank,15),label:"Pending bank does not create custody");
        var credit=await Seed(tenant,"D3-OVERPAY");
        Applied(await Post(tenant,BSale,await Sale(credit,6,110)),"Cash overpayment sale");
        var paid=await Read(credit);
        Require(paid.Balances.Custody,110m,"Only confirmed cash contributes to custody");
        Require(paid.Balances.CustomerCredit,20m,"Overpayment gives credit20");
        await Denied(credit,Path3+"/offer",Offer(credit,130),label:"CustomerCredit cannot count as extra cash");
    }
    private async Task AcceptCase(string tenant)
    {
        Ac03(tenant,"t5","T5_HANDOVER");
        var s=canonical[tenant].Scene;
        await CheckHandover(s,await GetSource(s,"CASH_HANDOVER_OFFER"),await GetSource(s,"CASH_HANDOVER_ACCEPT"),
            200,canonical[tenant].CashierEvidence);
        var offer=phases[(tenant,"offer")].Balances;
        var accepted=phases[(tenant,"t5")].Balances;
        Require(accepted,offer with {Custody=0,Fund=200},"Only custody and fund changed");
    }
    private async Task ReturnCase(string tenant)
    {
        Ac03(tenant,"t6","T6_RETURN");
        var s=await Locate(canonical[tenant].Scene);
        await CheckReturn(s,25,250,10,0);
        Require(phases[(tenant,"t6")].Extra.TripStatus,"RECONCILING","DEV terminal write fence");
        await using var db=await App02c6Dev.ReadOnly(run,tenant);
        string rights=await db.QuerySingleAsync<string>("""
            SELECT STRING_AGG(CONCAT(RightKind,':',Status,':',ConsumedBaseQuantity,':',ReleasedBaseQuantity),',')
                WITHIN GROUP(ORDER BY RightKind)
            FROM dbo.StockRight WHERE TenantId=@Tenant AND TripId=@Trip;
            """,s);
        Require(rights,"A:HELD:25.000:0.000,B:RELEASED:20.000:10.000","Terminal A and B right state");
    }

    private async Task<Reply> Denied(D2Scene s,string path,object input,string role="sales",string label="Rejected command")
    {
        string before=await Fingerprint(s.Tenant);
        var response=await Post(s.Tenant,path,input,role);
        Require(response.Denied,true,$"{label} HTTP {response.Http} {response.Status}/{response.Reason}");
        Require(response.Source,(Guid?)null,label+" produced no source");
        Require(await Fingerprint(s.Tenant),before,label+" leaves protected SQL unchanged");
        return response;
    }

    private async Task<D2Scene> Active(string tenant,string code,decimal qty=20,decimal cash=200,D3StockFixture? stock=null)
    {
        var s=await Seed(tenant,code,stock);
        Applied(await Post(tenant,BSale,await Sale(s,qty,cash)),"Verified B receipt fixture posted");
        return s;
    }
    private async Task<Guid> Handover(D2Scene s,decimal cash)
    {
        Guid offer=Applied(await Post(s.Tenant,Path3+"/offer",Offer(s,cash)),"Offer posted");
        return Applied(await Post(s.Tenant,Path3+"/accept",Accept(s,offer,await Count(s,cash)),"cashier"),"Independent cashier acceptance");
    }
}
