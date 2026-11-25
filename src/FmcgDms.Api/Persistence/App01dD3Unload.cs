using System.Security.Claims;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed partial class App01dCashierUnloadService
{
    private sealed record D3Manifest(Guid Id, Guid WarehouseLocationId, Guid VehicleLocationId,
        bool IsC3, string Status, string WarehouseKind, bool WarehouseActive);
    internal sealed record D3Right(Guid Id, Guid Warehouse, Guid Sku, string Kind, decimal Granted,
        decimal Consumed, decimal Released, string Status, bool HasAllocationProof);
    internal sealed record D3LoadedLine(Guid Id, Guid TripManifestId, Guid? SourceLine, Guid RightId,
        Guid Sku, string Kind, decimal Loaded, decimal Issued, bool HasMovementProof, bool HasValidIssues)
    {
        public decimal Remaining => Loaded - Issued;
    }
    private sealed record D3Pool(decimal OnHand, decimal Allocated);
    private sealed record D3LotRemainder(Guid LotId, decimal Loaded, decimal Issued);
    private sealed record D3Return(D3LoadedLine Line, decimal Value, IReadOnlyList<D3LotRemainder> Lots);
    private sealed record D3Release(D3Right Right, decimal Quantity, decimal NeverLoaded);
    private sealed record D3ReturnPlan(Guid Warehouse, Guid Vehicle, Guid Sku, decimal Quantity,
        decimal Value, decimal AllocatedAfter, IReadOnlyList<D3Return> Returns, IReadOnlyList<D3Release> Releases);

    public Task<BusinessCommandOutcome> UnloadD3Async(ClaimsPrincipal principal, D3UnloadInput input,
        CancellationToken ct) =>
        ExecuteAsync(principal, input, "D3_TRIP_UNLOAD", "warehouse", input.TripId,
            [("TripId", input.TripId.ToString("N"))], UnloadModernAsync, ct, d3: true);

    private async Task<BusinessCommandOutcome> UnloadModernAsync(SqlConnection db, SqlTransaction tx,
        string tenant, string actor, Guid command, D3UnloadInput input, CancellationToken ct)
    {
        const string sourceKind = "TRIP_UNLOAD";
        var scope = new { Tenant = tenant, Trip = input.TripId };
        if (await SourceUsed(db, tx, tenant, sourceKind, input.SourceNumber, ct))
            return BusinessCommandOutcome.Rejected("SOURCE_ALREADY_USED");

        var trip = await FindTrip(db, tx, tenant, input.TripId, ct);
        if (trip is null || trip.Status is not ("PREPARED" or "IN_PROGRESS") ||
            trip.VehicleKind != "VEHICLE" || !trip.VehicleActive)
            return BusinessCommandOutcome.Rejected("TRIP_NOT_ELIGIBLE");
        if (await db.ExecuteScalarAsync<int>(Sql("""
            SELECT (SELECT COUNT(*) FROM dbo.TripUnload WITH (UPDLOCK,HOLDLOCK) WHERE TenantId=@Tenant AND TripId=@Trip)
                 + (SELECT COUNT(*) FROM dbo.D3TripUnload WITH (UPDLOCK,HOLDLOCK) WHERE TenantId=@Tenant AND TripId=@Trip);
            """, scope, tx, ct)) != 0)
            return BusinessCommandOutcome.Rejected("TRIP_ALREADY_UNLOADED");
        if (await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.C4DeliveryEvidence e WITH (HOLDLOCK)
            JOIN dbo.BusinessCommand c WITH (HOLDLOCK) ON c.TenantId=e.TenantId AND c.Id=e.Id
            WHERE e.TenantId=@Tenant AND e.TripId=@Trip AND c.Status='RECEIVED';
            """, scope, tx, ct)) != 0)
            return BusinessCommandOutcome.Rejected("C4_DELIVERY_PENDING");

        decimal custody = await Custody(db, tx, tenant, input.TripId, ct);
        if (!await ReconcileCustody(db, tx, tenant, input.TripId, custody, ct))
            return BusinessCommandOutcome.Rejected("CUSTODY_SOURCE_DRIFT");
        if (await HasPendingD3CashOffer(db, tx, tenant, input.TripId, ct))
            return BusinessCommandOutcome.Rejected("CASH_HANDOVER_PENDING");
        if (custody != 0)
            return BusinessCommandOutcome.Rejected("CUSTODY_NOT_SETTLED");

        var (plans, rejection) = await PlanD3Return(db, tx, tenant, input.TripId, trip.VehicleLocationId, ct);
        if (plans is null) return BusinessCommandOutcome.Rejected(rejection!);

        Guid source = Guid.NewGuid();
        var owned = new { Source = source, Tenant = tenant, Command = command, Number = input.SourceNumber,
            Actor = actor, Trip = input.TripId, Warehouse = plans[0].Warehouse, Vehicle = trip.VehicleLocationId };
        await db.ExecuteAsync(Sql($"""
            INSERT dbo.BusinessSource(Id,TenantId,BusinessCommandId,SourceKind,SourceNumber,ActorId,TripId,OccurredAtUtc,RecordedAtUtc)
            VALUES(@Source,@Tenant,@Command,N'TRIP_UNLOAD',@Number,@Actor,@Trip,{App02c6Clock.SqlUtcNow},{App02c6Clock.SqlUtcNow});
            INSERT dbo.D3TripUnload(Id,TenantId,TripId,WarehouseLocationId,VehicleLocationId,RecordedAtUtc)
            VALUES(@Source,@Tenant,@Trip,@Warehouse,@Vehicle,{App02c6Clock.SqlUtcNow});
            """, owned, tx, ct));
        int number = 0;
        foreach (var plan in plans)
        {
            foreach (var physical in plan.Returns)
            {
                var line = physical.Line;
                Guid returned = Guid.NewGuid();
                await db.ExecuteAsync(Sql("""
                    INSERT dbo.BusinessSourceLine(Id,TenantId,BusinessSourceId,LineNumber,SkuId,BaseQuantity,Amount)
                    VALUES(@Returned,@Tenant,@Source,@Number,@Sku,@Quantity,@Value);
                    INSERT dbo.D3TripUnloadLine(TenantId,TripUnloadId,TripId,WarehouseLocationId,VehicleLocationId,
                        TripManifestLineId,LoadSourceLineId,StockRightId,RightKind,SkuId,SourceLineId,BaseQuantity,CostAmount)
                    VALUES(@Tenant,@Source,@Trip,@Warehouse,@Vehicle,@ManifestLine,@LoadLine,@Right,@Kind,@Sku,@Returned,@Quantity,@Value);
                    """, new { Returned = returned, Tenant = tenant, Source = source, Number = ++number,
                        line.Sku, Quantity = line.Remaining, physical.Value, Trip = input.TripId, plan.Warehouse, plan.Vehicle,
                        ManifestLine = line.Id, LoadLine = line.SourceLine, Right = line.RightId, line.Kind }, tx, ct));
                foreach (var lot in physical.Lots.Where(lot => lot.Loaded > lot.Issued))
                    await db.ExecuteAsync(Sql("""
                        INSERT dbo.App03LotUnload(TenantId,ManifestLineId,LotId,Quantity)
                        VALUES(@Tenant,@ManifestLine,@LotId,@Quantity);
                        """, new { Tenant = tenant, ManifestLine = line.Id, lot.LotId,
                            Quantity = lot.Loaded - lot.Issued }, tx, ct));
                await AddEffect(db, tx, tenant, returned, "VEHICLE-OUT", "PHYSICAL_STOCK",
                    -line.Remaining, -physical.Value, line.Sku, plan.Vehicle, ct);
                await AddEffect(db, tx, tenant, returned, "WAREHOUSE-IN", "PHYSICAL_STOCK",
                    line.Remaining, physical.Value, line.Sku, plan.Warehouse, ct);
            }

            foreach (var release in plan.Releases)
            {
                Guid line = Guid.NewGuid();
                await db.ExecuteAsync(Sql("""
                    INSERT dbo.BusinessSourceLine(Id,TenantId,BusinessSourceId,LineNumber,SkuId,BaseQuantity,Amount)
                    VALUES(@Line,@Tenant,@Source,@Number,@Sku,0,0);
                    INSERT dbo.D3BQuotaRelease(TenantId,TripUnloadId,TripId,WarehouseLocationId,
                        StockRightId,SkuId,SourceLineId,ReleasedBaseQuantity,NeverLoadedBaseQuantity)
                    VALUES(@Tenant,@Source,@Trip,@Warehouse,@Right,@Sku,@Line,@Quantity,@NeverLoaded);

                    UPDATE dbo.StockRight SET ReleasedBaseQuantity=@Quantity,Status=N'RELEASED'
                    WHERE TenantId=@Tenant AND Id=@Right AND TripId=@Trip AND RightKind='B'
                        AND GrantedBaseQuantity-ConsumedBaseQuantity=@Quantity AND ReleasedBaseQuantity=0 AND Status=N'HELD';
                    IF @@ROWCOUNT<>1 THROW 51915,'D.3 B right changed before release.',1;
                    """, new { Line = line, Tenant = tenant, Source = source, Number = ++number,
                        release.Right.Sku, Trip = input.TripId, plan.Warehouse, Right = release.Right.Id,
                        release.Quantity, release.NeverLoaded }, tx, ct));
                await AddEffect(db, tx, tenant, line, "RIGHT-B-RELEASE", "RIGHT_B_RELEASED",
                    -release.Quantity, 0, release.Right.Sku, null, ct);
            }

            await UpdateD3Balances(db, tx, tenant, plan, ct);
        }

        await db.ExecuteAsync(Sql("""
            UPDATE dbo.Trip SET Status=N'RECONCILING'
            WHERE TenantId=@Tenant AND Id=@Trip AND Status IN (N'PREPARED',N'IN_PROGRESS');
            IF @@ROWCOUNT<>1 THROW 51919,'D.3 trip status changed before return.',1;
            """, scope, tx, ct));
        await VerifyD3Return(db, tx, tenant, source, plans, ct);
        return BusinessCommandOutcome.Applied(number == 0 ? "TRIP_HANDOFF_RECONCILING" : "TRIP_REMAINDER_UNLOADED", source);
    }

    private static async Task UpdateD3Balances(SqlConnection db, SqlTransaction tx, string tenant,
        D3ReturnPlan plan, CancellationToken ct)
    {
        await db.ExecuteAsync(Sql("""
            IF @Quantity>0
            BEGIN
                UPDATE dbo.PhysicalStockBalance
                SET OnHandBaseQuantity=OnHandBaseQuantity-@Quantity,OnHandCostAmount=OnHandCostAmount-@Value
                WHERE TenantId=@Tenant AND StockLocationId=@Vehicle AND SkuId=@Sku
                    AND OnHandBaseQuantity>=@Quantity AND OnHandCostAmount>=@Value;
                IF @@ROWCOUNT<>1 THROW 51916,'D.3 vehicle balance changed before return.',1;

                UPDATE dbo.PhysicalStockBalance
                SET OnHandBaseQuantity=OnHandBaseQuantity+@Quantity,OnHandCostAmount=OnHandCostAmount+@Value
                WHERE TenantId=@Tenant AND StockLocationId=@Warehouse AND SkuId=@Sku;
                IF @@ROWCOUNT<>1 THROW 51917,'D.3 warehouse balance changed before return.',1;
            END;

            UPDATE dbo.StockPool
            SET OnHandBaseQuantity=OnHandBaseQuantity+@Quantity,AllocatedBaseQuantity=@AllocatedAfter
            WHERE TenantId=@Tenant AND StockLocationId=@Warehouse AND SkuId=@Sku
                AND @AllocatedAfter BETWEEN 0 AND OnHandBaseQuantity+@Quantity;
            IF @@ROWCOUNT<>1 THROW 51918,'D.3 stock pool changed before return.',1;

            """, new { Tenant = tenant, plan.Warehouse, plan.Vehicle, plan.Sku, plan.Quantity, plan.Value,
                plan.AllocatedAfter }, tx, ct));
    }

    private static async Task VerifyD3Return(SqlConnection db, SqlTransaction tx, string tenant,
        Guid source, IReadOnlyList<D3ReturnPlan> plans, CancellationToken ct)
    {
        var evidence = await db.QuerySingleAsync<(decimal Quantity, decimal Value, decimal Released,
            int Physical, int QuotaRelease, int Effects)>(Sql("""
            SELECT
                COALESCE((SELECT SUM(BaseQuantity) FROM dbo.D3TripUnloadLine WHERE TenantId=@Tenant AND TripUnloadId=@Source),0),
                COALESCE((SELECT SUM(CostAmount) FROM dbo.D3TripUnloadLine WHERE TenantId=@Tenant AND TripUnloadId=@Source),0),
                COALESCE((SELECT SUM(ReleasedBaseQuantity) FROM dbo.D3BQuotaRelease WHERE TenantId=@Tenant AND TripUnloadId=@Source),0),
                (SELECT COUNT(*) FROM dbo.D3TripUnloadLine WHERE TenantId=@Tenant AND TripUnloadId=@Source),
                (SELECT COUNT(*) FROM dbo.D3BQuotaRelease WHERE TenantId=@Tenant AND TripUnloadId=@Source),
                (SELECT COUNT(*) FROM dbo.LedgerEffect e JOIN dbo.BusinessSourceLine sl
                    ON sl.TenantId=e.TenantId AND sl.Id=e.BusinessSourceLineId
                    WHERE sl.TenantId=@Tenant AND sl.BusinessSourceId=@Source);
            """, new { Tenant = tenant, Source = source }, tx, ct));
        var returns = plans.SelectMany(plan => plan.Returns).ToArray();
        var releases = plans.SelectMany(plan => plan.Releases).ToArray();
        if (evidence.Quantity != plans.Sum(plan => plan.Quantity) || evidence.Value != plans.Sum(plan => plan.Value) ||
            evidence.Released != releases.Sum(x => x.Quantity) || evidence.Physical != returns.Length ||
            evidence.QuotaRelease != releases.Length || evidence.Effects != (returns.Length * 2) + releases.Length)
            throw new InvalidOperationException("D.3 stock return source and ledger do not reconcile.");
    }
}
