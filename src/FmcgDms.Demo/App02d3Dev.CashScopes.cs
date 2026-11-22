using System.Net.Http.Headers;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Demo;

internal sealed partial class App02d3Dev
{
    private sealed record CashCheckpoint(decimal Ar, decimal Credit, decimal Custody, decimal Fund,
        decimal Allocated, decimal Advance);

    private async Task CashScopesCase(string tenant)
    {
        await ObserveCashScope(tenant, "AC-10", new(120000, 60000, 80000, 0, 20000, 10000),
            oldCustody: 60000, newCash: 20000, [20000m]);
        await ObserveCashScope(tenant, "AC-21", new(105000, 25000, 60000, 0, 35000, 10000),
            oldCustody: 0, newCash: 60000, [20000m, 15000m]);
    }

    private async Task ObserveCashScope(string tenant, string scope, CashCheckpoint opening,
        decimal oldCustody, decimal newCash, decimal[] allocations)
    {
        var scene = await Seed(tenant, "D3-18-" + scope,
            new D3StockFixture(FirstB: 0, SecondB: 0, LoadA: false), price: false);
        Guid item = await SeedCashScope(scene, oldCustody, newCash, allocations);
        Require(await ReadCashScope(scene, item), opening, $"{scope}: auditable post-allocation opening");
        await CheckCashReport(scene, opening, 0, scope + " before offer");

        const decimal amount = 50000m;
        Guid offer = Applied(await Post(tenant, Path3 + "/offer", Offer(scene, amount)), scope + " offer");
        Require(await ReadCashScope(scene, item), opening, $"{scope}: OFFERED preserves every balance");
        await CheckCashReport(scene, opening, amount, scope + " with pending offer");

        string proof = await Count(scene, amount);
        Guid acceptance = Applied(await Post(tenant, Path3 + "/accept", Accept(scene, offer, proof), "cashier"),
            scope + " independent acceptance");
        CashCheckpoint expected = opening with { Custody = opening.Custody - amount, Fund = opening.Fund + amount };
        Require(await ReadCashScope(scene, item), expected, $"{scope}: ACCEPTED moves custody to fund only");
        await CheckCashReport(scene, expected, 0, scope + " after acceptance");
        await CheckHandover(scene, offer, acceptance, amount, proof);
    }

