using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;

namespace FmcgDms.Demo;

internal sealed partial class App02d3Dev
{
    private async Task ProofCase(string tenant)
    {
        var s=await Active(tenant,"D3-COUNT");
        Guid offer=Applied(await Post(tenant,Path3+"/offer",Offer(s,200)),"Offer waiting");
        var other=await Seed(tenant,"D3-COUNT-PEER-TRIP");
        var samples=new[]
        {
            ("D3-missing-count","Missing verified count"),
            ("D3-pending-count","Pending reference is absent from verified store"),
            (await Count(s,199),"Count too small"),
            (await Count(s,201),"Count too large"),
            (await Count(s,200,Actor(tenant,"other-cashier")),"Wrong counter actor"),
            (await Count(s,200,at:App02d1Dev.StartTime.AddMinutes(1).UtcDateTime),"Future count"),
            (await Count(other,200),"Wrong trip for count")
        };
        foreach(var (proof,label) in samples)
            await Denied(s,Path3+"/accept",Accept(s,offer,proof),"cashier",label);

        string sales=Actor(tenant,"sales");
        await using(var db=await Owner(tenant))
        {
            await db.ExecuteAsync("""
                INSERT dbo.App02ActorRoleGrant(TenantId,ActorId,RoleCode,IsActive,ChangedBy)
                VALUES(@Tenant,@sales,'cashier',1,'d3-proof-fixture');
                UPDATE dbo.D3CashierTrip SET ActorId=@sales WHERE TenantId=@Tenant AND TripId=@Trip;
                """,new {s.Tenant,s.Trip,sales});
        }
        try
        {
            var self=Accept(s,offer,await Count(s,200,sales)) with {ProducerId=sales};
            var denied=await Denied(s,Path3+"/accept",self,"sales-cashier","Sales cannot accept own offer");
            Require(denied.Reason,"CASHIER_MUST_BE_INDEPENDENT","Independence, rather than absent role, was checked");
        }
        finally { await Assign(s,"cashier",true); }
        string valid=await Count(s,200);
        Guid accepted=Applied(await Post(tenant,Path3+"/accept",Accept(s,offer,valid),"cashier"),"Valid count after rejections");
        await CheckHandover(s,offer,accepted,200,valid);
        Applied(await Post(tenant,BSale,await Sale(s,1,200,s.B10)),"Restock synthetic driver custody from verified cash");
        Guid second=Applied(await Post(tenant,Path3+"/offer",Offer(s,200)),"Separate offer");
        await Denied(s,Path3+"/accept",Accept(s,second,valid),"cashier","Count witness cannot back a second offer");
    }

    private async Task PartialCase(string tenant)
    {
        foreach(int variant in new[]{1,2,3})
        {
            var stock=variant==3?new D3StockFixture(FirstB:0,SecondB:0):new D3StockFixture(FirstB:5,SecondB:7);
            var s=await Seed(tenant,$"D3-PARTIAL-{variant}",stock);
            if(variant==1)
                Applied(await Post(tenant,BSale,await Sale(s,6,90)),"Sell six from second loaded batch");
            else if(variant==2)
            {
                Applied(await Post(tenant,BSale,await Sale(s,5,75,s.B10)),"Consume first loaded batch");
                Applied(await Post(tenant,BSale,await Sale(s,7,105,s.B20)),"Consume second loaded batch");
            }
            if(variant!=3) await Handover(s,variant==1?90:180);
            Applied(await Post(tenant,Path3+"/unload",Unload(s),"warehouse"),"Unload partially loaded B quota");
            int sold=variant==1?6:variant==2?12:0;
            int returned=variant==1?46:40;
            await CheckReturn(s,returned,returned*10,30-sold,variant==3?30:18);
            var after=await Read(s);
            Require(after.Balances.Truck,0m,"No stock left on trip");
            Require(after.Balances.TruckValue,0m,"No cost left on trip");
            Require(after.Balances.Main,100m-sold,"Opening units less sales");
            Require(after.Balances.MainValue,1000m-sold*10,"Opening stock value less COGS");
            Require(after.Balances.Cogs,sold*10m,"No reversal of COGS");
            Require(after.Extra.Allocated,40m,"Only remaining A holds consume MAIN allocation");
            Require(after.Extra.PoolOnHand-after.Extra.Allocated,60m-sold,"Unused B main capacity freed");
            Require(after.Balances.CreditRemaining,100m,"Unused financial grant remains source-bound");
        }
    }

