using System.Data;
using System.Security.Claims;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed record D1TripOption(Guid Id, string Label, bool CanLoad, bool CanGrantCredit);
public sealed record D1ShipToOption(Guid Id, Guid CustomerId, string Label);
public sealed record D1CreditView(Guid Id, Guid TripId, Guid CustomerId, Guid ShipToSnapshotId, Guid ApprovalId,
    string ApprovalVersion, decimal Granted, decimal Consumed);
public sealed record D1Catalog(string TenantId, string ActorId, string SessionId, bool Warehouse, bool Finance,
    IReadOnlyList<D1TripOption> Trips, IReadOnlyList<C2Option> Warehouses, IReadOnlyList<C2Option> Skus,
    IReadOnlyList<C2Option> Customers, IReadOnlyList<D1ShipToOption> ShipTos, IReadOnlyList<D1CreditView> CreditGrants);

public sealed partial class App01a2CommandService
{
    private void RequireD1Mode()
    {
        if (!connections.UsesApp02d1)
            throw new UnauthorizedAccessException("D.1 synthetic runtime is disabled.");
    }

    public Task<C3RightView[]> LoadRightsD1(ClaimsPrincipal principal, Guid trip, CancellationToken ct)
    {
        RequireD1Mode();
        return ReadC3(principal, trip, async (db, tx, tenant, _) =>
        {
            if (!IsPreparedTrip(await FindTrip(db, tx, tenant, trip, ct))
                || await HasLegacyManifest(db, tx, tenant, trip, ct)) return [];
            return (await EligibleD1Rights(db, tx, tenant, trip, null, connections.UsesApp03Field, connections.UsesApp03Settlement, ct)).ToArray();
        }, ct);
    }

    private static Task<IEnumerable<C3RightView>> EligibleD1Rights(SqlConnection db, SqlTransaction tx,
        string tenant, Guid trip, Guid[]? rightIds, bool field, bool settlement, CancellationToken ct)
    {
        const string sql = """
            SELECT r.Id,CAST(NULL AS uniqueidentifier) AS OrderId,r.StockLocationId AS WarehouseId,
                w.Code AS WarehouseCode,r.SkuId,k.Code AS SkuCode,k.BaseUom,
                r.GrantedBaseQuantity AS Granted,totals.Loaded,r.GrantedBaseQuantity-totals.Loaded AS WarehouseHeld,
                CAST(NULL AS nvarchar(32)) AS OriginTrip
            FROM dbo.StockRight r
            JOIN dbo.BQuota q ON q.TenantId=r.TenantId AND q.StockRightId=r.Id
            JOIN dbo.Sku k ON k.TenantId=r.TenantId AND k.Id=r.SkuId AND k.IsActive=1
            JOIN dbo.StockLocation w ON w.TenantId=r.TenantId AND w.Id=r.StockLocationId AND w.Kind='WAREHOUSE' AND w.IsActive=1
            JOIN dbo.BusinessSourceLine sl ON sl.TenantId=r.TenantId AND sl.Id=r.BusinessSourceLineId
                AND sl.SkuId=r.SkuId AND sl.BaseQuantity=r.GrantedBaseQuantity
            JOIN dbo.BusinessSource bs ON bs.TenantId=sl.TenantId AND bs.Id=sl.BusinessSourceId
                AND bs.SourceKind='STOCK_ALLOC' AND bs.TripId=r.TripId AND bs.CustomerId IS NULL
            JOIN dbo.BusinessCommand bc ON bc.TenantId=bs.TenantId AND bc.Id=bs.BusinessCommandId AND bc.Status='APPLIED'
            JOIN dbo.C2CommandScope cs ON cs.TenantId=bc.TenantId AND cs.CommandRowId=bc.Id
                AND cs.RoleCode='warehouse' AND cs.D1GrantTripId=r.TripId
            JOIN dbo.LedgerEffect fx ON fx.TenantId=sl.TenantId AND fx.BusinessSourceLineId=sl.Id
                AND fx.EffectKey='RIGHT-ALLOCATED' AND fx.LedgerKind='RIGHT_B'
                AND fx.QuantityDelta=r.GrantedBaseQuantity AND fx.AmountDelta=0
                AND fx.SkuId=r.SkuId AND fx.StockLocationId=r.StockLocationId
            CROSS APPLY (
                SELECT COALESCE(SUM(l.LoadedBaseQuantity),0) AS Loaded
                FROM dbo.TripManifestLine l
                JOIN dbo.PhysicalStockMovement m ON m.TenantId=l.TenantId AND m.TripManifestLineId=l.Id
                WHERE l.TenantId=r.TenantId AND l.StockRightId=r.Id AND l.IsC3=1 AND l.RightKind='B'
            ) totals
            WHERE r.TenantId=@Tenant AND r.TripId=@Trip AND r.RightKind='B' AND r.Status='HELD'
                AND r.ReleasedBaseQuantity=0 AND r.ConsumedBaseQuantity=0 AND totals.Loaded<=r.GrantedBaseQuantity
            """;
        string rightFilter = rightIds is null ? "" : " AND r.Id IN @Ids";
        string fieldFilter = field ? " AND " + App03FieldB.EligibleSku(settlement) + $"""
             AND EXISTS (SELECT 1 FROM dbo.Trip t WHERE t.TenantId=r.TenantId AND t.Id=r.TripId
                AND t.BusinessDate={App03FieldB.BusinessDate})
            """ : "";
        return db.QueryAsync<C3RightView>(Sql(sql + rightFilter + fieldFilter + " ORDER BY w.Code,k.Code,r.Id",
            new { Tenant = tenant, Trip = trip, Ids = rightIds }, tx, ct));
    }

