using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using FmcgDms.Api.Persistence;

namespace FmcgDms.Demo;

internal sealed partial class App02d3Dev
{
    private async Task FaultCase(string tenant)
    {
        var cash=await Active(tenant,"D3-FAULT-CASH");
        Guid offer=Applied(await Post(tenant,Path3+"/offer",Offer(cash,200)),"Fault fixture offer");
        string proof=await Count(cash,200);
        var accept=Accept(cash,offer,proof);
        await Fault(cash,Path3+"/accept",accept,"cashier",accept.ProducerId,accept.CommandId,"FUND-IN",51931,
            "EXISTS(SELECT 1 FROM dbo.LedgerEffect firstLeg WHERE firstLeg.TenantId=i.TenantId " +
            "AND firstLeg.BusinessSourceLineId=i.BusinessSourceLineId AND firstLeg.EffectKey='CUSTODY-OUT')");
        Guid accepted=Applied(await Post(tenant,Path3+"/accept",accept,"cashier"),"Same accept command succeeds after rollback");
        await CheckHandover(cash,offer,accepted,200,proof);

        var trip=await T5(tenant,"D3-FAULT-RETURN");
        var unload=Unload(trip);
        await Fault(trip,Path3+"/unload",unload,"warehouse",unload.ProducerId,unload.CommandId,"VEHICLE-OUT",51932,
            "(SELECT COUNT(*) FROM dbo.LedgerEffect firstPair JOIN dbo.BusinessSourceLine x " +
            "ON x.TenantId=firstPair.TenantId AND x.Id=firstPair.BusinessSourceLineId " +
            "WHERE x.TenantId=i.TenantId AND x.BusinessSourceId=l.BusinessSourceId " +
            "AND firstPair.EffectKey='WAREHOUSE-IN')=1",secondOccurrence:true);
        Applied(await Post(tenant,Path3+"/unload",unload,"warehouse"),"Same unload command succeeds after rollback");
        await CheckReturn(trip,25,250,10,0);
    }

    private async Task Fault(D2Scene s,string path,object input,string role,string producer,string command,
        string key,int error,string prerequisite,bool secondOccurrence=false)
    {
        string before=await Fingerprint(s.Tenant);
        await using var db=await Owner(s.Tenant);
        // Scope comes from the owned fixture. The second vehicle-out is reached only after a first complete pair.
        string occurrence=secondOccurrence
            ? """
                AND 2=(SELECT COUNT(*) FROM dbo.LedgerEffect prior JOIN dbo.BusinessSourceLine p
                    ON p.TenantId=prior.TenantId AND p.Id=prior.BusinessSourceLineId
                    WHERE p.BusinessSourceId=l.BusinessSourceId AND prior.TenantId=i.TenantId AND prior.EffectKey='VEHICLE-OUT')
                """
            : "";
        await db.ExecuteAsync($"""
            CREATE TRIGGER dbo.D3FixtureFault ON dbo.LedgerEffect AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;
                IF NOT EXISTS(SELECT 1 FROM inserted i JOIN dbo.BusinessSourceLine l
                    ON l.TenantId=i.TenantId AND l.Id=i.BusinessSourceLineId
                    JOIN dbo.BusinessSource s ON s.TenantId=l.TenantId AND s.Id=l.BusinessSourceId
                    WHERE s.TripId='{s.Trip:D}' AND i.EffectKey='{key}' {occurrence}) RETURN;
                IF NOT EXISTS(SELECT 1 FROM inserted i JOIN dbo.BusinessSourceLine l
                    ON l.TenantId=i.TenantId AND l.Id=i.BusinessSourceLineId
                    JOIN dbo.BusinessSource s ON s.TenantId=l.TenantId AND s.Id=l.BusinessSourceId
                    WHERE s.TripId='{s.Trip:D}' AND i.EffectKey='{key}' {occurrence} AND ({prerequisite}))
                       THROW 51939,'D.3 fault ordering prerequisite missing',1;
                THROW {error},'D.3 owned transaction fault',1;
            END;
            """);
        try
        {
            var failure=await Post(s.Tenant,path,input,role);
            Require(failure.Http,500,$"Fault for {key} interrupts real transaction");
            Require(await D2Sql.CommandRows(run,s,producer,command),0,"Command rollback");
            Require(await Fingerprint(s.Tenant),before,"Fault preserves source, ledger, physical and status");
        }
        finally
        {
            await run.Verify(s.Tenant);
            await db.ExecuteAsync("DROP TRIGGER dbo.D3FixtureFault;");
        }
    }

