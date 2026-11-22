using Dapper;
using FmcgDms.Api.Persistence;

namespace FmcgDms.Demo;

internal sealed record D2Scene(string Tenant, Guid Trip, Guid Customer, Guid ShipTo, Guid Selection, Guid? Outlet,
    Guid Sku, Guid Main, Guid Truck, Guid B10, Guid B20, Guid ALine, Guid BRight, Guid ARight, Guid Order,
    Guid? Grant, Guid? Approval);

internal sealed partial class App02d1Dev
{
    internal async Task<D2Scene> SeedD2(string tenant, string code, bool withOutlet = false, decimal grantCap = 100,
        bool bindD2 = true, D3StockFixture? stock = null, DateTimeOffset? fixtureTime = null)
    {
        stock ??= new();
        var scene = await Seed(tenant, code, grantCap, openingQuantity: stock.OpeningQuantity, openingValue: stock.OpeningValue, fixtureTime: fixtureTime);
        await using (var owner = await Write(tenant))
            await owner.ExecuteAsync("""
                INSERT dbo.C4DeliveryWriter (TenantId,TripId,ActorId) VALUES(@Tenant,@Trip,@actor);
                """, new { scene.Tenant, scene.Trip, actor = Actor(tenant, "sales") });

        Guid? outlet = null;
        if (withOutlet)
        {
            var saved = await Post(scene, "sales", C1 + "/outlets/save",
                new C1OutletInput(Guid.NewGuid(), scene.Customer, null, null, code + " Outlet"));
            outlet = saved.Body.GetProperty("id").GetGuid();
        }

        var address = new C1Address("VN", "HCM", "Khu DEV", "01 Duong DEV", code + " Nguoi nhan");
        var selected = await Post(scene, "sales", C1 + "/ship-to-selections",
            new C1SelectionInput(Guid.NewGuid(), scene.Customer, outlet, null, null, address));
        Guid selection = selected.Body.GetProperty("id").GetGuid();
        Guid order = (await Post(scene, "sales", C2 + "/drafts",
            new C2DraftInput(Guid.NewGuid(), scene.Customer, scene.Trip, selection, [new(scene.Sku, stock.A)]))).Source;
        Guid confirmation = (await Post(scene, "sales", C2 + "/confirm", new C2ConfirmInput(Guid.NewGuid(), order))).Source;
        await using var read = await App02c6Dev.ReadOnly(run, tenant);
        var frozen = await read.QuerySingleAsync<D2FrozenOrder>("""
            SELECT a.ShipToSnapshotId AS ShipTo,l.Id AS OrderLine
            FROM dbo.AOrder a JOIN dbo.AOrderLine l ON l.TenantId=a.TenantId AND l.AOrderId=a.Id
            WHERE a.TenantId=@tenant AND a.Id=@order;
            """, new { tenant, order });
        scene = scene with { ShipTo = frozen.ShipTo };
        if (!stock.DeferReservations)
            await Post(scene, "warehouse", C2 + "/reserve",
            new C2ReserveInput(Guid.NewGuid(), order, frozen.OrderLine, scene.Main, stock.A, confirmation));
        Guid aRight = await read.QuerySingleOrDefaultAsync<Guid>("""
            SELECT StockRightId FROM dbo.AReservation WHERE TenantId=@tenant AND AOrderLineId=@orderLine;
            """, new { tenant, orderLine = frozen.OrderLine });
        Guid bRight = Guid.Empty;
        if (!stock.DeferReservations)
        {
            await Post(scene, "warehouse", D1 + "/quota", Quota(scene, stock.B));
            bRight = await Right(scene);
            if (grantCap > 0)
                await Post(scene, "finance", D1 + "/credit", Credit(scene, grantCap));
        }
        if (stock.FirstB > 0)
            await Post(scene, "warehouse", D1 + "/load", Load(scene, bRight, stock.B, stock.FirstB));
        if (stock.SecondB > 0)
            await Post(scene, "warehouse", D1 + "/load", Load(scene, bRight, stock.B - stock.FirstB, stock.SecondB));
        if (stock.LoadA)
            await Post(scene, "warehouse", C3 + "/load", Load(scene, aRight, stock.A, stock.A));

        var lines = (await read.QueryAsync<D2ManifestLine>("""
            SELECT Id,StockRightId AS RightId,LoadedBaseQuantity AS Quantity FROM dbo.TripManifestLine
            WHERE TenantId=@Tenant AND TripId=@Trip AND IsC3=1;
            """, scene)).ToArray();
        Guid? grant = await read.QuerySingleOrDefaultAsync<Guid?>("""
            SELECT Id FROM dbo.BCreditGrant WHERE TenantId=@Tenant AND TripId=@Trip AND ShipToSnapshotId=@ShipTo;
            """, scene);
        Guid? approval = await read.QuerySingleOrDefaultAsync<Guid?>("""
            SELECT Id FROM dbo.CreditPolicyApproval WHERE TenantId=@Tenant AND TripId=@Trip;
            """, scene);
        var bLines = lines.Where(l => l.RightId == bRight).OrderBy(l => l.Quantity).ThenBy(l => l.Id).ToArray();
        var result = new D2Scene(tenant, scene.Trip, scene.Customer, scene.ShipTo, selection, outlet,
            scene.Sku, scene.Main, scene.Truck, bLines.ElementAtOrDefault(0)?.Id ?? Guid.Empty,
            bLines.ElementAtOrDefault(1)?.Id ?? Guid.Empty, lines.SingleOrDefault(l => l.RightId == aRight)?.Id ?? Guid.Empty,
            bRight, aRight, order, grant, approval);
        if (bindD2) await BindD2(result);
        await GrantRole(tenant, D2OtherSales(tenant), "sales");
        await GrantRole(tenant, Actor(tenant, "cash-reviewer"), "finance");
        return result;
    }