    private async Task BlockReturnCase(string tenant)
    {
        var s=await Active(tenant,"D3-BLOCK");
        await Denied(s,Path3+"/unload",Unload(s),"warehouse","Cash remains unsettled");
        Guid offer=Applied(await Post(tenant,Path3+"/offer",Offer(s,200)),"Pending offer");
        await Denied(s,Path3+"/unload",Unload(s),"warehouse","Open handover cannot be bypassed");
        Applied(await Post(tenant,Path3+"/accept",Accept(s,offer,await Count(s,200)),"cashier"),"Settle custody");
        var peer=await Seed(tenant,"D3-BLOCK-DIFFERENT");
        await Denied(s,Path3+"/unload",Unload(s) with {TripId=Guid.NewGuid()},"warehouse","Wrong trip");
        var badRequest=JsonSerializer.SerializeToNode(Unload(s),Json)!.AsObject();
        badRequest["manifestId"]=peer.B20;
        await Denied(s,Path3+"/unload",badRequest,"warehouse","Client cannot inject wrong manifest");
        await using(var db=await Owner(tenant))
        {
            const string update="""
                UPDATE dbo.PhysicalStockBalance SET OnHandCostAmount=OnHandCostAmount+@delta
                  WHERE TenantId=@Tenant AND StockLocationId=@Truck AND SkuId=@Sku;
                """;
            Require(await db.ExecuteAsync(update,new {s.Tenant,s.Truck,s.Sku,delta=1}),1,"Guarded scratch-only stock drift fixture");
            try { await Denied(s,Path3+"/unload",Unload(s),"warehouse","Physical carrying value is unreconciled"); }
            finally { await db.ExecuteAsync(update,new {s.Tenant,s.Truck,s.Sku,delta=-1}); }
        }

        var input=Delivery(s,1);
        await using(var db=await Owner(tenant))
            await db.ExecuteAsync($"""
                CREATE TRIGGER dbo.D3FixtureBlockedA ON dbo.DeliveryStockIssue AFTER INSERT AS
                BEGIN
                    IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.SaleDelivery d
                        ON d.TenantId=i.TenantId AND d.Id=i.SaleDeliveryId
                        WHERE d.TripId='{s.Trip:D}' AND d.RightKind='A')
                      THROW 51930,'D.3 fixture blocks A posting after durable receipt',1;
                END;
                """);
        try
        {
            var fault=await Post(tenant,ADeliver,input);
            Require(fault.Http,500,"A post fault after captured receipt");
            Require((await Read(s)).Extra.PendingA,1,"Actual unposted C.4 evidence in SQL");
            var denied=await Denied(s,Path3+"/unload",Unload(s),"warehouse","Pending C.4 evidence protects stock");
            Require(denied.Reason,"C4_DELIVERY_PENDING","Return denied for exact unsettled A evidence");
        }
        finally
        {
            await using var db=await Owner(tenant);
            await db.ExecuteAsync("DROP TRIGGER dbo.D3FixtureBlockedA;");
        }
        Applied(await Post(tenant,ADeliver,input),"Recover original C.4 evidence");
        Require((await Read(s)).Extra.PendingA,0,"Recovered command is no longer pending");
    }

