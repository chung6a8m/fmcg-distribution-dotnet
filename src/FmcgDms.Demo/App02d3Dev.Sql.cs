using System.Security.Cryptography;
using Dapper;
using System.Text.Json;

namespace FmcgDms.Demo;

internal sealed partial class App02d3Dev
{
    private sealed record D3Extras(decimal PoolOnHand,decimal Allocated,string TripStatus,decimal Sold,int Offers,int Pending,
        int Acceptances,int Returns,int ReturnLines,int Releases,int PendingA,decimal ReservationApplied);
    private sealed record D3State(D2Snapshot Balances,D3Extras Extra);
    private sealed record Ledger(string Key,string Kind,decimal Qty,decimal Cost,Guid? Location);
    private sealed record Returned(Guid ManifestLine,Guid LoadLine,Guid Right,string Kind,Guid SourceLine,decimal Qty,decimal Cost);
    private sealed record Release(Guid Right,Guid SourceLine,decimal Qty,decimal NotLoaded);

    private async Task<D3State> Read(D2Scene s)
    {
        var b=await D2Sql.Read(run,s);
        await using var db=await App02c6Dev.ReadOnly(run,s.Tenant);
        var e=await db.QuerySingleAsync<D3Extras>("""
            SELECT p.OnHandBaseQuantity PoolOnHand,p.AllocatedBaseQuantity Allocated,t.Status TripStatus,
                COALESCE((SELECT SUM(i.BaseQuantity) FROM dbo.DeliveryStockIssue i JOIN dbo.SaleDelivery d
                      ON d.TenantId=i.TenantId AND d.Id=i.SaleDeliveryId WHERE d.TenantId=@Tenant AND d.TripId=@Trip),0) Sold,
                (SELECT COUNT(*) FROM dbo.CashHandoverOffer WHERE TenantId=@Tenant AND TripId=@Trip) Offers,
                (SELECT COUNT(*) FROM dbo.CashHandoverOffer o WHERE o.TenantId=@Tenant AND o.TripId=@Trip
                     AND NOT EXISTS(SELECT 1 FROM dbo.CashHandoverAcceptance a WHERE a.TenantId=o.TenantId AND a.OfferId=o.Id)) Pending,
                (SELECT COUNT(*) FROM dbo.CashHandoverAcceptance WHERE TenantId=@Tenant AND TripId=@Trip) Acceptances,
                (SELECT COUNT(*) FROM dbo.D3TripUnload WHERE TenantId=@Tenant AND TripId=@Trip) Returns,
                (SELECT COUNT(*) FROM dbo.D3TripUnloadLine WHERE TenantId=@Tenant AND TripId=@Trip) ReturnLines,
                (SELECT COUNT(*) FROM dbo.D3BQuotaRelease WHERE TenantId=@Tenant AND TripId=@Trip) Releases,
                (SELECT COUNT(*) FROM dbo.C4DeliveryEvidence e JOIN dbo.BusinessCommand c ON c.TenantId=e.TenantId
                     AND c.Id=e.Id WHERE e.TenantId=@Tenant AND e.TripId=@Trip AND c.Status='RECEIVED') PendingA,
                COALESCE((SELECT SUM(a.AppliedAmount) FROM dbo.ReceiptAllocation a JOIN dbo.CashReceipt r
                     ON r.TenantId=a.TenantId AND r.Id=a.CashReceiptId WHERE r.TenantId=@Tenant AND r.TripId=@Trip),0) ReservationApplied
            FROM dbo.Trip t JOIN dbo.StockPool p ON p.TenantId=t.TenantId
                AND p.StockLocationId=@Main AND p.SkuId=@Sku WHERE t.TenantId=@Tenant AND t.Id=@Trip;
            """,s);
        return new(b,e);
    }

    private async Task<string> Fingerprint(string tenant)
    {
        string old=await D2Sql.Fingerprint(run,tenant);
        await using var db=await App02c6Dev.ReadOnly(run,tenant);
        return await db.QuerySingleAsync<string>("""
            SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONCAT(@old,
              (SELECT * FROM dbo.Trip ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.C4DeliveryEvidence ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.CashHandoverOffer ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.CashHandoverAcceptance ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.CashierCountEvidence ORDER BY EvidenceRef FOR JSON PATH, INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.TripUnload ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.TripUnloadLine ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.D3TripUnload ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.D3TripUnloadLine ORDER BY SourceLineId FOR JSON PATH, INCLUDE_NULL_VALUES),
              (SELECT * FROM dbo.D3BQuotaRelease ORDER BY SourceLineId FOR JSON PATH, INCLUDE_NULL_VALUES))),2);
            """,new {old});
    }

