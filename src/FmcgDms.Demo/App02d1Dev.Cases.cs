using System.Net;
using System.Security.Claims;
using Dapper;
using FmcgDms.Api.Persistence;
using FmcgDms.Api.Tenancy;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal sealed partial class App02d1Dev
{
    private async Task Migration(Scene s)
    {
        VerifyUpgrade(s.Tenant);
        string before=await Fingerprint(s.Tenant);
        ScratchMigrator.Migrate(run.Connection(s.Tenant),s.Tenant,run.Id,LogLevel.Warning,true);
        await run.Verify(s.Tenant);
        Expect(await Fingerprint(s.Tenant),before,"Reapplication preserves existing source/manifest/ledger");
        await using var db=await App02c6Dev.ReadOnly(run,s.Tenant);
        Expect(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.VersionInfo WHERE Version=202611220007"),1,"Load migration applied once");
        foreach(var change in new Action<SqlConnectionStringBuilder>[]
        {
            b=>b.InitialCatalog="master", b=>b.DataSource="refused-non-localdb",
            b=>b.InitialCatalog=$"Fmcg_App00_{Guid.NewGuid():N}_{Slot(s.Tenant)}",
            b=>b.InitialCatalog=$"Fmcg_App00_{run.Id}_{(Slot(s.Tenant)=="A"?"B":"A")}"
        })
        {
            var invalid=new SqlConnectionStringBuilder(run.Connection(s.Tenant));
            change(invalid);
            bool refused=false;
            try { ScratchMigrator.Migrate(invalid.ConnectionString,s.Tenant,run.Id,LogLevel.Warning,true); }
            catch(InvalidOperationException) { refused=true; }
            Expect(refused,true,"Unsafe migration target refused");
        }
        // Wrong marker and crossed binding are changed only on the already verified, owned scratch slot.
        await using var owner=await Write(s.Tenant);
        string other=s.Tenant=="tenant-a"?"tenant-b":"tenant-a";
        using var host=Host.CreateDefaultBuilder().UseEnvironment("IntegrationTest").Build();
        var cfg=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["SyntheticAuth:Enabled"]="true", [$"TenantDatabases:{s.Tenant}"]=run.Connection(s.Tenant) }).Build();
        var guard=new TenantConnections(cfg,host.Services.GetRequiredService<IHostEnvironment>());
        var identity=new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id",s.Tenant)],"d1-guard"));
        // Alter only a scratch marker fixture inside a transaction that is always rolled back after a probe.
        int? markers=await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.App00ScratchMarker WHERE Nonce=@nonce",new {nonce=run.Id});
        Expect(markers,1,"Admitted nonce marker");
        await using var opened=await guard.OpenSyntheticScratchAsync(identity,ct);
        Expect(opened.Database,new SqlConnectionStringBuilder(run.Connection(s.Tenant)).InitialCatalog,"Admitted tenant database");
        var peerCfg=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["SyntheticAuth:Enabled"]="true", [$"TenantDatabases:{s.Tenant}"]=run.Connection(other) }).Build();
        bool wrongTenant=false;
        try { await using var peer=await new TenantConnections(peerCfg,host.Services.GetRequiredService<IHostEnvironment>()).OpenSyntheticScratchAsync(identity,ct); }
        catch(UnauthorizedAccessException) { wrongTenant=true; }
        Expect(wrongTenant,true,"Crossed binding refused");
    }

    private async Task QuotaRace(string tenant)
    {
        var s=await Seed(tenant,"D1-Q-RACE");
        await ReserveA(s,40);
        var bids=new[]{Quota(s,40),Quota(s,40)};
        var replies=await Task.WhenAll(bids.Select(b=>Send(s,"warehouse",HttpMethod.Post,D1+"/quota",b)));
        Expect(replies.Count(x=>x.Status==HttpStatusCode.OK),1,"One competing quota succeeds");
        Expect(replies.Count(x=>(int)x.Status>=400&&(int)x.Status<500),1,"One competing quota rejects");
        var winner=Array.FindIndex(replies,x=>x.Status==HttpStatusCode.OK);
        var replay=await Post(s,"warehouse",D1+"/quota",bids[winner]);
        Expect(replay.Source,replies[winner].Source,"Quota replay source");
        Expect(replay.Replayed,true,"Quota replay");
        Expect(await SnapshotOf(s),new Snapshot(100,1000,0,0,80,40,0,40,0,0,0,0),"Shared pool after quota race");
    }

    private async Task PolicyNegatives(string tenant)
    {
        var missing=await Seed(tenant,"D1-MISSING",decision:"MISSING");
        await Denied(missing,"finance",D1+"/credit",Credit(missing,1),"CREDIT_APPROVAL_REQUIRED");
        var unset=await Seed(tenant,"D1-UNSET",decision:"MISSING",cap:0);
        await Denied(unset,"finance",D1+"/credit",Credit(unset,1),"CREDIT_APPROVAL_REQUIRED");
        foreach(var (code,decision,from,until) in new[]
        {
            ("PENDING","PENDING",From,Until),("EXPIRED","APPROVED",From.AddHours(-2),From),
            ("FUTURE","APPROVED",Until,Until.AddHours(1))
        })
        {
            var s=await Seed(tenant,"D1-"+code,decision:decision,from:from,until:until);
            await Denied(s,"finance",D1+"/credit",Credit(s,1));
        }
        var ambiguous=await Seed(tenant,"D1-AMBIG");
        await Approval(ambiguous,100,"APPROVED");
        await Denied(ambiguous,"finance",D1+"/credit",Credit(ambiguous,1),"CREDIT_APPROVAL_AMBIGUOUS");
        var wrong=await Seed(tenant,"D1-WRONG");
        await Denied(wrong,"finance",D1+"/credit",Credit(wrong,1) with {PoolCode="A"});
        await Denied(wrong,"finance",D1+"/credit",Credit(wrong,1) with {ShipToSnapshotId=missing.ShipTo});
        var self=await Seed(tenant,"D1-SELF",decision:"MISSING");
        bool rejected=false;
        try { await Approval(self,100,"APPROVED",maker:Actor(tenant,"credit_maker"),checker:Actor(tenant,"credit_maker")); }
        catch(SqlException e) when(e.Number==547) { rejected=true; }
        Expect(rejected,true,"Approval schema rejects same maker/checker");
        var issuer=await Seed(tenant,"D1-ISSUER",checker:Actor(tenant,"finance"));
        await GrantRole(tenant,Actor(tenant,"finance"),"credit_checker");
        await Denied(issuer,"finance",D1+"/credit",Credit(issuer,1),"CREDIT_CHECKER_CONFLICT");
        var inactive=await Seed(tenant,"D1-INACTIVE");
        await using(var db=await Write(tenant))
            await db.ExecuteAsync("UPDATE dbo.Customer SET IsActive=0 WHERE Id=@Customer",inactive);
        await Denied(inactive,"finance",D1+"/credit",Credit(inactive,1));
        var boundary=await Seed(tenant,"D1-BOUND");
        try
        {
            await SetClock(From);
            await Post(boundary,"finance",D1+"/credit",Credit(boundary,1));
            await SetClock(Until);
            await Denied(boundary,"finance",D1+"/credit",Credit(boundary,1),"CREDIT_APPROVAL_REQUIRED");
            Expect((await SnapshotOf(boundary)).Credit,1m,"Half-open approval interval");
        }
        finally { await SetClock(StartTime); }
    }

    private async Task GrantRole(string tenant,string actor,string role)
    {
        await using var db=await Write(tenant);
        await db.ExecuteAsync("""
            IF NOT EXISTS(SELECT 1 FROM dbo.App02ActorRoleGrant WHERE TenantId=@tenant AND ActorId=@actor AND RoleCode=@role)
            INSERT dbo.App02ActorRoleGrant(TenantId,ActorId,RoleCode,IsActive,ChangedBy) VALUES(@tenant,@actor,@role,1,'d1-fixture')
            """,new {tenant,actor,role});
    }

    private async Task CreditRace(string tenant)
    {
        var s=await Seed(tenant,"D1-CR-RACE");
        var bids=new[]{Credit(s,80),Credit(s,80)};
        var replies=await Task.WhenAll(bids.Select(b=>Send(s,"finance",HttpMethod.Post,D1+"/credit",b)));
        Expect(replies.Count(x=>x.Status==HttpStatusCode.OK),1,"One competing credit succeeds");
        Expect(replies.Count(x=>(int)x.Status>=400&&(int)x.Status<500),1,"One competing credit rejects");
        var winner=Array.FindIndex(replies,x=>x.Status==HttpStatusCode.OK);
        var replay=await Post(s,"finance",D1+"/credit",bids[winner]);
        Expect(replay.Source,replies[winner].Source,"Credit replay source");
        Expect(replay.Replayed,true,"Credit replay");
        var address=Guid.NewGuid();
        await using(var db=await Write(tenant))
            await db.ExecuteAsync("""
                INSERT dbo.ShipToSnapshot(Id,TenantId,CustomerId,DisplayName,Address)
                VALUES(@address,@tenant,@Customer,'Alternate approved customer address','02 Duong DEV')
                """,new {address,tenant,s.Customer});
        await Denied(s,"finance",D1+"/credit",Credit(s,80) with {ShipToSnapshotId=address});
        Expect((await SnapshotOf(s)).Credit,80m,"Shared Customer cap");
    }

    private async Task InvalidLoad(string tenant)
    {
        var s=await Seed(tenant,"D1-INVALID");
        Guid a=await ReserveA(s,40);
        await Post(s,"warehouse",D1+"/quota",Quota(s,30));
        Guid b=await Right(s);
        await Post(s,"warehouse",D1+"/load",Load(s,b,30,10));
        foreach(var bad in new[] { Load(s,b,30,10),Load(s,b,20,21),Load(s,a,40,1),
            Load(s,Guid.NewGuid(),1,1),Load(s,b,20,1) with {TripId=Guid.NewGuid()} })
            await Denied(s,"warehouse",D1+"/load",bad);
        var cross=await Seed(tenant,"D1-XWH");
        var other=await AddWarehouse(cross);
        await Post(cross,"warehouse",D1+"/quota",Quota(cross,30));
        var mainRight=await Right(cross);
        await Post(cross,"warehouse",D1+"/quota",Quota(cross,20) with {StockLocationId=other});
        await using var read=await App02c6Dev.ReadOnly(run,tenant);
        var otherRight=await read.QuerySingleAsync<Guid>("SELECT Id FROM dbo.StockRight WHERE TripId=@Trip AND StockLocationId=@other",new {cross.Trip,other});
        await Denied(cross,"warehouse",D1+"/load",new C3LoadInput(Guid.NewGuid(),cross.Trip,[new(mainRight,30,1),new(otherRight,20,1)]));
        await Denied(s,"warehouse",D1+"/load",Load(s,otherRight,20,1));
        Expect((await SnapshotOf(s)).BWarehouse,20m,"Valid B20 still warehouse-held");
    }

    private async Task<Guid> AddWarehouse(Scene s)
    {
        var id=Guid.NewGuid();
        await using var db=await Write(s.Tenant);
        await db.ExecuteAsync("""
            INSERT dbo.StockLocation(Id,TenantId,Code,Kind) VALUES(@id,@Tenant,'D1-ALT-MAIN','WAREHOUSE');
            INSERT dbo.StockPool(TenantId,StockLocationId,SkuId,OnHandBaseQuantity) VALUES(@Tenant,@id,@Sku,100);
            INSERT dbo.PhysicalStockOpening(TenantId,StockLocationId,SkuId,SnapshotCode,BaseQuantity,CostAmount)
                VALUES(@Tenant,@id,@Sku,'D1-v0.1',100,1000);
            INSERT dbo.PhysicalStockBalance(TenantId,StockLocationId,SkuId,OnHandBaseQuantity,OnHandCostAmount)
                VALUES(@Tenant,@id,@Sku,100,1000);
            """,new {id,s.Tenant,s.Sku});
        return id;
    }

    private async Task LoadRace(string tenant)
    {
        var s=await Seed(tenant,"D1-L-RACE");
        await ReserveA(s,40);
        await Post(s,"warehouse",D1+"/quota",Quota(s,30));
        Guid b=await Right(s);
        var bids=new[]{Load(s,b,30,20),Load(s,b,30,20)};
        var replies=await Task.WhenAll(bids.Select(q=>Send(s,"warehouse",HttpMethod.Post,D1+"/load",q)));
        Expect(replies.Count(x=>x.Status==HttpStatusCode.OK),1,"One competing load succeeds");
        Expect(replies.Count(x=>(int)x.Status>=400&&(int)x.Status<500),1,"One competing load rejects");
        Expect(await SnapshotOf(s),new Snapshot(80,800,20,200,50,40,0,10,20,0,0,0),"Shared pool after load race");
    }

    private async Task Rollback(string tenant)
    {
        var s=await Seed(tenant,"D1-FAULT");
        await Post(s,"warehouse",D1+"/quota",Quota(s,20));
        Guid b=await Right(s);
        await Post(s,"warehouse",D1+"/quota",Quota(s,20));
        await using var read=await App02c6Dev.ReadOnly(run,tenant);
        Guid second=await read.QuerySingleAsync<Guid>("SELECT Id FROM dbo.StockRight WHERE TripId=@Trip AND Id<>@b",new {s.Trip,b});
        var sorted = new[] { b, second }.Order().ToArray();
        var request=new C3LoadInput(Guid.NewGuid(),s.Trip,[new(sorted[0],20,5),new(sorted[1],20,13)]);
        string before=await Fingerprint(tenant);
        await using var db=await Write(tenant);
        await db.ExecuteAsync("""
            CREATE TRIGGER dbo.TR_D1_DEV_Fault ON dbo.PhysicalStockMovement AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF EXISTS (SELECT 1 FROM inserted i WHERE i.BaseQuantity=13)
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM inserted i
                        JOIN dbo.TripManifestLine target ON target.Id=i.TripManifestLineId AND target.TenantId=i.TenantId
                        JOIN dbo.TripManifestLine prior ON prior.TripManifestId=target.TripManifestId AND prior.TenantId=target.TenantId
                        JOIN dbo.PhysicalStockMovement movement ON movement.TripManifestLineId=prior.Id AND movement.TenantId=prior.TenantId
                        WHERE movement.BaseQuantity=5)
                        THROW 51802,'D.1 fault ordering prerequisite missing',1;
                    THROW 51801,'D.1 synthetic second movement fault',1;
                END
            END
            """);
        try
        {
            var failed=await Send(s,"warehouse",HttpMethod.Post,D1+"/load",request);
            Expect(failed.Status,HttpStatusCode.InternalServerError,"Injected SQL fault");
            Expect(await Fingerprint(tenant),before,"All protected writes rolled back");
            Expect(await read.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.BusinessCommand WHERE CommandId=@id",
                new {id=request.CommandId.ToString("N")}),0,"Fault does not leave a command outcome");
        }
        finally { await db.ExecuteAsync("DROP TRIGGER dbo.TR_D1_DEV_Fault"); }
        await Post(s,"warehouse",D1+"/load",request);
        Expect(await SnapshotOf(s),new Snapshot(82,820,18,180,22,0,0,22,18,0,0,0),"Same-command recovery loads both lines");
    }
}