    private async Task RoundCase(string tenant)
    {
        var s=await Seed(tenant,"D3-ROUND",new(A:1,B:2,FirstB:1,SecondB:1,OpeningQuantity:3,OpeningValue:1));
        var first=Unload(s);
        Guid id=Applied(await Post(tenant,Path3+"/unload",first,"warehouse"),"Fractional carrying value return");
        await CheckReturn(s,3,1,2,0);
        await using var db=await App02c6Dev.ReadOnly(run,tenant);
        decimal[] values=(await db.QueryAsync<decimal>("""
            SELECT CostAmount FROM dbo.D3TripUnloadLine WHERE TenantId=@Tenant AND TripUnloadId=@id ORDER BY CostAmount;
            """,new {s.Tenant,id})).ToArray();
        Require(string.Join(",",values.Select(x=>FormattableString.Invariant($"{x:0.0000}"))),
            "0.3333,0.3333,0.3334","Last unit takes residual at four decimals");
        string before=await Fingerprint(tenant);
        var replay=await Post(tenant,Path3+"/unload",first,"warehouse");
        Require(replay.Source,(Guid?)id,"Exact same return after replay");
        Require(replay.Replayed,true,"Fractional return is replayed");
        Require(await Fingerprint(tenant),before,"Fractional cost and ledger remain unchanged");
    }

    private async Task CashRace(string tenant)
    {
        var s=await Active(tenant,"D3-CASH-RACE");
        var offered=await Task.WhenAll(Enumerable.Range(0,2).Select(_=>Post(tenant,Path3+"/offer",Offer(s,200))));
        Require(offered.Count(x=>x.Applied),1,"One offer wins the race");
        Require(offered.Count(x=>x.Denied),1,"One competing offer denied");
        Guid offer=offered.Single(x=>x.Applied).Source!.Value;
        string proof=await Count(s,200);
        var accepted=await Task.WhenAll(Enumerable.Range(0,2).Select(_=>Post(tenant,Path3+"/accept",Accept(s,offer,proof),"cashier")));
        Require(accepted.Count(x=>x.Applied),1,"One cashier acceptance wins");
        Require(accepted.Count(x=>x.Denied),1,"One competing cashier command denied");
        await CheckHandover(s,offer,accepted.Single(x=>x.Applied).Source!.Value,200,proof);
        var after=await Read(s);
        Require(after.Balances.Custody,0m,"Custody never overdrawn");
        Require(after.Balances.Fund,200m,"Exactly one fund receipt");
        Require(after.Balances.ArNet,100m,"Cashier race does not apply AR again");
    }

    private async Task ReturnRace(string tenant)
    {
        var s=await T5(tenant,"D3-RETURN-RACE");
        var replies=await Task.WhenAll(Enumerable.Range(0,2).Select(_=>Post(tenant,Path3+"/unload",Unload(s),"warehouse")));
        Require(replies.Count(x=>x.Applied),1,"Exactly one unload wins");
        Require(replies.Count(x=>x.Denied),1,"Second unload blocks");
        await CheckReturn(s,25,250,10,0);

        var mixed=await T5(tenant,"D3-SALE-RETURN-RACE");
        var b=await Sale(mixed,1,15,mixed.B10);
        var effects=await Task.WhenAll(Post(tenant,Path3+"/unload",Unload(mixed),"warehouse"),Post(tenant,BSale,b));
        var state=await Read(mixed);
        Require(effects.Count(x=>x.Applied),1,"Only one mixed race operation can commit");
        Require(effects.Count(x=>x.Denied),1,"The serial loser is refused");
        Require(state.Balances.ArA,375m,"A receivable preserved in either ordering");
        Require(state.Balances.ArNet,100m,"B receivable preserved in either ordering");
        Require(state.Balances.Fund,200m,"Fund already accepted is unchanged");
        if(effects[0].Applied)
            await CheckReturn(mixed,25,250,10,0);
        else
        {
            Require(state.Balances.Truck,24m,"B1 first leaves 24 EA");
            Require(state.Balances.TruckValue,240m,"B1 first leaves value240");
            Require(state.Balances.Cogs,460m,"B1 adds cost10");
            Require(state.Balances.B,9m,"B1 leaves nine B rights");
            Require(state.Balances.Custody,15m,"New verified cash prevents unload");
            Require(state.Extra.Returns,0,"No partial unload");
        }
    }

    private async Task<D2Scene> T5(string tenant,string code)
    {
        var s=await Active(tenant,code);
        Applied(await Post(tenant,ADeliver,Delivery(s)),"Source-bound A25");
        await Handover(s,200);
        return s;
    }
}