    private sealed record D2FrozenOrder(Guid ShipTo, Guid OrderLine);
    private sealed record D2ManifestLine(Guid Id, Guid RightId, decimal Quantity);

    internal async Task BindD2(D2Scene s)
    {
        await using var db = await Write(s.Tenant);
        await db.ExecuteAsync("""
            INSERT dbo.D2ShipToBinding(TenantId,CustomerId,ShipToSnapshotId,SelectionId)
            VALUES(@Tenant,@Customer,@ShipTo,@Selection);
            """, s);
    }

    private static string D2OtherSales(string tenant) => Actor(tenant, "other-sales");

    internal string D2Credential(string tenant, string role = "sales") => role == "other-sales"
        ? sign(tenant, "sales", D2OtherSales(tenant)) : Credential(tenant, role);

    internal async Task SetD2SalesAuthority(string tenant, bool isActive)
    {
        await using var db = await Write(tenant);
        Expect(await db.ExecuteAsync("""
            UPDATE dbo.App02ActorRoleGrant SET IsActive=@isActive,ChangedBy='d2-fixture',ChangedAtUtc=SYSUTCDATETIME()
            WHERE TenantId=@tenant AND ActorId=@actor AND RoleCode='sales';
            """, new { tenant, actor = Actor(tenant, "sales"), isActive }), 1, "D.2 Sales fixture authority");
    }

    internal async Task<string> SeedD2Cash(D2Scene s, decimal amount, string? kind = null, DateTime? verifiedAt = null)
    {
        if (kind is not (null or "pending" or "future" or "self"))
            throw new ArgumentOutOfRangeException(nameof(kind));
        string evidence = "D2-CASH-" + Key();
        DateTime? at = kind switch
        {
            "pending" => null,
            "future" => verifiedAt ?? StartTime.AddMinutes(1).UtcDateTime,
            _ => verifiedAt ?? StartTime.AddMinutes(-5).UtcDateTime
        };
        string? witness = kind switch
        {
            "pending" => null,
            "self" => Actor(s.Tenant, "sales"),
            _ => Actor(s.Tenant, "cash-reviewer")
        };
        await using var db = await Write(s.Tenant);
        await db.ExecuteAsync("""
            INSERT dbo.VerifiedCashEvidence
                (TenantId,EvidenceRef,TripId,CustomerId,ShipToSnapshotId,PaymentMethod,VerificationStatus,
                 VerifiedAmount,VerifiedBy,VerifiedAtUtc)
            VALUES(@Tenant,@evidence,@Trip,@Customer,@ShipTo,'CASH',@status,@amount,@witness,@at);
            """, new { s.Tenant,s.Trip,s.Customer,s.ShipTo,evidence,amount,witness,at,
                status = kind == "pending" ? "PENDING" : "VERIFIED" });
        return evidence;
    }

    internal async Task<string> SeedD2Bank(D2Scene s, decimal amount = 60)
    {
        string evidence = "D2-BANK-" + Key();
        await using var db = await Write(s.Tenant);
        await db.ExecuteAsync("""
            INSERT dbo.BLocalPendingBankEvidence(TenantId,EvidenceRef,TripId,CustomerId,ShipToSnapshotId,ReportedAmount)
            VALUES(@Tenant,@evidence,@Trip,@Customer,@ShipTo,@amount);
            """, new { s.Tenant,s.Trip,s.Customer,s.ShipTo,evidence,amount });
        return evidence;
    }

