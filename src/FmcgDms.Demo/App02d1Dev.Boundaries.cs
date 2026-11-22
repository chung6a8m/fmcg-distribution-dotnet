using System.Net;
using Dapper;
using FmcgDms.Api.Persistence;

namespace FmcgDms.Demo;

internal sealed partial class App02d1Dev
{
    private async Task Recovery(string tenant,Func<bool,Task> restart)
    {
        var s=await Seed(tenant,"D1-RESTART");
        var quota=Quota(s,30);
        var credit=Credit(s,100);
        // Read through the API proxy only after discarding the originating response bodies.
        await LoseAck(s,"warehouse",D1+"/quota",quota);
        await LoseAck(s,"finance",D1+"/credit",credit);
        Guid right=await Right(s);
        var load=Load(s,right,30,10);
        await LoseAck(s,"warehouse",D1+"/load",load);
        string before=await Fingerprint(tenant);
        await restart(false);
        foreach(var (role,scope,id,command,operation) in new (string,string,string,object,string)[]
        {
            ("warehouse","grants",quota.CommandId,quota,"quota"),
            ("finance","grants",credit.CommandId,credit,"credit"),
            ("warehouse","commands",load.CommandId.ToString(),load,"load")
        })
        {
            var recover=await Send(s,role,HttpMethod.Get,$"{D1}/trips/{s.Trip}/{scope}/{id}");
            Expect(recover.Status,HttpStatusCode.OK,$"Lookup {operation} after restart");
            var replay=await Post(s,role,D1+"/"+operation,command);
            Expect(replay.Replayed,true,$"Stable {operation} replay");
            Expect(replay.Source,recover.Source,$"Same {operation} source");
        }
        Expect((await Send(s,"warehouse",HttpMethod.Post,D1+"/quota",quota with {BaseQuantity=29})).Status,HttpStatusCode.Conflict,"Quota changed payload");
        Expect((await Send(s,"finance",HttpMethod.Post,D1+"/credit",credit with {Amount=99})).Status,HttpStatusCode.Conflict,"Credit changed payload");
        Expect((await Send(s,"warehouse",HttpMethod.Post,D1+"/load",Load(s,right,30,9) with {CommandId=load.CommandId})).Status,
            HttpStatusCode.Conflict,"Load changed payload");
        await Denied(s,"warehouse",D1+"/quota",quota with {CommandId=Key()});
        await Denied(s,"finance",D1+"/credit",credit with {CommandId=Key()});
        await Denied(s,"other-warehouse",D1+"/load",load);
        Expect(await Fingerprint(tenant),before,"No repeat quota, credit or transfer SQL effect");
    }

