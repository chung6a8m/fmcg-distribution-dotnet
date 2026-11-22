using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Dapper;
using FmcgDms.Api.Authentication;
using FmcgDms.Api.Persistence;
using FmcgDms.Api.Tenancy;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal sealed partial class App02d1Dev
{
    private sealed record Snapshot(decimal Main,decimal MainValue,decimal Truck,decimal TruckValue,
        decimal Allocated,decimal AWarehouse,decimal ALoaded,decimal BWarehouse,decimal BLoaded,
        decimal Credit,decimal Consumed,int Financial);

    private async Task<Snapshot> SnapshotOf(Scene s)
    {
        await using var db=await App02c6Dev.ReadOnly(run,s.Tenant);
        return await db.QuerySingleAsync<Snapshot>("""
            SELECT m.OnHandBaseQuantity AS Main, m.OnHandCostAmount AS MainValue,
                t.OnHandBaseQuantity AS Truck,t.OnHandCostAmount AS TruckValue,p.AllocatedBaseQuantity AS Allocated,
                COALESCE(r.a,0)-COALESCE(l.a,0) AS AWarehouse,COALESCE(l.a,0) AS ALoaded,
                COALESCE(r.b,0)-COALESCE(l.b,0) AS BWarehouse,COALESCE(l.b,0) AS BLoaded,
                (SELECT COALESCE(SUM(GrantedAmount),0) FROM dbo.BCreditGrant WHERE TripId=@Trip) AS Credit,
                (SELECT COALESCE(SUM(ConsumedAmount),0) FROM dbo.BCreditGrant WHERE TripId=@Trip) AS Consumed,
                (SELECT COUNT(*) FROM dbo.SaleDelivery WHERE TripId=@Trip)
                +(SELECT COUNT(*) FROM dbo.CashReceipt WHERE TripId=@Trip)
                +(SELECT COUNT(*) FROM dbo.LedgerEffect e JOIN dbo.BusinessSourceLine bl
                    ON bl.Id=e.BusinessSourceLineId AND bl.TenantId=e.TenantId
                    JOIN dbo.BusinessSource bs ON bs.Id=bl.BusinessSourceId AND bs.TenantId=bl.TenantId
                    WHERE bs.TripId=@Trip AND e.LedgerKind IN ('AR','COGS','CUSTODY','FUND')) AS Financial
            FROM dbo.PhysicalStockBalance m JOIN dbo.PhysicalStockBalance t ON t.TenantId=m.TenantId AND t.SkuId=m.SkuId
            JOIN dbo.StockPool p ON p.TenantId=m.TenantId AND p.StockLocationId=m.StockLocationId AND p.SkuId=m.SkuId
            CROSS JOIN (SELECT SUM(CASE WHEN RightKind='A' THEN GrantedBaseQuantity END) AS a,
                SUM(CASE WHEN RightKind='B' THEN GrantedBaseQuantity END) AS b FROM dbo.StockRight WHERE TripId=@Trip AND SkuId=@Sku) r
            CROSS JOIN (SELECT SUM(CASE WHEN RightKind='A' THEN LoadedBaseQuantity END) AS a,
                SUM(CASE WHEN RightKind='B' THEN LoadedBaseQuantity END) AS b FROM dbo.TripManifestLine WHERE TripId=@Trip AND SkuId=@Sku) l
            WHERE m.TenantId=@Tenant AND m.SkuId=@Sku AND m.StockLocationId=@Main AND t.StockLocationId=@Truck
            """,s);
    }

    private async Task<string> Fingerprint(string tenant)
    {
        await using var db=await App02c6Dev.ReadOnly(run,tenant);
        return await db.QuerySingleAsync<string>("""
            SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONCAT(
                (SELECT * FROM dbo.BusinessSource ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                (SELECT * FROM dbo.BusinessSourceLine ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                (SELECT * FROM dbo.StockPool ORDER BY StockLocationId,SkuId FOR JSON PATH, INCLUDE_NULL_VALUES),
                (SELECT * FROM dbo.StockRight ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                (SELECT * FROM dbo.BQuota ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                (SELECT * FROM dbo.BCreditPool ORDER BY TripId,CustomerId FOR JSON PATH, INCLUDE_NULL_VALUES),
                (SELECT * FROM dbo.BCreditGrant ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                (SELECT * FROM dbo.TripManifest ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                (SELECT * FROM dbo.TripManifestLine ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                (SELECT * FROM dbo.PhysicalStockBalance ORDER BY StockLocationId,SkuId FOR JSON PATH, INCLUDE_NULL_VALUES),
                (SELECT * FROM dbo.LedgerEffect ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                (SELECT * FROM dbo.SaleDelivery ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
                (SELECT * FROM dbo.ReceivableOpenItem ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES))),2)
            """);
    }

    private async Task Record(string id,string scope,Func<Task> verify)
    {
        var log=new List<object>();
        Comparisons.Value=log;
        try
        {
            await verify();
            results.Add(new(id,scope,"PASS",log.ToArray(),null));
        }
        catch(Exception ex) when(!ct.IsCancellationRequested)
        {
            results.Add(new(id,scope,"FAIL",log.ToArray(),ex.Message));
            Console.Error.WriteLine($"{id}/{scope}: {ex.Message}");
        }
        finally { Comparisons.Value=null; }
    }

    internal async Task Complete(int browserExit,Func<bool,Task> restart)
    {
        string browserDir=Path.Combine(root,"web","test-results-app02d1",run.Id);
        string junit=Path.Combine(browserDir,"junit.xml");
        try
        {
            foreach(var s in canonical.Values)
            {
                await Record("D1-01",Slot(s.Tenant),()=>Migration(s));
                await Record("D1-02",Slot(s.Tenant),async ()=>
                {
                    await BrowserCheck(s,browserExit,junit,"stock-opening",new(100,1000,0,0,0,0,0,0,0,0,0,0));
                    await BrowserCheck(s,browserExit,junit,"opening",new(100,1000,0,0,40,40,0,0,0,0,0,0));
                    await BrowserCheck(s,browserExit,junit,"quota-rejected",new(100,1000,0,0,70,40,0,30,0,0,0,0));
                });
                await Record("D1-03",Slot(s.Tenant),()=>QuotaRace(s.Tenant));
                await Record("D1-04",Slot(s.Tenant),async()=>
                {
                    await BrowserCheck(s,browserExit,junit,"credit",new(100,1000,0,0,70,40,0,30,0,100,0,0));
                    var zero=await Seed(s.Tenant,"D1-ZERO",0);
                    await Denied(zero,"finance",D1+"/credit",Credit(zero,1),"CREDIT_LIMIT_INSUFFICIENT");
                });
                await Record("D1-05",Slot(s.Tenant),()=>PolicyNegatives(s.Tenant));
                await Record("D1-06",Slot(s.Tenant),()=>CreditRace(s.Tenant));
                await Record("D1-07",Slot(s.Tenant),async()=>
                {
                    await BrowserCheck(s,browserExit,junit,"b10",new(90,900,10,100,60,40,0,20,10,100,0,0));
                    await BrowserCheck(s,browserExit,junit,"b20",new(70,700,30,300,40,40,0,0,30,100,0,0));
                    await BrowserCheck(s,browserExit,junit,"a40",new(30,300,70,700,0,0,40,0,30,100,0,0));
                    await LoadLineage(s);
                });
                await Record("D1-08",Slot(s.Tenant),()=>InvalidLoad(s.Tenant));
                await Record("D1-09",Slot(s.Tenant),()=>LoadRace(s.Tenant));
                await Record("D1-10",Slot(s.Tenant),()=>Rollback(s.Tenant));
                await Record("D1-11",Slot(s.Tenant),()=>Recovery(s.Tenant,restart));
                await Record("D1-14",Slot(s.Tenant),async()=>
                {
                    await BrowserCheck(s,browserExit,junit,"offline",new(90,900,10,100,60,40,0,20,10,100,0,0));
                    await BrowserCheck(s,browserExit,junit,"a40",new(30,300,70,700,0,0,40,0,30,100,0,0));
                });
            }
            foreach(var own in canonical.Values)
            {
                var peer=canonical[Tenant(Slot(own.Tenant)=="A"?"B":"A")];
                await Record("D1-12",$"{Slot(own.Tenant)}-to-{Slot(peer.Tenant)}",async ()=>
                {
                    var final=new Snapshot(30,300,70,700,0,0,40,0,30,100,0,0);
                    await BrowserCheck(own,browserExit,junit,"revoke-warehouse",final);
                    await BrowserCheck(own,browserExit,junit,"restore-warehouse",final);
                    await TenantDenial(own,peer);
                });
            }
            foreach(var s in canonical.Values)
                await Record("D1-13",Slot(s.Tenant),()=>LegacyFence(s.Tenant,restart));
        }
        finally
        {
            string report=Path.Combine(evidencePath,"sql-review.json");
            await Save(report,new
            {
                source,apiSha256,runId=run.Id,physical="NOT_RUN",host=Environment.MachineName,instance=DemoRun.Instance,
                matrixSha256=MatrixSha, contractSha256=ContractSha,
                fixtureSha256=Hash(Path.Combine(evidencePath,"fixture.json")),
                browserReportSha256=File.Exists(junit)?Hash(junit):null,
                checkpoints=Directory.GetFiles(Checkpoints,"*.response.json").Order().Select(p=>new { name=Path.GetFileName(p),sha256=Hash(p) }),
                cases=results.OrderBy(x=>x.CaseId).ThenBy(x=>x.Scope),http
            });
            Console.WriteLine($"APP-02D.1 SQL comparison: {report}");
        }
        if (results.Count!=28 || results.Any(x=>x.Status!="PASS"))
            throw new InvalidOperationException("D.1 has incomplete or failed case evidence.");
    }

    private async Task BrowserCheck(Scene s,int exit,string junit,string phase,Snapshot expected)
    {
        string path=Path.Combine(Checkpoints,$"{Slot(s.Tenant)}-{phase}.response.json");
        if(!File.Exists(junit) || !File.Exists(path))
            throw new InvalidOperationException($"Browser checkpoint {phase} absent for {Slot(s.Tenant)}.");
        var report=XDocument.Load(junit);
        var tests=report.Descendants("testcase").Where(n=>((string?)n.Attribute("name"))?.Contains(Slot(s.Tenant)+" ",StringComparison.Ordinal)==true).ToArray();
        Expect(tests.Length>0 && tests.All(x=>x.Element("failure")==null && x.Element("skipped")==null),true,$"Browser tenant {Slot(s.Tenant)} (exit {exit})");
        var actual=JsonSerializer.Deserialize<Snapshot>(await File.ReadAllTextAsync(path,ct),Json);
        Expect(actual,expected,phase);
    }

    private async Task Denied(Scene s,string role,string path,object input,string? reason=null)
    {
        string before=await Fingerprint(s.Tenant);
        var result=await Send(s,role,HttpMethod.Post,path,input);
        Expect((int)result.Status>=400 && (int)result.Status<500,true,$"Rejected {path} ({result.Reason}, {(int)result.Status})");
        if (reason is not null) Expect(result.Reason,reason,"Rejection reason");
        Expect(await Fingerprint(s.Tenant),before,"Protected SQL unchanged");
    }

    private async Task LoadLineage(Scene s)
    {
        await using var read = await App02c6Dev.ReadOnly(run,s.Tenant);
        var observed=(await read.QueryAsync<string>("""
            SELECT CONCAT(l.RightKind,'|',l.LoadedBaseQuantity,'|',l.LoadedCostAmount,'|',
                (SELECT COUNT(*) FROM dbo.LedgerEffect e JOIN dbo.BusinessSourceLine bl
                    ON bl.Id=e.BusinessSourceLineId AND bl.TenantId=e.TenantId
                    WHERE bl.Id=m.LoadSourceLineId AND e.TenantId=m.TenantId AND e.LedgerKind='PHYSICAL_STOCK'))
            FROM dbo.TripManifestLine l JOIN dbo.PhysicalStockMovement m ON m.TripManifestLineId=l.Id AND m.TenantId=l.TenantId
            WHERE l.TripId=@Trip
            ORDER BY l.RightKind,l.LoadedBaseQuantity
            """,s)).ToArray();
        Expect(string.Join(",",observed),"A|40.000|400.0000|2,B|10.000|100.0000|2,B|20.000|200.0000|2","manifest lineage and paired transfer legs");
        var legs=(await read.QueryAsync<string>("""
            SELECT CONCAT(l.RightKind,'|',l.LoadedBaseQuantity,'|',e.EffectKey,'|',e.QuantityDelta,'|',e.AmountDelta)
            FROM dbo.TripManifestLine l JOIN dbo.LedgerEffect e
                ON e.BusinessSourceLineId=l.LoadSourceLineId AND e.TenantId=l.TenantId
            WHERE l.TripId=@Trip AND e.LedgerKind='PHYSICAL_STOCK'
            ORDER BY l.RightKind,l.LoadedBaseQuantity,e.EffectKey
            """,s)).ToArray();
        Expect(string.Join(",",legs),string.Join(",",new[]
        {
            "A|40.000|PHYSICAL-IN|40.000|400.0000","A|40.000|PHYSICAL-OUT|-40.000|-400.0000",
            "B|10.000|PHYSICAL-IN|10.000|100.0000","B|10.000|PHYSICAL-OUT|-10.000|-100.0000",
            "B|20.000|PHYSICAL-IN|20.000|200.0000","B|20.000|PHYSICAL-OUT|-20.000|-200.0000"
        }),"Exact signed MAIN/TRIP ledger deltas per source");
        var grants=(await read.QueryAsync<string>("""
            SELECT CONCAT(g.GrantedAmount,'|',g.ConsumedAmount,'|',a.PolicyVersion,'|',
                IIF(g.ShipToSnapshotId=@ShipTo,'BOUND','WRONG'),'|',
                (SELECT COUNT(*) FROM dbo.LedgerEffect e WHERE e.BusinessSourceLineId=g.BusinessSourceLineId AND e.LedgerKind='RIGHT_CREDIT_B'))
            FROM dbo.BCreditGrant g JOIN dbo.CreditPolicyApproval a ON a.Id=g.ApprovalId AND a.TenantId=g.TenantId
            WHERE g.TripId=@Trip AND g.CustomerId=@Customer
            """,s)).ToArray();
        Expect(string.Join(",",grants),"100.0000|0.0000|1|BOUND|1","credit proof, scope and source cardinality");
    }
}