    private async Task<Guid> SeedCashScope(D2Scene scene, decimal oldCustody, decimal newCash,
        decimal[] allocations)
    {
        // This isolated financial checkpoint uses the same legacy receipt carrier as
        // AC-21/C.6. It deliberately never enters the modern stock-return path.
        Guid receiptManifest = Guid.NewGuid(), receiptManifestLine = Guid.NewGuid();
        string? oldWitness = oldCustody > 0 ? await d1.SeedD2Cash(scene, oldCustody) : null;
        string currentWitness = await d1.SeedD2Cash(scene, newCash);
        await using var db = await Owner(scene.Tenant);
        await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(ct);
        DateTime at = App02d1Dev.StartTime.UtcDateTime;
        Guid loadSource = Guid.NewGuid(), loadLine = Guid.NewGuid();
        await AddCashScopeSource(db, tx, scene, loadSource, loadLine, "MANIFEST_LOAD", 0, at, withSku: true);
        await db.ExecuteAsync("""
            INSERT dbo.TripManifest(Id,TenantId,ManifestNumber,TripId,WarehouseLocationId,VehicleLocationId)
            VALUES(@receiptManifest,@Tenant,@number,@Trip,@Main,@Truck);
            INSERT dbo.TripManifestLine(Id,TenantId,TripManifestId,TripId,WarehouseLocationId,VehicleLocationId,
                LineNumber,SkuId,StockRightId,RightKind,PlannedBaseQuantity)
            VALUES(@receiptManifestLine,@Tenant,@receiptManifest,@Trip,@Main,@Truck,1,@Sku,@BRight,'B',1);
            UPDATE dbo.TripManifest SET Status=N'LOADED',LoadBusinessSourceId=@loadSource,LoadedAtUtc=@at
            WHERE TenantId=@Tenant AND Id=@receiptManifest;
            UPDATE dbo.TripManifestLine SET LoadBusinessSourceId=@loadSource,LoadSourceLineId=@loadLine,
                LoadedBaseQuantity=1,LoadedCostAmount=0
            WHERE TenantId=@Tenant AND Id=@receiptManifestLine;
            """, new { scene.Tenant, scene.Trip, scene.Main, scene.Truck, scene.Sku, scene.BRight,
                receiptManifest, receiptManifestLine, loadSource, loadLine, at, number = Key() }, tx);

        var gross = await AddCashScopeDelivery(db, tx, scene, receiptManifestLine, loadLine, 140000m, at);
        Guid item = Guid.NewGuid();
        await db.ExecuteAsync("""
            INSERT dbo.ReceivableOpenItem(Id,TenantId,SaleDeliveryId,CustomerId,BusinessSourceLineId,RightKind,OriginalAmount)
            VALUES(@item,@Tenant,@delivery,@Customer,@line,'B',140000);
            INSERT dbo.LedgerEffect(Id,TenantId,BusinessSourceLineId,EffectKey,LedgerKind,CustomerId,AmountDelta)
            VALUES(NEWID(),@Tenant,@line,'AR-OPENING','AR',@Customer,140000);
            """, new { item, scene.Tenant, scene.Customer, delivery = gross.Delivery, line = gross.Line }, tx);
        if (oldWitness is not null)
            await AddCashScopeReceipt(db, tx, scene, receiptManifestLine, loadLine, oldWitness, oldCustody, at);
        Guid receipt = await AddCashScopeReceipt(db, tx, scene, receiptManifestLine, loadLine, currentWitness, newCash, at);

        foreach (decimal allocation in allocations)
        {
            Guid source = Guid.NewGuid(), line = Guid.NewGuid();
            await AddCashScopeSource(db, tx, scene, source, line, "CASH_RECEIPT_ALLOCATION", allocation, at);
            await db.ExecuteAsync("""
                INSERT dbo.ReceiptAllocation(Id,TenantId,CashReceiptId,ReceivableOpenItemId,CustomerId,
                    AppliedAmount,AllocationSourceLineId,RecordedAtUtc)
                VALUES(NEWID(),@Tenant,@receipt,@item,@Customer,@allocation,@line,@at);
                UPDATE dbo.CashReceipt SET AppliedAmount=AppliedAmount+@allocation
                WHERE TenantId=@Tenant AND Id=@receipt;
                UPDATE dbo.ReceivableOpenItem SET AppliedAmount=AppliedAmount+@allocation
                WHERE TenantId=@Tenant AND Id=@item;
                INSERT dbo.LedgerEffect(Id,TenantId,BusinessSourceLineId,EffectKey,LedgerKind,CustomerId,AmountDelta)
                VALUES(NEWID(),@Tenant,@line,'AR-CASH-ALLOC','AR',@Customer,-@allocation),
                      (NEWID(),@Tenant,@line,'CREDIT-CASH-ALLOC','CUSTOMER_CREDIT',@Customer,-@allocation);
                """, new { scene.Tenant, scene.Customer, receipt, item, allocation, line, at }, tx);
        }

        Guid advanceSource = Guid.NewGuid(), advanceLine = Guid.NewGuid();
        await AddCashScopeSource(db, tx, scene, advanceSource, advanceLine, "D3_FIXTURE_ADVANCE", 10000, at);
        await db.ExecuteAsync("""
            INSERT dbo.LedgerEffect(Id,TenantId,BusinessSourceLineId,EffectKey,LedgerKind,AmountDelta)
            VALUES(NEWID(),@Tenant,@advanceLine,'ADVANCE-OPENING','ADVANCE',10000);
            """, new { scene.Tenant, advanceLine }, tx);
        await tx.CommitAsync(ct);
        return item;
    }