    private async Task LoseAck(Scene s,string role,string path,object body)
    {
        var builder=WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0));
        await using var proxy=builder.Build();
        var upstream=new TaskCompletionSource<HttpStatusCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.MapPost("/drop-ack",async context=>
        {
            try
            {
                var reply=await Send(s,role,HttpMethod.Post,path,body);
                upstream.TrySetResult(reply.Status);
            }
            catch(Exception ex) { upstream.TrySetException(ex); }
            finally { context.Abort(); }
        });
        await proxy.StartAsync(ct);
        bool lost=false;
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,proxy.Urls.Single()+"/drop-ack");
            using var response=await client.SendAsync(request,ct);
        }
        catch(HttpRequestException) { lost=true; }
        Expect(lost,true,path+" caller lost the ACK");
        Expect(await upstream.Task.WaitAsync(ct),HttpStatusCode.OK,path+" committed before ACK drop");
    }

    private async Task TenantDenial(Scene own,Scene foreign)
    {
        string ownBefore=await Fingerprint(own.Tenant);
        string peerBefore=await Fingerprint(foreign.Tenant);
        var catalog=await Send(own,"warehouse",HttpMethod.Get,D1+"/catalog",forged:true);
        Expect(catalog.Status,HttpStatusCode.OK,"Current tenant catalog");
        Expect(catalog.Body.GetRawText().Contains(foreign.Trip.ToString(),StringComparison.OrdinalIgnoreCase),false,"Peer trip excluded");
        var peerRead=await Send(own,"warehouse",HttpMethod.Get,$"{D1}/trips/{foreign.Trip}/load-rights");
        Expect((int)peerRead.Status is 403 or 404 || (peerRead.Status==HttpStatusCode.OK &&
            peerRead.Body.ValueKind==System.Text.Json.JsonValueKind.Array && peerRead.Body.GetArrayLength()==0),true,"No peer rights exposed");
        var foreignRight=await Right(foreign);
        foreach(var (role,path,body) in new (string,string,object)[]
        {
            ("warehouse","quota",Quota(foreign,1)),
            ("finance","credit",Credit(foreign,1)),
            ("warehouse","load",Load(foreign,foreignRight,30,1))
        })
        {
            var denied=await Send(own,role,HttpMethod.Post,D1+"/"+path,body,forged:true);
            Expect((int)denied.Status>=400 && (int)denied.Status<500,true,$"Peer {path} denied");
        }
        await Denied(own,"sales",D1+"/quota",Quota(own,1));
        await Denied(own,"other-warehouse",D1+"/quota",Quota(own,1));
        await Denied(own,"warehouse",D1+"/credit",Credit(own,1));
        await using var db=await Write(own.Tenant);
        const string roleSql="""
            UPDATE dbo.App02ActorRoleGrant SET IsActive=@active,ChangedBy='d1-fixture',ChangedAtUtc=SYSUTCDATETIME()
            WHERE TenantId=@Tenant AND ActorId=@actor AND RoleCode='warehouse'
            """;
        await db.ExecuteAsync(roleSql,new {own.Tenant,actor=Actor(own.Tenant,"warehouse"),active=false});
        try { await Denied(own,"warehouse",D1+"/quota",Quota(own,1)); }
        finally { await db.ExecuteAsync(roleSql,new {own.Tenant,actor=Actor(own.Tenant,"warehouse"),active=true}); }
        Expect(await Fingerprint(own.Tenant),ownBefore,"Local protected business rows unchanged");
        Expect(await Fingerprint(foreign.Tenant),peerBefore,"Peer protected business rows unchanged");
    }

    private async Task LegacyFence(string tenant,Func<bool,Task> restart)
    {
        var s=await Seed(tenant,"D1-LEGACY");
        await Post(s,"warehouse",D1+"/quota",Quota(s,30));
        Guid right=await Right(s);
        await Post(s,"warehouse",D1+"/load",Load(s,right,30,10));
        await using var read=await App02c6Dev.ReadOnly(run,tenant);
        var manifest=await read.QuerySingleAsync<(Guid Id,Guid Line)>("""
            SELECT TripManifestId AS Id,Id AS Line FROM dbo.TripManifestLine WHERE StockRightId=@right
            """,new {right});
        Expect(await read.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.TripManifest WHERE Id=@Id AND IsC3=1",new {manifest.Id}),1,"D1 uses modern model");
        var legacyTrip=await Seed(tenant,"D1-OLD-MODEL");
        await Post(legacyTrip,"warehouse",D1+"/quota",Quota(legacyTrip,30));
        var legacyRight=await Right(legacyTrip);
        Guid legacyManifest=Guid.NewGuid();
        await using(var db=await Write(tenant))
            await db.ExecuteAsync("""
                INSERT dbo.TripManifest(Id,TenantId,ManifestNumber,TripId,WarehouseLocationId,VehicleLocationId)
                    VALUES(@legacyManifest,@Tenant,'D1-LEGACY-DRAFT',@Trip,@Main,@Truck)
                """,new {legacyManifest,legacyTrip.Tenant,legacyTrip.Trip,legacyTrip.Main,legacyTrip.Truck});
        await Denied(legacyTrip,"warehouse",D1+"/load",Load(legacyTrip,legacyRight,30,10));
        try
        {
            await restart(true);
            string before=await Fingerprint(tenant);
            var sale=await Send(s,"sales",HttpMethod.Post,"/api/synthetic/app01c/commands/post-b-sale-delivery-receipt",
                new PostBSaleDeliveryReceiptCommand(Actor(tenant,"sales"),Key(),Key(),s.Trip,s.Customer,s.ShipTo,
                    manifest.Line,s.Sku,1,15,15,"d1-dev-fence-probe"));
            Expect(sale.Reason,"B_LOADED_RIGHT_NOT_FOUND","Legacy sale rejects a modern manifest source");
            var unload=await Send(s,"warehouse",HttpMethod.Post,"/api/synthetic/app01d/commands/unload-trip-remainder",
                new UnloadTripRemainderCommand(Actor(tenant,"warehouse"),Key(),Key(),s.Trip,manifest.Id));
            Expect(unload.Reason,"LOADED_MANIFEST_NOT_ELIGIBLE","Legacy unload rejects a modern manifest");
            Expect(await Fingerprint(tenant),before,"Legacy fences preserve protected SQL");
        }
        finally { await restart(false); }
        Expect((await SnapshotOf(s)).BLoaded,10m,"Modern custody remains intact");
        Expect(await read.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.TripManifest WHERE Id=@legacyManifest AND IsC3=0",
            new {legacyManifest}),1,"Legacy model marker unchanged");
    }
}