    private void Ac03(string tenant,string phase,string step)
    {
        var s=phases[(tenant,phase)];
        var b=s.Balances;
        JsonElement[] expected=oracle.GetProperty("steps").EnumerateArray()
            .Single(row=>row[0].GetString()==step).EnumerateArray().ToArray();
        string[] columns=oracle.GetProperty("cols").EnumerateArray().Select(x=>x.GetString()!).ToArray();
        Require(step,expected[0].GetString(),"AC-03 step");
        decimal[] actual=[b.Main,b.Truck,s.Extra.Sold,b.MainValue,b.TruckValue,b.Cogs,b.A,b.B,b.ArA,b.ArNet,b.Custody,b.Fund];
        for(int i=0;i<actual.Length;i++) Require(actual[i],expected[i+1].GetDecimal(),$"AC-03 {step} {columns[i+1]}");
        Require(b.Main+b.Truck+s.Extra.Sold,100m,"Opening stock conserved");
        Require(b.MainValue+b.TruckValue+b.Cogs,1000m,"Opening value conserved");
        Require(s.Extra.PoolOnHand,b.Main,"Warehouse pool and physical quantity match");
        decimal allocated=phase=="t0"?70:phase=="t6"?15:0;
        Require(s.Extra.Allocated,allocated,"Pool allocation");
        Require(s.Extra.PoolOnHand-s.Extra.Allocated,phase=="t6"?40m:30m,"Unallocated warehouse stock");
        Require(b.Cash,b.Custody+b.Fund,"Confirmed cash equals custody plus fund");
        Require(b.CreditConsumed,b.ArNet,"B grant used equals open B AR for this fixture");
        Require(b.CreditReleased,0m,"No financial grant release");
        Require(b.InvoiceEligible,0,"No invoice-eligible delivery");
        Require(b.Outbox,0,"No outbound invoice/accounting");
    }

    private async Task<Ledger[]> Effects(string tenant,Guid sourceId)
    {
        await using var db=await App02c6Dev.ReadOnly(run,tenant);
        return (await db.QueryAsync<Ledger>("""
            SELECT e.EffectKey [Key],e.LedgerKind Kind,e.QuantityDelta Qty,e.AmountDelta Cost,e.StockLocationId Location
            FROM dbo.LedgerEffect e JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId AND l.Id=e.BusinessSourceLineId
            WHERE l.TenantId=@tenant AND l.BusinessSourceId=@sourceId ORDER BY e.EffectKey;
            """,new {tenant,sourceId})).ToArray();
    }

    private static string LedgerText(IEnumerable<Ledger> effects)=>string.Join(",",effects.Select(e=>
        FormattableString.Invariant($"{e.Key}|{e.Kind}|{e.Qty:0.0000}|{e.Cost:0.0000}|{e.Location}")).Order());

    private async Task<Guid> GetSource(D2Scene s,string kind)
    {
        await using var db=await App02c6Dev.ReadOnly(run,s.Tenant);
        return await db.QuerySingleAsync<Guid>("SELECT Id FROM dbo.BusinessSource WHERE TenantId=@Tenant AND TripId=@Trip AND SourceKind=@kind;",
            new {s.Tenant,s.Trip,kind});
    }

