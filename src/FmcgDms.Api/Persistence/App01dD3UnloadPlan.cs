using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed partial class App01dCashierUnloadService
{
    private async Task<(D3ReturnPlan[]? Plans, string? Rejection)> PlanD3Return(SqlConnection db,
        SqlTransaction tx, string tenant, Guid trip, Guid vehicle, CancellationToken ct)
    {
        var scope = new { Tenant = tenant, Trip = trip };
        var manifests = (await db.QueryAsync<D3Manifest>(Sql("""
            SELECT m.Id,m.WarehouseLocationId,m.VehicleLocationId,m.IsC3,m.Status,
                w.Kind AS WarehouseKind,w.IsActive AS WarehouseActive
            FROM dbo.TripManifest m WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.StockLocation w WITH (HOLDLOCK) ON w.TenantId=m.TenantId AND w.Id=m.WarehouseLocationId
            WHERE m.TenantId=@Tenant AND m.TripId=@Trip;
            """, scope, tx, ct))).ToArray();
        var rights = await ReadD3Rights(db, tx, tenant, trip, ct);
        if (manifests.Length == 0 && (!connections.UsesApp03Settlement || !rights.Any(r => r.Kind == "B")))
            return (null, "LOADED_MANIFEST_NOT_ELIGIBLE");
        Guid warehouse = manifests.FirstOrDefault()?.WarehouseLocationId ?? rights[0].Warehouse;
        if (manifests.Any(m => !m.IsC3 || m.Status != "LOADED" || !m.WarehouseActive ||
                m.WarehouseKind != "WAREHOUSE" || m.WarehouseLocationId != warehouse || m.VehicleLocationId != vehicle))
            return (null, "LOADED_MANIFEST_NOT_ELIGIBLE");

        if (rights.Length == 0) return (null, "REMAINDER_RIGHT_SOURCE_DRIFT");
        if ((!connections.UsesApp03Settlement && rights.Any(r => r.Sku != rights[0].Sku)) ||
            rights.Any(r => r.Warehouse != warehouse || !r.HasAllocationProof ||
                r.Released != 0 || r.Consumed > r.Granted ||
                r.Status != (r.Consumed == r.Granted ? "CONSUMED" : "HELD")))
            return (null, "REMAINDER_RIGHT_SOURCE_DRIFT");

        var lines = await ReadD3LoadedLines(db, tx, tenant, trip, ct);
        var manifestIds = manifests.Select(m => m.Id).ToHashSet();
        var rightKeys = rights.ToDictionary(r => r.Id, r => (r.Kind, r.Sku));
        if (!manifestIds.SetEquals(lines.Select(l => l.TripManifestId)) ||
            lines.Any(l => !l.HasMovementProof || !l.HasValidIssues || l.SourceLine is null ||
                l.Loaded <= 0 || l.Issued < 0 || l.Remaining < 0 ||
                rightKeys.GetValueOrDefault(l.RightId) != (l.Kind, l.Sku)))
            return (null, "MANIFEST_SOURCE_DRIFT");

        if (!connections.UsesApp03Settlement && await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.PhysicalStockBalance WITH (UPDLOCK,HOLDLOCK)
            WHERE TenantId=@Tenant AND StockLocationId=@Vehicle AND SkuId NOT IN @Skus
                AND (OnHandBaseQuantity<>0 OR OnHandCostAmount<>0);
            """, new { Tenant = tenant, Vehicle = vehicle, Skus = rights.Select(r => r.Sku).Distinct().ToArray() }, tx, ct)) != 0)
            return (null, "VEHICLE_REMAINDER_MISMATCH");

        var plans = new List<D3ReturnPlan>();
        var bySku = lines.ToLookup(l => l.Sku);
        foreach (var group in rights.GroupBy(r => r.Sku).OrderBy(group => group.Key))
        {
            var (plan, rejection) = await PlanD3Sku(db, tx, tenant, warehouse, vehicle, group.Key,
                group, bySku[group.Key].ToArray(), ct);
            if (plan is null) return (null, rejection);
            plans.Add(plan);
        }
        return !connections.UsesApp03Settlement && plans.All(plan => plan.Returns.Count == 0 && plan.Releases.Count == 0)
            ? (null, "NOTHING_TO_UNLOAD") : (plans.ToArray(), null);
    }

    private async Task<(D3ReturnPlan? Plan, string? Rejection)> PlanD3Sku(SqlConnection db, SqlTransaction tx,
        string tenant, Guid warehouse, Guid vehicle, Guid sku, IEnumerable<D3Right> rights,
        D3LoadedLine[] lines, CancellationToken ct)
    {
        var byRight = lines.ToLookup(l => l.RightId);
        var releases = new List<D3Release>();
        decimal neverLoadedA = 0;
        foreach (var right in rights.OrderBy(r => r.Id))
        {
            decimal loaded = byRight[right.Id].Sum(l => l.Loaded);
            decimal issued = byRight[right.Id].Sum(l => l.Issued);
            if (loaded > right.Granted || issued != right.Consumed)
                return (null, "REMAINDER_RIGHT_SOURCE_DRIFT");
            if (right.Kind == "A")
                neverLoadedA += right.Granted - loaded;
            else if (right.Granted > issued)
                releases.Add(new(right, right.Granted - issued, right.Granted - loaded));
        }

        decimal quantity = lines.Sum(l => l.Remaining);
        var mainBalance = await ReadD3Physical(db, tx, tenant, warehouse, sku, ct);
        var truckBalance = await ReadD3Physical(db, tx, tenant, vehicle, sku, ct);
        if (truckBalance is null || mainBalance is null ||
            truckBalance.OnHandBaseQuantity != truckBalance.SourceBaseQuantity ||
            truckBalance.OnHandCostAmount != truckBalance.SourceCostAmount ||
            mainBalance.OnHandBaseQuantity != mainBalance.SourceBaseQuantity ||
            mainBalance.OnHandCostAmount != mainBalance.SourceCostAmount)
            return (null, "PHYSICAL_SOURCE_DRIFT");
        if (quantity > truckBalance.OnHandBaseQuantity ||
            (!connections.UsesApp03Settlement && quantity != truckBalance.OnHandBaseQuantity) ||
            (truckBalance.OnHandBaseQuantity == 0 && truckBalance.OnHandCostAmount != 0))
            return (null, "VEHICLE_REMAINDER_MISMATCH");

        // A successor trip can already occupy the same physical vehicle. Move only
        // the original trip's source quantity at the vehicle's carrying unit cost.
        decimal value = D3CarryingCost(truckBalance.OnHandCostAmount, truckBalance.OnHandBaseQuantity, quantity);
        if (mainBalance.OnHandBaseQuantity + quantity > MaximumQuantity ||
            mainBalance.OnHandCostAmount + value > MaximumAmount)
            return (null, "VEHICLE_REMAINDER_MISMATCH");

        var pool = await db.QuerySingleOrDefaultAsync<D3Pool>(Sql("""
            SELECT OnHandBaseQuantity AS OnHand,AllocatedBaseQuantity AS Allocated
            FROM dbo.StockPool WITH (UPDLOCK,HOLDLOCK)
            WHERE TenantId=@Tenant AND StockLocationId=@Warehouse AND SkuId=@Sku;
            """, new { Tenant = tenant, Warehouse = warehouse, Sku = sku }, tx, ct));
        decimal neverLoadedB = releases.Sum(r => r.NeverLoaded);
        decimal returnedA = lines.Where(l => l.Kind == "A").Sum(l => l.Remaining);
        if (pool is null || pool.OnHand != mainBalance.OnHandBaseQuantity || pool.Allocated < neverLoadedA + neverLoadedB)
            return (null, "ALLOCATION_POOL_SOURCE_DRIFT");
        decimal allocated = pool.Allocated - neverLoadedB + returnedA;
        if (allocated > pool.OnHand + quantity) return (null, "ALLOCATION_POOL_SOURCE_DRIFT");

        var returned = lines.Where(l => l.Remaining > 0).ToArray();

        var returns = new List<D3Return>(returned.Length);
        decimal assigned = 0;
        // SQL orders immutable load source lines first. The last return receives
        // the exact residual cash-unit value, never a second rounded fraction.
        for (int i = 0; i < returned.Length; i++)
        {
            decimal remainingValue = value - assigned;
            decimal lineValue = i == returned.Length - 1
                ? remainingValue
                : Math.Min(remainingValue, decimal.Round(value * returned[i].Remaining / quantity,
                    4, MidpointRounding.AwayFromZero));
            var line = returned[i];
            IReadOnlyList<D3LotRemainder> lots = connections.UsesApp03Settlement
                ? (await ReadD3LotRemainder(db, tx, tenant, line.Id, ct)).AsList() : [];
            if (lots.Count != 0 && (lots.Any(lot => lot.Issued > lot.Loaded) ||
                lots.Sum(lot => lot.Loaded) != line.Loaded || lots.Sum(lot => lot.Issued) != line.Issued))
                return (null, "LOT_REMAINDER_SOURCE_DRIFT");
            returns.Add(new(line, lineValue, lots));
            assigned += lineValue;
        }
        return (new(warehouse, vehicle, sku, quantity, value, allocated, returns, releases), null);
    }

    private static decimal D3CarryingCost(decimal cost, decimal total, decimal quantity) =>
        quantity == 0 || total == 0 ? 0 : quantity == total ? cost
            : decimal.Round(cost * (quantity / total), 4, MidpointRounding.AwayFromZero);

    private static Task<IEnumerable<D3LotRemainder>> ReadD3LotRemainder(SqlConnection db, SqlTransaction tx,
        string tenant, Guid manifestLine, CancellationToken ct) =>
        db.QueryAsync<D3LotRemainder>(Sql("""
            WITH Issued AS (
                SELECT i.LotId,SUM(i.Quantity) AS Quantity FROM dbo.F03LotIssue i WITH (HOLDLOCK)
                JOIN dbo.SaleDelivery d WITH (HOLDLOCK) ON d.TenantId=i.TenantId AND d.Id=i.DeliveryId
                WHERE d.TenantId=@Tenant AND d.TripManifestLineId=@ManifestLine GROUP BY i.LotId
            ), Loaded AS (
                SELECT LotId,Quantity FROM dbo.F03LotLoad WITH (HOLDLOCK)
                WHERE TenantId=@Tenant AND ManifestLineId=@ManifestLine
            )
            SELECT COALESCE(l.LotId,i.LotId) AS LotId,COALESCE(l.Quantity,0) AS Loaded,
                COALESCE(i.Quantity,0) AS Issued
            FROM Loaded l FULL OUTER JOIN Issued i ON i.LotId=l.LotId;
            """, new { Tenant = tenant, ManifestLine = manifestLine }, tx, ct));

    private static async Task<D3Right[]> ReadD3Rights(SqlConnection db, SqlTransaction tx,
        string tenant, Guid trip, CancellationToken ct) =>
        (await db.QueryAsync<D3Right>(Sql("""
            SELECT r.Id,r.StockLocationId AS Warehouse,r.SkuId AS Sku,r.RightKind AS Kind,
                r.GrantedBaseQuantity AS Granted,r.ConsumedBaseQuantity AS Consumed,
                r.ReleasedBaseQuantity AS Released,r.Status,
                CONVERT(bit,CASE WHEN EXISTS (
                    SELECT 1 FROM dbo.BusinessSourceLine l WITH (HOLDLOCK)
                    JOIN dbo.BusinessSource s WITH (HOLDLOCK) ON s.TenantId=l.TenantId AND s.Id=l.BusinessSourceId
                    JOIN dbo.BusinessCommand c WITH (HOLDLOCK) ON c.TenantId=s.TenantId AND c.Id=s.BusinessCommandId
                    JOIN dbo.C2CommandScope sc WITH (HOLDLOCK) ON sc.TenantId=c.TenantId AND sc.CommandRowId=c.Id
                    WHERE l.TenantId=r.TenantId AND l.Id=r.BusinessSourceLineId AND l.SkuId=r.SkuId
                        AND l.BaseQuantity=r.GrantedBaseQuantity AND s.SourceKind=N'STOCK_ALLOC'
                        AND s.TripId=r.TripId AND c.Status=N'APPLIED' AND sc.RoleCode=N'warehouse'
                        AND ((r.RightKind='A' AND sc.LoadTripId IS NULL AND sc.D1GrantTripId IS NULL AND EXISTS (
                                SELECT 1 FROM dbo.AReservation a WITH (HOLDLOCK)
                                WHERE a.TenantId=r.TenantId AND a.StockRightId=r.Id))
                             OR (r.RightKind='B' AND sc.D1GrantTripId=r.TripId AND EXISTS (
                                SELECT 1 FROM dbo.BQuota b WITH (HOLDLOCK)
                                WHERE b.TenantId=r.TenantId AND b.StockRightId=r.Id)))
                ) THEN 1 ELSE 0 END) AS HasAllocationProof
            FROM dbo.StockRight r WITH (UPDLOCK,HOLDLOCK)
            WHERE r.TenantId=@Tenant AND r.TripId=@Trip;
            """, new { Tenant = tenant, Trip = trip }, tx, ct))).ToArray();

    private static async Task<D3LoadedLine[]> ReadD3LoadedLines(SqlConnection db, SqlTransaction tx,
        string tenant, Guid trip, CancellationToken ct) =>
        (await db.QueryAsync<D3LoadedLine>(Sql("""
            SELECT l.Id,l.TripManifestId,l.LoadSourceLineId AS SourceLine,l.StockRightId AS RightId,
                l.SkuId AS Sku,l.RightKind AS Kind,l.LoadedBaseQuantity AS Loaded,
                COALESCE(issued.Quantity,0) AS Issued,
                CONVERT(bit,CASE WHEN EXISTS (
                    SELECT 1 FROM dbo.PhysicalStockMovement mv WITH (HOLDLOCK)
                    JOIN dbo.BusinessSourceLine sl WITH (HOLDLOCK) ON sl.TenantId=mv.TenantId AND sl.Id=mv.LoadSourceLineId
                    JOIN dbo.BusinessSource bs WITH (HOLDLOCK) ON bs.TenantId=sl.TenantId AND bs.Id=sl.BusinessSourceId
                    JOIN dbo.BusinessCommand c WITH (HOLDLOCK) ON c.TenantId=bs.TenantId AND c.Id=bs.BusinessCommandId
                    JOIN dbo.C2CommandScope sc WITH (HOLDLOCK) ON sc.TenantId=c.TenantId AND sc.CommandRowId=c.Id
                    JOIN dbo.LedgerEffect lo WITH (HOLDLOCK) ON lo.TenantId=sl.TenantId AND lo.BusinessSourceLineId=sl.Id
                        AND lo.EffectKey=N'PHYSICAL-OUT' AND lo.LedgerKind=N'PHYSICAL_STOCK'
                        AND lo.SkuId=l.SkuId AND lo.StockLocationId=l.WarehouseLocationId
                        AND lo.QuantityDelta=-l.LoadedBaseQuantity AND lo.AmountDelta=-l.LoadedCostAmount
                    JOIN dbo.LedgerEffect li WITH (HOLDLOCK) ON li.TenantId=sl.TenantId AND li.BusinessSourceLineId=sl.Id
                        AND li.EffectKey=N'PHYSICAL-IN' AND li.LedgerKind=N'PHYSICAL_STOCK'
                        AND li.SkuId=l.SkuId AND li.StockLocationId=l.VehicleLocationId
                        AND li.QuantityDelta=l.LoadedBaseQuantity AND li.AmountDelta=l.LoadedCostAmount
                    WHERE mv.TenantId=l.TenantId AND mv.TripManifestLineId=l.Id AND mv.LoadSourceLineId=l.LoadSourceLineId
                        AND mv.BaseQuantity=l.LoadedBaseQuantity AND mv.CostAmount=l.LoadedCostAmount
                        AND sl.SkuId=l.SkuId AND sl.BaseQuantity=mv.BaseQuantity AND sl.Amount=mv.CostAmount
                        AND bs.Id=l.LoadBusinessSourceId AND bs.SourceKind=N'MANIFEST_LOAD' AND bs.TripId=l.TripId
                        AND c.Status=N'APPLIED' AND sc.RoleCode=N'warehouse' AND sc.LoadTripId=l.TripId
                        AND ((l.RightKind='A' AND sc.IsD1Load=0) OR (l.RightKind='B' AND sc.IsD1Load=1))
                ) THEN 1 ELSE 0 END) AS HasMovementProof,
                CONVERT(bit,CASE WHEN COALESCE(issued.IssueCount,0) = (
                    SELECT COUNT(*) FROM dbo.SaleDelivery d WITH (HOLDLOCK)
                    WHERE d.TenantId=l.TenantId AND d.TripManifestLineId=l.Id
                ) AND COALESCE(issued.IssueCount,0) = (
                    SELECT COUNT(*) FROM dbo.DeliveryStockIssue i WITH (HOLDLOCK)
                    WHERE i.TenantId=l.TenantId AND i.TripManifestLineId=l.Id
                ) THEN 1 ELSE 0 END) AS HasValidIssues
            FROM dbo.TripManifestLine l WITH (UPDLOCK,HOLDLOCK)
            OUTER APPLY (
                SELECT SUM(i.BaseQuantity) AS Quantity,COUNT(*) AS IssueCount
                FROM dbo.DeliveryStockIssue i WITH (HOLDLOCK)
                JOIN dbo.SaleDelivery d WITH (HOLDLOCK) ON d.TenantId=i.TenantId AND d.Id=i.SaleDeliveryId
                    AND d.BusinessSourceLineId=i.BusinessSourceLineId AND d.TripManifestLineId=i.TripManifestLineId
                WHERE i.TenantId=l.TenantId AND i.TripManifestLineId=l.Id
                    AND d.TripId=l.TripId AND d.StockRightId=l.StockRightId AND d.RightKind=l.RightKind
                    AND i.SkuId=l.SkuId AND d.BaseQuantity=i.BaseQuantity AND d.CostAmount=i.CostAmount
            ) issued
            WHERE l.TenantId=@Tenant AND l.TripId=@Trip AND l.IsC3=1
            ORDER BY l.LoadSourceLineId,l.Id;
            """, new { Tenant = tenant, Trip = trip }, tx, ct))).ToArray();

    private static Task<LocationBalance?> ReadD3Physical(SqlConnection db, SqlTransaction tx,
        string tenant, Guid location, Guid sku, CancellationToken ct) =>
        db.QuerySingleOrDefaultAsync<LocationBalance>(Sql("""
            SELECT b.OnHandBaseQuantity,b.OnHandCostAmount,
                o.BaseQuantity+COALESCE(d.Quantity,0) AS SourceBaseQuantity,
                o.CostAmount+COALESCE(d.Amount,0) AS SourceCostAmount
            FROM dbo.PhysicalStockBalance b WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.PhysicalStockOpening o WITH (HOLDLOCK)
                ON o.TenantId=b.TenantId AND o.StockLocationId=b.StockLocationId AND o.SkuId=b.SkuId
            OUTER APPLY (
                SELECT SUM(e.QuantityDelta) AS Quantity,SUM(e.AmountDelta) AS Amount
                FROM dbo.LedgerEffect e WITH (HOLDLOCK)
                WHERE e.TenantId=b.TenantId AND e.StockLocationId=b.StockLocationId AND e.SkuId=b.SkuId
                    AND e.LedgerKind=N'PHYSICAL_STOCK'
            ) d
            WHERE b.TenantId=@Tenant AND b.StockLocationId=@Location AND b.SkuId=@Sku;
            """, new { Tenant = tenant, Location = location, Sku = sku }, tx, ct));
}