    internal async Task<Guid> ReserveD2CustomerCredit(D2Scene s, Guid originSourceId, decimal amount = 20)
    {
        Guid id = Guid.NewGuid();
        await using var db = await Write(s.Tenant);
        var changed = await db.ExecuteAsync("""
            INSERT dbo.BLocalCreditReservation(Id,TenantId,CreditSaleId,CustomerId,TripId,ShipToSnapshotId,Amount)
            SELECT @id,d.TenantId,d.Id,d.CustomerId,d.TripId,d.ShipToSnapshotId,@amount
            FROM dbo.SaleDelivery d JOIN dbo.BLocalSalePayment p ON p.TenantId=d.TenantId AND p.SaleDeliveryId=d.Id
            WHERE d.TenantId=@Tenant AND d.TripId=@Trip AND d.CustomerId=@Customer AND d.ShipToSnapshotId=@ShipTo
              AND d.BusinessSourceId=@originSourceId AND p.CreditIssued>0;
            """, new { s.Tenant,s.Trip,s.Customer,s.ShipTo,id,originSourceId,amount });
        Expect(changed, 1, "D.2 fixture reservation has exact existing overpayment source");
        return id;
    }


    internal async Task<Guid> SeedD2IndependentFundAndAdvance(D2Scene s)
    {
        Guid command = Guid.NewGuid(), source = Guid.NewGuid(), line = Guid.NewGuid();
        string key = Key();
        await using var db = await Write(s.Tenant);
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        await db.ExecuteAsync("""
            INSERT dbo.BusinessCommand (Id,TenantId,ProducerId,CommandId,PayloadHashVersion,PayloadHash,ActorId,
                Status,OutcomeCode,CompletedAtUtc)
            VALUES(@command,@Tenant,'d2-fixture',@key,1,HASHBYTES('SHA2_256',@key),@actor,'APPLIED','D2_OPENING',@now);
            INSERT dbo.BusinessSource(Id,TenantId,BusinessCommandId,SourceKind,SourceNumber,ActorId,TripId,OccurredAtUtc)
            VALUES(@source,@Tenant,@command,'D2_FIXTURE_OPENING',@key,@actor,@Trip,@now);
            INSERT dbo.BusinessSourceLine(Id,TenantId,BusinessSourceId,LineNumber)
            VALUES(@line,@Tenant,@source,1);
            INSERT dbo.LedgerEffect(Id,TenantId,BusinessSourceLineId,EffectKey,LedgerKind,AmountDelta)
            VALUES(NEWID(),@Tenant,@line,'D2-OPENING-FUND','FUND',25),
                  (NEWID(),@Tenant,@line,'D2-OPENING-ADVANCE','ADVANCE',10);
            """, new { s.Tenant,s.Trip,command,source,line,key,actor = Actor(s.Tenant,"finance"),now = StartTime.UtcDateTime }, tx);
        await tx.CommitAsync();
        return source;
    }

    internal async Task<Guid> AddD2Approval(D2Scene s, decimal cap = 100, DateTimeOffset? from = null,
        DateTimeOffset? until = null, string version = "2")
    {
        Guid id = Guid.NewGuid();
        await using var db = await Write(s.Tenant);
        await db.ExecuteAsync("""
            INSERT dbo.CreditPolicyApproval(Id,TenantId,TripId,CustomerId,PoolCode,CurrencyUnit,CaseNumber,PolicyVersion,
                ProposedBy,CheckedBy,Decision,Reason,EvidenceRef,ApprovedLimit,EffectiveFromUtc,EffectiveUntilUtc,ReviewedAtUtc)
            VALUES(@id,@Tenant,@Trip,@Customer,'B','SYNTHETIC_UNITLESS',@number,@version,
                @maker,@checker,'APPROVED','D2 alternate decision fixture','D2-v0.1',@cap,@from,@until,@reviewed);
            """, new { s.Tenant,s.Trip,s.Customer,id,number=Key(),version,cap,
                maker=Actor(s.Tenant,"credit_maker"),checker=Actor(s.Tenant,"credit_checker"),
                from=(from??From).UtcDateTime,until=(until??Until).UtcDateTime,reviewed=From.AddMinutes(-5).UtcDateTime });
        return id;
    }

    internal async Task<Guid> SeedD2Outlet(D2Scene s)
    {
        var scene = new Scene(s.Tenant,s.Trip,s.Customer,s.ShipTo,s.Sku,s.Main,s.Truck);
        var outcome = await Post(scene, "sales", C1 + "/outlets/save",
            new C1OutletInput(Guid.NewGuid(), s.Customer, null, null, "D2-alt-" + Key()));
        return outcome.Body.GetProperty("id").GetGuid();
    }
}