    private async Task RecoveryCase(string tenant)
    {
        var s=await Active(tenant,"D3-ACK");
        Applied(await Post(tenant,ADeliver,Delivery(s)),"Prepared A25 for lost ACK journey");
        var offer=Offer(s,200);
        Guid offered=await LoseAndRecover(s,"offer","sales",offer,offer.ProducerId,offer.CommandId,
            offer with {OfferedAmount=199},offer with {CommandId=Key()});
        string proof=await Count(s,200);
        var accept=Accept(s,offered,proof);
        Guid accepted=await LoseAndRecover(s,"accept","cashier",accept,accept.ProducerId,accept.CommandId,
            accept with {CountEvidenceRef="D3-changed-count"},accept with {CommandId=Key()});
        await CheckHandover(s,offered,accepted,200,proof);
        var unload=Unload(s);
        await LoseAndRecover(s,"unload","warehouse",unload,unload.ProducerId,unload.CommandId,
            unload with {SourceNumber=Key()},unload with {CommandId=Key()});
        await CheckReturn(s,25,250,10,0);
    }

    private async Task<Guid> LoseAndRecover(D2Scene s,string operation,string role,object input,
        string producer,string command,object conflict,object duplicate)
    {
        var builder=WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0));
        await using var proxy=builder.Build();
        var upstream=new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
        string path=$"{Path3}/{operation}";
        proxy.MapPost("/drop-ack",async context=>
        {
            try { upstream.TrySetResult(await Post(s.Tenant,path,input,role)); }
            catch(Exception ex) { upstream.TrySetException(ex); }
            finally { context.Abort(); }
        });
        await proxy.StartAsync(ct);
        bool lost=false;
        try { using var ignored=await client.PostAsJsonAsync(proxy.Urls.Single()+"/drop-ack",new {},ct); }
        catch(HttpRequestException) { lost=true; }
        Require(lost,true,$"{operation} response was lost");
        Guid expected=Applied(await upstream.Task.WaitAsync(ct),$"{operation} committed before ACK abort");
        string snapshot=await Fingerprint(s.Tenant);
        await restart("on");
        var lookup=await Send(s.Tenant,HttpMethod.Get,$"{Path3}/trips/{s.Trip}/commands/{producer}/{command}",role:role);
        Require(lookup.Status,"APPLIED",$"{operation} durable outcome survives restart");
        Require(lookup.Source,(Guid?)expected,$"{operation} lookup source");
        var replay=await Post(s.Tenant,path,input,role);
        Require(replay.Replayed,true,$"{operation} replays");
        Require(replay.Source,(Guid?)expected,$"{operation} replay source");
        Require((await Denied(s,path,conflict,role,"Same identity with changed payload")).Http,409,"Conflict reported");
        await Denied(s,path,duplicate,role,"New identity cannot reuse source");
        Require(await Fingerprint(s.Tenant),snapshot,$"{operation} recovery adds no effect");
        return expected;
    }

    private async Task TenantCase(string tenant,string peer)
    {
        var s=await Active(tenant,$"D3-PEER-OWN-{Slot(tenant)}");
        var outside=await Active(peer,$"D3-PEER-FOREIGN-{Slot(tenant)}");
        var remote=Offer(outside,200);
        Guid foreignOffer=Applied(await Post(peer,Path3+"/offer",remote),"Peer offer exists only on peer DB");
        Guid ownOffer=Applied(await Post(tenant,Path3+"/offer",Offer(s,200)),"Local offer");
        string foreignProof=await Count(outside,200);
        string beforePeer=await Fingerprint(peer);
        await Denied(s,Path3+"/offer",Offer(s,200) with {TripId=outside.Trip},"sales","Foreign trip submitted as own");
        await Denied(s,Path3+"/accept",Accept(s,foreignOffer,foreignProof),"cashier","Foreign offer ID");
        await Denied(s,Path3+"/accept",Accept(s,ownOffer,foreignProof),"cashier","Foreign count reference");
        await Denied(s,Path3+"/unload",Unload(s) with {TripId=outside.Trip},"warehouse","Foreign trip unload");
        var lookup=await Send(tenant,HttpMethod.Get,
            $"{Path3}/trips/{outside.Trip}/commands/{remote.ProducerId}/{remote.CommandId}");
        Require(lookup.Http is 403 or 404,true,"Peer command cannot be read");
        Require(lookup.Source,(Guid?)null,"Peer source is not disclosed");
        Require(await Fingerprint(peer),beforePeer,"Peer DB protected rows are unchanged");
    }

    private sealed record BrowserAuthority(string Role,int OfflinePostCount,bool QueuedAfterOffline,int RevokedSubmitStatus);
    private sealed record BrowserAuthorityReport(string Slot,Guid Trip,BrowserAuthority[] Checks);
    private sealed record BrowserJourney(string Slot,Guid Trip,string[] Phases,BrowserObservation[] Observations);
    private sealed record BrowserObservation(string Endpoint,int HttpStatus,string Status,Guid SourceId);

    private async Task AuthorityCase(string tenant,int browserExit)
    {
        var s=await Locate(canonical[tenant].Scene);
        Require(browserExit,0,"Two-tenant browser exits cleanly");
        var browser=JsonSerializer.Deserialize<BrowserAuthorityReport>(await File.ReadAllTextAsync(
            Path.Combine(Checkpoints,$"{Slot(tenant)}-D3-17.json"),ct),Json)!;
        Require(browser.Trip,s.Trip,"Browser authority checks match the SQL trip");
        Require(browser.Checks.Length,3,"Sales, Cashier and Warehouse are checked");
        foreach(var role in new[]{"sales","cashier","warehouse"})
        {
            var check=browser.Checks.Single(x=>string.Equals(x.Role,role,StringComparison.OrdinalIgnoreCase));
            Require(check.OfflinePostCount,0,$"{role} did not enqueue an offline POST");
            Require(check.QueuedAfterOffline,false,$"{role} has no offline write state");
            Require(check.RevokedSubmitStatus,403,$"{role} revoked at server before write");
        }
        var journey=JsonSerializer.Deserialize<BrowserJourney>(await File.ReadAllTextAsync(
            Path.Combine(Checkpoints,$"{Slot(tenant)}-journey.json"),ct),Json)!;
        Require(journey.Trip,s.Trip,"Journey matches same SQL trip");
        Require(journey.Observations.Length,11,"Eleven real browser commands");
        await using(var reader=await App02c6Dev.ReadOnly(run,tenant))
        foreach(var item in journey.Observations)
        {
            Require(item.Status,"APPLIED","Actual browser command "+item.Endpoint);
            Require(item.HttpStatus,200,"Browser command HTTP response "+item.Endpoint);
            Require(await reader.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM dbo.BusinessSource WHERE TenantId=@Tenant AND TripId=@Trip AND Id=@SourceId;
                """,new {s.Tenant,s.Trip,item.SourceId}),1,"Browser source correlates to exact trip in SQL");
        }
        var lateSale=await Sale(s,1,15,s.B10);
        string before=await Fingerprint(tenant);
        var load=new C3LoadInput(Guid.NewGuid(),s.Trip,[new(s.BRight,0,1)]);
        await Denied(s,"/api/synthetic/app02d1/load",load,"warehouse","New load after reconciliation");
        await Denied(s,BSale,lateSale,"sales","New B sale after reconciliation");
        var delivery=Delivery(s,1);
        await Denied(s,ADeliver,delivery,"sales","Fresh A evidence may not be captured after reconciliation");
        await Denied(s,Path3+"/unload",Unload(s),"warehouse","Second unload identity blocked");
        Require((await Read(s)).Extra.PendingA,0,"No new pending A evidence after terminal fence");
        Require(await Fingerprint(tenant),before,"Terminal actions leave the whole protected state unchanged");
        var other=await Active(tenant,"D3-ROLE-AUTHORITY");
        await Denied(other,Path3+"/offer",Offer(other,200),"other-sales","Sales role without trip assignment");
        await Denied(other,Path3+"/unload",Unload(other),"other-warehouse","Warehouse role without trip assignment");
        var offer=Applied(await Post(tenant,Path3+"/offer",Offer(other,200)),"Offer for cashier assignment check");
        await Denied(other,Path3+"/accept",Accept(other,offer,await Count(other,200)),"other-cashier","Cashier role without trip assignment");
    }
}