    public async Task<D1Catalog> CatalogD1(ClaimsPrincipal principal, CancellationToken ct)
    {
        RequireD1Mode();
        string tenant = App02a3Sql.Tenant(principal);
        string actor = App02a3Sql.Actor(principal);
        await using var db = await connections.OpenApp02c3Async(principal, ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var roles = (await db.QueryAsync<string>(Sql("""
            SELECT RoleCode FROM dbo.App02ActorRoleGrant WITH (HOLDLOCK)
            WHERE TenantId=@Tenant AND ActorId=@Actor AND IsActive=1 AND RoleCode IN ('warehouse','finance')
            """, new { Tenant = tenant, Actor = actor }, tx, ct))).AsList();
        bool warehouse = roles.Contains("warehouse") && principal.HasClaim("synthetic_role", "warehouse");
        bool finance = roles.Contains("finance") && principal.HasClaim("synthetic_role", "finance");
        if (!warehouse && !finance)
            throw new UnauthorizedAccessException("Current Warehouse or Finance role required.");

        string fieldFilter = connections.UsesApp03Field ? " AND " + App03FieldB.EligibleSku(connections.UsesApp03Settlement) : "";
        using var rows = await db.QueryMultipleAsync(Sql($"""
            SELECT Nonce FROM dbo.App00ScratchMarker;
            SELECT t.Id,t.Code AS Label,
                CONVERT(bit,CASE WHEN @Warehouse=1 AND w.ActorId=@Actor THEN 1 ELSE 0 END) AS CanLoad,
                CONVERT(bit,CASE WHEN @Finance=1 AND p.HasPool=1 THEN 1 ELSE 0 END) AS CanGrantCredit
            FROM dbo.Trip t
            JOIN dbo.StockLocation v ON v.TenantId=t.TenantId AND v.Id=t.VehicleLocationId
                AND v.Kind='VEHICLE' AND v.IsActive=1
            LEFT JOIN dbo.C3TripWriter w ON w.TenantId=t.TenantId AND w.TripId=t.Id
            OUTER APPLY (
                SELECT TOP (1) 1 AS HasPool FROM dbo.BCreditPool p
                WHERE p.TenantId=t.TenantId AND p.TripId=t.Id AND p.PoolCode='B'
                    AND p.CurrencyUnit='SYNTHETIC_UNITLESS'
            ) p
            WHERE t.TenantId=@Tenant AND t.Status='PREPARED'
                {(connections.UsesApp03Field ? " AND t.BusinessDate=" + App03FieldB.BusinessDate : "")}
                AND ((@Warehouse=1 AND w.ActorId=@Actor) OR (@Finance=1 AND p.HasPool=1))
                AND NOT EXISTS(SELECT 1 FROM dbo.TripManifest m WHERE m.TenantId=t.TenantId AND m.TripId=t.Id AND m.IsC3=0)
            ORDER BY t.Code;
            SELECT l.Id,l.Code AS Label FROM dbo.StockLocation l
            WHERE @Warehouse=1 AND l.TenantId=@Tenant AND l.Kind='WAREHOUSE' AND l.IsActive=1
                AND (@Settlement=0 OR EXISTS (SELECT 1 FROM dbo.StockPool p
                    WHERE p.TenantId=l.TenantId AND p.StockLocationId=l.Id))
            ORDER BY l.Code;
            SELECT k.Id,CAST(k.Code+N' · '+k.Name AS nvarchar(300)) AS Label FROM dbo.Sku k
            WHERE @Warehouse=1 AND k.TenantId=@Tenant AND k.IsActive=1 {fieldFilter} ORDER BY k.Code;
            SELECT c.Id,CAST(c.Code+N' · '+c.Name AS nvarchar(300)) AS Label FROM dbo.Customer c
            WHERE @Finance=1 AND c.TenantId=@Tenant AND c.IsActive=1
                AND EXISTS(SELECT 1 FROM dbo.BCreditPool p WHERE p.TenantId=c.TenantId AND p.CustomerId=c.Id
                    AND p.PoolCode='B' AND p.CurrencyUnit='SYNTHETIC_UNITLESS')
            ORDER BY c.Code;
            SELECT s.Id,s.CustomerId,s.Address AS Label FROM dbo.ShipToSnapshot s
            JOIN dbo.Customer c ON c.TenantId=s.TenantId AND c.Id=s.CustomerId AND c.IsActive=1
            WHERE @Finance=1 AND s.TenantId=@Tenant
                AND EXISTS(SELECT 1 FROM dbo.BCreditPool p WHERE p.TenantId=s.TenantId AND p.CustomerId=s.CustomerId
                    AND p.PoolCode='B' AND p.CurrencyUnit='SYNTHETIC_UNITLESS')
            ORDER BY s.CustomerId,s.CapturedAtUtc,s.Id;
            SELECT g.Id,g.TripId,g.CustomerId,g.ShipToSnapshotId,a.Id AS ApprovalId,
                a.PolicyVersion AS ApprovalVersion,g.GrantedAmount AS Granted,g.ConsumedAmount AS Consumed
            FROM dbo.BCreditGrant g
            JOIN dbo.CreditPolicyApproval a ON a.TenantId=g.TenantId AND a.Id=g.ApprovalId
            JOIN dbo.BusinessSourceLine sl ON sl.TenantId=g.TenantId AND sl.Id=g.BusinessSourceLineId
            JOIN dbo.BusinessSource bs ON bs.TenantId=sl.TenantId AND bs.Id=sl.BusinessSourceId
            JOIN dbo.C2CommandScope cs ON cs.TenantId=bs.TenantId AND cs.CommandRowId=bs.BusinessCommandId
                AND cs.D1GrantTripId=g.TripId AND cs.RoleCode='finance'
            WHERE @Finance=1 AND g.TenantId=@Tenant ORDER BY g.TripId,g.CustomerId,g.Id;
            """, new { Tenant = tenant, Actor = actor, Warehouse = warehouse, Finance = finance, Settlement = connections.UsesApp03Settlement }, tx, ct));
        var result = new D1Catalog(tenant, actor, await rows.ReadSingleAsync<string>(), warehouse, finance,
            (await rows.ReadAsync<D1TripOption>()).AsList(), (await rows.ReadAsync<C2Option>()).AsList(),
            (await rows.ReadAsync<C2Option>()).AsList(), (await rows.ReadAsync<C2Option>()).AsList(),
            (await rows.ReadAsync<D1ShipToOption>()).AsList(), (await rows.ReadAsync<D1CreditView>()).AsList());
        await tx.CommitAsync(ct);
        return result;
    }
}
