using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed partial class App01dCashierUnloadService
{
    internal sealed record MainPoolScope(Guid Warehouse, Guid Sku);
    private sealed record MainReturn(Guid ManifestLineId, decimal Quantity, bool Proven);
    private sealed record MainRelease(Guid RightId, decimal Quantity, decimal NeverLoaded, bool Proven);
    private sealed record MainTransfer(Guid RightId, decimal Quantity, bool Proven);

    internal static async Task<bool> VerifyMainHeldPool(SqlConnection db, SqlTransaction tx, string tenant,
        MainPoolScope scope, CancellationToken ct, bool successor = false)
    {
        var rights = await ReadD3Rights(db, tx, tenant, null, ct, scope);
        var loads = await ReadD3LoadedLines(db, tx, tenant, null, ct, scope);
        var byRight = loads.ToLookup(load => load.RightId);
        var rightKinds = rights.ToDictionary(right => right.Id, right => right.Kind);
        if (loads.Any(load => !load.HasMovementProof || !load.HasValidIssues || load.Remaining < 0 ||
            rightKinds.GetValueOrDefault(load.RightId) != load.Kind))
            return false;

        Dictionary<Guid, MainReturn> returns;
        Dictionary<Guid, MainRelease> releases;
        MainTransfer[] transfers = [];
        using (var evidence = await db.QueryMultipleAsync(Sql(ReturnAndReleaseProofSql + (successor ? "\n" + TransferProofSql : ""),
            new { Tenant = tenant, scope.Warehouse, scope.Sku }, tx, ct)))
        {
            returns = (await evidence.ReadAsync<MainReturn>()).ToDictionary(row => row.ManifestLineId);
            releases = (await evidence.ReadAsync<MainRelease>()).ToDictionary(row => row.RightId);
            if (successor) transfers = (await evidence.ReadAsync<MainTransfer>()).ToArray();
        }
        var loadIds = loads.Select(line => line.Id).ToHashSet();
        if (returns.Values.Any(row => !row.Proven || !loadIds.Contains(row.ManifestLineId)) ||
            releases.Values.Any(row => !row.Proven || rightKinds.GetValueOrDefault(row.RightId) != "B") ||
            transfers.Any(row => !row.Proven || rightKinds.GetValueOrDefault(row.RightId) != "A"))
            return false;

        var movedByRight = transfers.ToLookup(row => row.RightId);
        decimal allocated = 0;
        foreach (var right in rights)
        {
            var lines = byRight[right.Id];
            var release = releases.GetValueOrDefault(right.Id);
            decimal loaded = lines.Sum(line => line.Loaded);
            decimal remainder = right.Granted - right.Consumed - right.Released;
            decimal vehicle = lines.Sum(line => line.Remaining - (returns.GetValueOrDefault(line.Id)?.Quantity ?? 0));
            if (!right.HasAllocationProof || right.Released != (release?.Quantity ?? 0) + movedByRight[right.Id].Sum(row => row.Quantity) ||
                (release is not null && (release.Quantity != right.Granted - right.Consumed ||
                    release.NeverLoaded != right.Granted - loaded)) ||
                lines.Sum(line => line.Issued) != right.Consumed || loaded > right.Granted ||
                lines.Any(line => returns.TryGetValue(line.Id, out var returned) && returned.Quantity != line.Remaining) ||
                vehicle > remainder ||
                right.Status != (remainder > 0 ? "HELD" : right.Released > 0 ? "RELEASED" : "CONSUMED"))
                return false;
            // A returned vehicle remainder is back at MAIN. A released B quota
            // no longer contributes on the vehicle or in the MAIN pool.
            allocated += remainder - vehicle;
        }

        var physical = await ReadD3Physical(db, tx, tenant, scope.Warehouse, scope.Sku, ct, readOnly: true);
        var pool = await ReadD3Pool(db, tx, tenant, scope.Warehouse, scope.Sku, ct, readOnly: true);
        return physical is not null && physical.OnHandBaseQuantity == physical.SourceBaseQuantity &&
            physical.OnHandCostAmount == physical.SourceCostAmount && pool is not null &&
            pool.OnHand == physical.OnHandBaseQuantity && pool.Allocated == allocated;
    }

    private const string TransferProofSql = """
        SELECT e.OriginalRightId AS RightId,h.NeverLoaded+h.Returned AS Quantity,
            CONVERT(bit,IIF(r.GrantedBaseQuantity=h.NeverLoaded+h.Returned AND granted.Id IS NOT NULL
                AND paired.Id IS NOT NULL,1,0)) AS Proven
        FROM dbo.App05AHeldExecution e
        JOIN dbo.App05AHeldTransfer h ON h.TenantId=e.TenantId AND h.SuccessorRightId=e.RightId
        JOIN dbo.StockRight r ON r.TenantId=e.TenantId AND r.Id=e.RightId
        JOIN dbo.BusinessSourceLine grantLine ON grantLine.TenantId=r.TenantId AND grantLine.Id=r.BusinessSourceLineId
        LEFT JOIN dbo.LedgerEffect granted ON granted.TenantId=grantLine.TenantId AND granted.BusinessSourceLineId=grantLine.Id
            AND granted.LedgerKind='RIGHT_A' AND granted.EffectKey='RIGHT-ALLOCATED'
            AND granted.QuantityDelta=r.GrantedBaseQuantity AND granted.AmountDelta=0
            AND granted.StockLocationId=e.WarehouseId AND granted.SkuId=e.SkuId
        OUTER APPLY (
            SELECT l.Id FROM dbo.BusinessSourceLine l JOIN dbo.LedgerEffect effect
                ON effect.TenantId=l.TenantId AND effect.BusinessSourceLineId=l.Id
                AND effect.LedgerKind='RIGHT_A_TRANSFER_OUT' AND effect.EffectKey='RIGHT-ALLOCATED'
                AND effect.QuantityDelta=-r.GrantedBaseQuantity AND effect.AmountDelta=0
                AND effect.StockLocationId=e.WarehouseId AND effect.SkuId=e.SkuId
            WHERE l.TenantId=grantLine.TenantId AND l.BusinessSourceId=grantLine.BusinessSourceId
                AND l.LineNumber=2 AND l.BaseQuantity=r.GrantedBaseQuantity AND l.SkuId=e.SkuId
        ) paired
        WHERE e.TenantId=@Tenant AND e.WarehouseId=@Warehouse AND e.SkuId=@Sku;
        """;

    private const string ReturnAndReleaseProofSql = """
        SELECT r.TripManifestLineId AS ManifestLineId,r.BaseQuantity AS Quantity,
            CONVERT(bit,IIF(warehouse.Id IS NOT NULL AND vehicle.Id IS NOT NULL AND handoff.Id IS NOT NULL,1,0)) AS Proven
        FROM dbo.D3TripUnloadLine r
        LEFT JOIN dbo.LedgerEffect warehouse ON warehouse.TenantId=r.TenantId AND warehouse.BusinessSourceLineId=r.SourceLineId
            AND warehouse.EffectKey='WAREHOUSE-IN' AND warehouse.LedgerKind='PHYSICAL_STOCK'
            AND warehouse.StockLocationId=r.WarehouseLocationId AND warehouse.SkuId=r.SkuId
            AND warehouse.QuantityDelta=r.BaseQuantity AND warehouse.AmountDelta=r.CostAmount
        LEFT JOIN dbo.LedgerEffect vehicle ON vehicle.TenantId=r.TenantId AND vehicle.BusinessSourceLineId=r.SourceLineId
            AND vehicle.EffectKey='VEHICLE-OUT' AND vehicle.LedgerKind='PHYSICAL_STOCK'
            AND vehicle.StockLocationId=r.VehicleLocationId AND vehicle.SkuId=r.SkuId
            AND vehicle.QuantityDelta=-r.BaseQuantity AND vehicle.AmountDelta=-r.CostAmount
        OUTER APPLY (
            SELECT s.Id FROM dbo.BusinessSource s
            JOIN dbo.BusinessCommand c ON c.TenantId=s.TenantId AND c.Id=s.BusinessCommandId AND c.Status='APPLIED'
            JOIN dbo.D3CommandScope sc ON sc.TenantId=c.TenantId AND sc.CommandRowId=c.Id
                AND sc.RoleCode='warehouse' AND sc.TripId=r.TripId
            WHERE s.TenantId=r.TenantId AND s.Id=r.TripUnloadId AND s.TripId=r.TripId AND s.SourceKind='TRIP_UNLOAD'
                AND c.ActorId COLLATE Latin1_General_100_BIN2=s.ActorId COLLATE Latin1_General_100_BIN2
        ) handoff
        WHERE r.TenantId=@Tenant AND r.WarehouseLocationId=@Warehouse AND r.SkuId=@Sku;

        SELECT r.StockRightId AS RightId,r.ReleasedBaseQuantity AS Quantity,r.NeverLoadedBaseQuantity AS NeverLoaded,
            CONVERT(bit,IIF(e.Id IS NOT NULL AND handoff.Id IS NOT NULL,1,0)) AS Proven
        FROM dbo.D3BQuotaRelease r
        LEFT JOIN dbo.LedgerEffect e ON e.TenantId=r.TenantId AND e.BusinessSourceLineId=r.SourceLineId
            AND e.EffectKey='RIGHT-B-RELEASE' AND e.LedgerKind='RIGHT_B_RELEASED'
            AND e.QuantityDelta=-r.ReleasedBaseQuantity AND e.AmountDelta=0 AND e.SkuId=r.SkuId
        OUTER APPLY (
            SELECT s.Id FROM dbo.BusinessSource s
            JOIN dbo.BusinessCommand c ON c.TenantId=s.TenantId AND c.Id=s.BusinessCommandId AND c.Status='APPLIED'
            JOIN dbo.D3CommandScope sc ON sc.TenantId=c.TenantId AND sc.CommandRowId=c.Id
                AND sc.RoleCode='warehouse' AND sc.TripId=r.TripId
            WHERE s.TenantId=r.TenantId AND s.Id=r.TripUnloadId AND s.TripId=r.TripId AND s.SourceKind='TRIP_UNLOAD'
                AND c.ActorId COLLATE Latin1_General_100_BIN2=s.ActorId COLLATE Latin1_General_100_BIN2
        ) handoff
        WHERE r.TenantId=@Tenant AND r.WarehouseLocationId=@Warehouse AND r.SkuId=@Sku;
        """;
}