    private async Task CheckHandover(D2Scene s,Guid offer,Guid acceptance,decimal cash,string proof)
    {
        Require((await Effects(s.Tenant,offer)).Length,0,"Offer has zero ledger effects");
        Require(LedgerText(await Effects(s.Tenant,acceptance)),
            LedgerText([new("CUSTODY-OUT","CUSTODY",0,-cash,null),new("FUND-IN","FUND",0,cash,null)]),
            "Acceptance has precisely the signed custody out and fund in");
        await using var db=await App02c6Dev.ReadOnly(run,s.Tenant);
        int joined=await db.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.CashHandoverAcceptance a
            JOIN dbo.CashHandoverOffer o ON o.TenantId=a.TenantId AND o.Id=a.OfferId AND o.TripId=a.TripId
            JOIN dbo.CashierCountEvidence w ON w.TenantId=a.TenantId AND w.EvidenceRef=a.EvidenceRef AND w.TripId=a.TripId
            WHERE a.TenantId=@Tenant AND a.TripId=@Trip AND a.Id=@acceptance AND o.Id=@offer
                AND a.AcceptedBy=w.CountedBy AND a.AcceptedBy<>o.OfferedBy AND w.CountedAmount=@cash
                AND o.OfferedAmount=@cash AND a.AcceptedAmount=@cash AND w.EvidenceRef=@proof;
            """,new {s.Tenant,s.Trip,acceptance,offer,cash,proof});
        Require(joined,1,"Exactly one immutable offer/witness/actor acceptance lineage");
    }

    private async Task CheckReturn(D2Scene s,decimal expectedQuantity,decimal expectedValue,decimal bReleased,decimal neverLoaded)
    {
        await using var db=await App02c6Dev.ReadOnly(run,s.Tenant);
        var id=await db.QuerySingleAsync<Guid>("SELECT Id FROM dbo.D3TripUnload WHERE TenantId=@Tenant AND TripId=@Trip;",s);
        var rows=(await db.QueryAsync<Returned>("""
            SELECT r.TripManifestLineId ManifestLine,r.LoadSourceLineId LoadLine,r.StockRightId [Right],
                r.RightKind Kind,r.SourceLineId SourceLine,r.BaseQuantity Qty,r.CostAmount Cost
            FROM dbo.D3TripUnloadLine r JOIN dbo.TripManifestLine m ON m.TenantId=r.TenantId
                AND m.Id=r.TripManifestLineId AND m.LoadSourceLineId=r.LoadSourceLineId
                AND m.StockRightId=r.StockRightId AND m.RightKind=r.RightKind AND m.TripId=r.TripId
            WHERE r.TenantId=@Tenant AND r.TripId=@Trip AND r.TripUnloadId=@id;
            """,new {s.Tenant,s.Trip,id})).ToArray();
        Require(rows.Sum(x=>x.Qty),expectedQuantity,"Physical returns linked to exact load lines");
        Require(rows.Sum(x=>x.Cost),expectedValue,"Returned warehouse carrying value");
        foreach(var row in rows)
        {
            var effects=await db.QueryAsync<Ledger>("""
                SELECT EffectKey [Key],LedgerKind Kind,QuantityDelta Qty,AmountDelta Cost,StockLocationId Location
                FROM dbo.LedgerEffect WHERE TenantId=@Tenant AND BusinessSourceLineId=@SourceLine;
                """,new {s.Tenant,row.SourceLine});
            Require(LedgerText(effects),LedgerText([new("VEHICLE-OUT","PHYSICAL_STOCK",-row.Qty,-row.Cost,s.Truck),
                new("WAREHOUSE-IN","PHYSICAL_STOCK",row.Qty,row.Cost,s.Main)]),$"Return {row.Kind} paired signed legs");
        }
        var release=await db.QuerySingleAsync<Release>("""
            SELECT StockRightId [Right],SourceLineId SourceLine,ReleasedBaseQuantity Qty,NeverLoadedBaseQuantity NotLoaded
            FROM dbo.D3BQuotaRelease WHERE TenantId=@Tenant AND TripId=@Trip AND TripUnloadId=@id;
            """,new {s.Tenant,s.Trip,id});
        Require(release.Right,s.BRight,"One release bound to original B right");
        Require(release.Qty,bReleased,"Unused quota released once");
        Require(release.NotLoaded,neverLoaded,"Never-loaded MAIN allocation released once");
        var leg=await db.QueryAsync<Ledger>("""
            SELECT EffectKey [Key],LedgerKind Kind,QuantityDelta Qty,AmountDelta Cost,StockLocationId Location
            FROM dbo.LedgerEffect WHERE TenantId=@Tenant AND BusinessSourceLineId=@SourceLine;
            """,new {s.Tenant,release.SourceLine});
        Require(LedgerText(leg),LedgerText([new("RIGHT-B-RELEASE","RIGHT_B_RELEASED",-bReleased,0,null)]),
            "Release source has no invented physical movement");
        Require((await Effects(s.Tenant,id)).Length,rows.Length*2+1,"No extra ledger effects from unload");
    }

    private static string Hash(string p)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
}
