using System.Data;
using System.Security.Claims;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed partial class App01c2BPostingService
{
    private async Task<T> ReadD2<T>(ClaimsPrincipal user, Guid? trip,
        Func<SqlConnection, SqlTransaction, string, string, Task<T>> read, CancellationToken ct)
    {
        string tenant = App02a3Sql.Tenant(user);
        string actor = App02a3Sql.Actor(user);
        await using var db = await OpenD2(user, ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (trip is Guid tripId)
        {
            await RequireD2Writer(db, tx, tenant, actor, tripId, ct);
            if (connections.UsesApp02d3)
                await LockAsync(db, tx, "APP01C:TRIP:" + tenant + ":" + tripId.ToString("N"), ct);
        }
        else await App02a3Sql.RequireRole(db, tx, tenant, actor, "sales", ct);
        T result = await read(db, tx, tenant, actor);
        await tx.CommitAsync(ct);
        return result;
    }

    internal Task<D2TripOption[]> ListD2Trips(ClaimsPrincipal user, CancellationToken ct) =>
        ReadD2(user, null, async (db, tx, tenant, actor) =>
            (await db.QueryAsync<D2TripOption>(Sql("""
                SELECT t.Id AS TripId,t.Code AS Label
                FROM dbo.Trip t
                JOIN dbo.C4DeliveryWriter w ON w.TenantId=t.TenantId AND w.TripId=t.Id AND w.ActorId=@Actor
                JOIN dbo.StockLocation v ON v.TenantId=t.TenantId AND v.Id=t.VehicleLocationId
                    AND v.Kind='VEHICLE' AND v.IsActive=1
                WHERE t.TenantId=@Tenant AND t.Status IN ('PREPARED','IN_PROGRESS')
                ORDER BY t.Code
                """, new { Tenant = tenant, Actor = actor }, tx, ct))).ToArray(), ct);

    private sealed record D2CustomerSelection(Guid CustomerId, string CustomerLabel,
        Guid SelectionId, Guid ShipToSnapshotId, Guid? OutletId, string Label);

    internal Task<D2Workspace> GetD2Workspace(ClaimsPrincipal user, Guid tripId, CancellationToken ct) =>
        ReadD2(user, tripId, async (db, tx, tenant, _) =>
        {
            var lines = await db.QueryAsync<D2LineOption>(Sql($"""
                SELECT l.Id AS TripManifestLineId,l.SkuId,CAST(k.Code+N' · '+k.Name AS nvarchar(300)) AS SkuLabel,
                    CAST(CASE WHEN lineRemaining.Qty<rightRemaining.Qty THEN lineRemaining.Qty
                        ELSE rightRemaining.Qty END AS decimal(19,3)) AS AvailableQuantity,
                    CAST(@Price AS decimal(19,4)) AS UnitPrice
                FROM dbo.TripManifestLine l
                JOIN dbo.TripManifest m ON m.TenantId=l.TenantId AND m.Id=l.TripManifestId
                    AND m.TripId=l.TripId AND m.Status='LOADED' AND m.IsC3=1
                JOIN dbo.Trip t ON t.TenantId=l.TenantId AND t.Id=l.TripId
                    AND t.Status IN ('PREPARED','IN_PROGRESS') AND t.VehicleLocationId=l.VehicleLocationId
                JOIN dbo.Sku k ON k.TenantId=l.TenantId AND k.Id=l.SkuId AND k.IsActive=1 AND k.BaseUom='EA'
                JOIN dbo.StockRight r ON r.TenantId=l.TenantId AND r.Id=l.StockRightId
                    AND r.TripId=l.TripId AND r.SkuId=l.SkuId AND r.RightKind='B' AND r.Status='HELD'
                JOIN dbo.BQuota b ON b.TenantId=r.TenantId AND b.StockRightId=r.Id
                JOIN dbo.BusinessSourceLine al ON al.TenantId=r.TenantId AND al.Id=r.BusinessSourceLineId
                    AND al.SkuId=l.SkuId AND al.BaseQuantity=r.GrantedBaseQuantity
                JOIN dbo.BusinessSource a ON a.TenantId=al.TenantId AND a.Id=al.BusinessSourceId
                    AND a.SourceKind='STOCK_ALLOC' AND a.TripId=l.TripId
                JOIN dbo.PhysicalStockMovement mv ON mv.TenantId=l.TenantId AND mv.TripManifestLineId=l.Id
                    AND mv.LoadSourceLineId=l.LoadSourceLineId AND mv.BaseQuantity=l.LoadedBaseQuantity
                    AND mv.CostAmount=l.LoadedCostAmount
                CROSS APPLY (
                    SELECT l.LoadedBaseQuantity-COALESCE(SUM(d.BaseQuantity),0) AS Qty
                    FROM dbo.SaleDelivery d WHERE d.TenantId=l.TenantId AND d.TripManifestLineId=l.Id
                ) lineRemaining
                CROSS APPLY (SELECT r.GrantedBaseQuantity-r.ConsumedBaseQuantity-r.ReleasedBaseQuantity AS Qty) rightRemaining
                WHERE l.TenantId=@Tenant AND l.TripId=@Trip AND l.RightKind='B' AND l.IsC3=1
                    AND lineRemaining.Qty>0 AND rightRemaining.Qty>0
                    {D2ManifestProof}
                ORDER BY k.Code,l.Id
                """, new { Tenant = tenant, Trip = tripId, Price = D2FixtureUnitPrice }, tx, ct));
            var selections = await db.QueryAsync<D2CustomerSelection>(Sql("""
                SELECT c.Id AS CustomerId,CAST(c.Code+N' · '+c.Name AS nvarchar(300)) AS CustomerLabel,
                    s.Id AS SelectionId,sh.Id AS ShipToSnapshotId,s.OutletId,
                    CAST(sh.Address+COALESCE(N' · '+o.Name,N'') AS nvarchar(800)) AS Label
                FROM dbo.D2ShipToBinding b
                JOIN dbo.Customer c ON c.TenantId=b.TenantId AND c.Id=b.CustomerId AND c.IsActive=1
                JOIN dbo.C1ShipToSelection s ON s.TenantId=b.TenantId AND s.CustomerId=b.CustomerId AND s.Id=b.SelectionId
                JOIN dbo.ShipToSnapshot sh ON sh.TenantId=b.TenantId AND sh.CustomerId=b.CustomerId AND sh.Id=b.ShipToSnapshotId
                LEFT JOIN dbo.C1Outlet o ON o.TenantId=s.TenantId AND o.CustomerId=s.CustomerId AND o.Id=s.OutletId
                WHERE b.TenantId=@Tenant
                ORDER BY c.Code,sh.Address,sh.Id
                """, new { Tenant = tenant }, tx, ct));
            var presentedLines = lines.ToArray();
            if (await IsD3Trip(db, tx, tenant, tripId, ct))
            {
                var prices = new Dictionary<Guid, D3Price?>();
                foreach (Guid sku in presentedLines.Select(l => l.SkuId).Distinct())
                    prices.Add(sku, await EffectiveD3Price(db, tx, tenant, tripId, sku, ct));
                presentedLines = presentedLines.Where(l => prices[l.SkuId] is not null)
                    .Select(l => l with { UnitPrice = prices[l.SkuId]!.UnitPrice }).ToArray();
            }
            return new D2Workspace(tripId, presentedLines, selections.GroupBy(s => (s.CustomerId, s.CustomerLabel))
                .Select(c => new D2CustomerOption(c.Key.CustomerId, c.Key.CustomerLabel, c
                    .Select(s => new D2SelectionOption(s.SelectionId, s.ShipToSnapshotId, s.OutletId, s.Label)).ToArray()))
                .ToArray());
        }, ct);

    private sealed record D2StoredOutcome(string Status, string OutcomeCode, string? ReasonCode, Guid? SourceId);

    internal Task<BusinessCommandOutcome?> GetD2SaleOutcome(ClaimsPrincipal user, Guid tripId,
        string producerId, string commandId, CancellationToken ct) =>
        ReadD2(user, tripId, async (db, tx, tenant, actor) =>
        {
            if (!ValidKey(producerId, 128) || !ValidKey(commandId, 128)) return null;
            var result = await db.QuerySingleOrDefaultAsync<D2StoredOutcome>(Sql("""
                SELECT command.Status,command.OutcomeCode,command.ReasonCode,source.Id AS SourceId
                FROM dbo.BusinessCommand command
                JOIN dbo.D2SaleCommandScope scope ON scope.TenantId=command.TenantId AND scope.CommandRowId=command.Id
                LEFT JOIN dbo.BusinessSource source ON source.TenantId=command.TenantId AND source.BusinessCommandId=command.Id
                WHERE command.TenantId=@Tenant AND command.ProducerId=@Producer AND command.CommandId=@Command
                    AND command.ActorId COLLATE Latin1_General_100_BIN2=@Actor AND scope.TripId=@Trip
                    AND command.Status IN ('APPLIED','REJECTED')
                """, new { Tenant = tenant, Actor = actor, Producer = producerId, Command = commandId, Trip = tripId }, tx, ct));
            return result is null ? null : new BusinessCommandOutcome(result.Status, result.OutcomeCode,
                result.ReasonCode, true, result.SourceId);
        }, ct);
}