    private static async Task<Guid> AddCashScopeReceipt(SqlConnection db, SqlTransaction tx, D2Scene scene,
        Guid manifestLine, Guid loadLine, string witness, decimal amount, DateTime at)
    {
        var carrier = await AddCashScopeDelivery(db, tx, scene, manifestLine, loadLine, amount, at);
        Guid receipt = Guid.NewGuid();
        int inserted = await db.ExecuteAsync("""
            INSERT dbo.CashReceipt(Id,TenantId,SaleDeliveryId,TripId,CustomerId,BusinessSourceLineId,
                VerificationEvidenceRef,ReceivedAmount,VerifiedAtUtc)
            SELECT @receipt,@Tenant,@delivery,@Trip,@Customer,@line,EvidenceRef,VerifiedAmount,VerifiedAtUtc
            FROM dbo.VerifiedCashEvidence
            WHERE TenantId=@Tenant AND TripId=@Trip AND EvidenceRef=@witness AND VerifiedAmount=@amount;
            """, new { scene.Tenant, scene.Trip, scene.Customer, witness, amount, receipt,
                delivery = carrier.Delivery, line = carrier.Line }, tx);
        Require(inserted, 1, "Fixture receipt has its exact verified cash source");
        await db.ExecuteAsync("""
            INSERT dbo.LedgerEffect(Id,TenantId,BusinessSourceLineId,EffectKey,LedgerKind,CustomerId,AmountDelta)
            VALUES(NEWID(),@Tenant,@line,'CASH-OPENING','CUSTODY',@Customer,@amount),
                  (NEWID(),@Tenant,@line,'CREDIT-OPENING','CUSTOMER_CREDIT',@Customer,@amount);
            """, new { scene.Tenant, scene.Customer, amount, line = carrier.Line }, tx);
        return receipt;
    }

    private static async Task<(Guid Delivery, Guid Line)> AddCashScopeDelivery(SqlConnection db,
        SqlTransaction tx, D2Scene scene, Guid manifestLine, Guid loadLine, decimal payable, DateTime at)
    {
        Guid source = Guid.NewGuid(), line = Guid.NewGuid(), delivery = Guid.NewGuid();
        await AddCashScopeSource(db, tx, scene, source, line, "B_SALE_DELIVERY", payable, at, withSku: true);
        await db.ExecuteAsync("""
            INSERT dbo.SaleDelivery(Id,TenantId,TripId,CustomerId,ShipToSnapshotId,TripManifestLineId,
                LoadSourceLineId,StockRightId,RightKind,SkuId,BusinessSourceId,BusinessSourceKind,
                BusinessSourceLineId,BaseQuantity,PayableAmount,CostAmount,CostBasisVersion,BusinessDate)
            SELECT @delivery,@Tenant,t.Id,@Customer,@ShipTo,@manifestLine,@loadLine,@BRight,
                'B',@Sku,@source,'B_SALE_DELIVERY',@line,1,@payable,0,'SYNTHETIC_MA_V1',t.BusinessDate
            FROM dbo.Trip t WHERE t.TenantId=@Tenant AND t.Id=@Trip;
            """, new { scene.Tenant, scene.Trip, scene.Customer, scene.ShipTo, scene.BRight, scene.Sku,
                manifestLine, loadLine, source, line, delivery, payable }, tx);
        return (delivery, line);
    }

    private static Task<int> AddCashScopeSource(SqlConnection db, SqlTransaction tx, D2Scene scene,
        Guid source, Guid line, string kind, decimal amount, DateTime at, bool withSku = false) =>
        db.ExecuteAsync("""
            INSERT dbo.BusinessCommand(Id,TenantId,ProducerId,CommandId,PayloadHashVersion,PayloadHash,
                ActorId,Status,OutcomeCode,CompletedAtUtc)
            VALUES(@command,@Tenant,'d3-checkpoint-fixture',@number,1,HASHBYTES('SHA2_256',@number),
                'd3-fixture','APPLIED','CONFIRMED_OPENING',@at);
            INSERT dbo.BusinessSource(Id,TenantId,BusinessCommandId,SourceKind,SourceNumber,ActorId,TripId,CustomerId,OccurredAtUtc)
            VALUES(@source,@Tenant,@command,@kind,@number,'d3-fixture',@Trip,@Customer,@at);
            INSERT dbo.BusinessSourceLine(Id,TenantId,BusinessSourceId,LineNumber,SkuId,BaseQuantity,Amount)
            VALUES(@line,@Tenant,@source,1,@sku,@qty,@amount);
            """, new { scene.Tenant, scene.Trip, scene.Customer, source, line, kind, amount, at,
                command = Guid.NewGuid(), number = Key(), sku = withSku ? (Guid?)scene.Sku : null,
                qty = withSku ? (decimal?)1m : null }, tx);

    private async Task<CashCheckpoint> ReadCashScope(D2Scene scene, Guid item)
    {
        await using var reader = await App02c6Dev.ReadOnly(run, scene.Tenant);
        var actual = await reader.QuerySingleAsync<CashCheckpoint>("""
            WITH tripEffects AS (
                SELECT e.LedgerKind,e.AmountDelta
                FROM dbo.LedgerEffect e
                JOIN dbo.BusinessSourceLine l ON l.TenantId=e.TenantId AND l.Id=e.BusinessSourceLineId
                JOIN dbo.BusinessSource s ON s.TenantId=l.TenantId AND s.Id=l.BusinessSourceId
                WHERE s.TenantId=@Tenant AND s.TripId=@Trip
            )
            SELECT COALESCE(SUM(CASE WHEN LedgerKind='AR' THEN AmountDelta ELSE 0 END),0) AS Ar,
                (SELECT COALESCE(SUM(ReceivedAmount-AppliedAmount),0) FROM dbo.CashReceipt
                    WHERE TenantId=@Tenant AND TripId=@Trip) AS Credit,
                COALESCE(SUM(CASE WHEN LedgerKind='CUSTODY' THEN AmountDelta ELSE 0 END),0) AS Custody,
                COALESCE(SUM(CASE WHEN LedgerKind='FUND' THEN AmountDelta ELSE 0 END),0) AS Fund,
                (SELECT COALESCE(SUM(AppliedAmount),0) FROM dbo.ReceiptAllocation
                    WHERE TenantId=@Tenant AND ReceivableOpenItemId=@item) AS Allocated,
                COALESCE(SUM(CASE WHEN LedgerKind='ADVANCE' THEN AmountDelta ELSE 0 END),0) AS Advance
            FROM tripEffects;
            """, new { scene.Tenant, scene.Trip, item });
        decimal openItem = await reader.QuerySingleAsync<decimal>(
            "SELECT OutstandingAmount FROM dbo.ReceivableOpenItem WHERE TenantId=@Tenant AND Id=@item;",
            new { scene.Tenant, item });
        Require(openItem, actual.Ar, "AR item and signed AR ledger agree");
        return actual;
    }

    private async Task CheckCashReport(D2Scene scene, CashCheckpoint expected, decimal pending, string label)
    {
        string path = Path3 + $"/trips/{scene.Trip}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Credential(scene.Tenant, "cashier"));
        using var response = await client.SendAsync(request, ct);
        lock (http) http.Add(new { tenant = scene.Tenant, role = "cashier", method = "GET", path, status = (int)response.StatusCode });
        Require((int)response.StatusCode, 200, label + " authorized report");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var body = json.RootElement;
        Require(body.GetProperty("arB").GetDecimal(), expected.Ar, label + " AR");
        Require(body.GetProperty("customerCredit").GetDecimal(), expected.Credit, label + " CustomerCredit");
        Require(body.GetProperty("custody").GetDecimal(), expected.Custody, label + " custody");
        Require(body.GetProperty("fund").GetDecimal(), expected.Fund, label + " fund");
        Require(body.GetProperty("advance").GetDecimal(), expected.Advance, label + " advance");
        Require(body.GetProperty("pendingOfferAmount").GetDecimal(), pending, label + " offer status");
    }
}
