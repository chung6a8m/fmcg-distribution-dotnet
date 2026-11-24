using System.Data;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Data.SqlClient;

namespace FmcgDms.Api.Persistence;

public sealed record C2Option(Guid Id, string Label);
public sealed record C2Identity(string TenantId, string ActorId, string SessionId, bool Sales, bool Warehouse, bool Visits = false)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Profile { get; init; }
}
public sealed record C2Catalog(IReadOnlyList<C2Option> Customers, IReadOnlyList<C2Option> Trips,
    IReadOnlyList<C2Option> Skus, IReadOnlyList<C2Option> Warehouses,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<F03UnitView>? Units = null);
public sealed record C2OrderLineView(Guid? LineId, string Sku, string BaseUom, decimal Ordered, decimal Reserved,
    decimal? UnitPrice, int? PolicyVersion, int? TermsDays, decimal Delivered, decimal Loaded, decimal HeldOnTrip,
    string? PolicySourceHash)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public F04PriceView? Pricing { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public C2LineHandoff? Handoff { get; init; }
}
public sealed record C2OrderView(Guid Id, string Customer, string Trip, string ShipTo, DateTime? BusinessDate,
    Guid? ConfirmSourceId, string Status, IReadOnlyList<C2OrderLineView> Lines, Guid? ShipToSnapshotId, string ShipToOrigin)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? TripId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public C2HandoffActions? Actions { get; init; }
}

public sealed partial class App01a2CommandService
{
    private sealed record C2Header(Guid Id, string Customer, string Trip, Guid TripId, string TripStatus,
        DateTime TripDate, string ShipTo, DateTime? BusinessDate, Guid? ConfirmSourceId, Guid? ShipToSnapshotId, string ShipToOrigin);
    private sealed record C2LineRow(Guid OrderId, int LineNumber, Guid? LineId, string Sku, string BaseUom, decimal Ordered,
        decimal Reserved, decimal? UnitPrice, int? PolicyVersion, int? TermsDays, decimal Delivered,
        decimal Loaded, decimal HeldOnTrip, string? PolicySourceHash);

    public Task<C2Catalog> CatalogC2(ClaimsPrincipal principal, CancellationToken ct) =>
        ReadC2(principal, async (db, tx, tenant, _) =>
        {
            using var grid = await db.QueryMultipleAsync(Sql($"""
                SELECT Id,CAST(Code+N' · '+Name AS nvarchar(300)) AS Label
                FROM dbo.Customer WHERE TenantId=@Tenant AND IsActive=1 ORDER BY Code;
                SELECT t.Id,t.Code AS Label FROM dbo.Trip t JOIN dbo.StockLocation l
                    ON l.TenantId=t.TenantId AND l.Id=t.VehicleLocationId AND l.Kind='VEHICLE' AND l.IsActive=1
                WHERE t.TenantId=@Tenant AND t.Status='PREPARED'
                    AND (@CurrentDayOnly=0 OR t.BusinessDate={LotBusinessDate}) ORDER BY t.Code;
                SELECT Id,CAST(Code+N' · '+Name AS nvarchar(300)) AS Label
                FROM dbo.Sku WHERE TenantId=@Tenant AND IsActive=1 ORDER BY Code;
                SELECT Id,Code AS Label FROM dbo.StockLocation
                WHERE TenantId=@Tenant AND Kind='WAREHOUSE' AND IsActive=1 ORDER BY Code;
                """, new { Tenant = tenant, CurrentDayOnly = connections.UsesApp03BusinessDay }, tx, ct));
            var customers = (await grid.ReadAsync<C2Option>()).AsList();
            var trips = (await grid.ReadAsync<C2Option>()).AsList();
            var skus = (await grid.ReadAsync<C2Option>()).AsList();
            var warehouses = (await grid.ReadAsync<C2Option>()).AsList();
            var units = connections.UsesDevF03
                ? (await db.QueryAsync<F03UnitView>(Sql("""
                    SELECT u.SkuId,u.UomCode,u.Factor,u.Step FROM dbo.F03Unit u
                    JOIN dbo.Sku s ON s.TenantId=u.TenantId AND s.Id=u.SkuId AND s.IsActive=1
                    WHERE u.TenantId=@Tenant
                    ORDER BY u.SkuId, CASE WHEN u.UomCode COLLATE DATABASE_DEFAULT=s.BaseUom THEN 0 ELSE 1 END,u.UomCode
                    """, new { Tenant = tenant }, tx, ct))).AsList()
                : null;
            return new C2Catalog(customers, trips, skus, warehouses, units);
        }, ct);

    public Task<C2Identity> IdentityC2(ClaimsPrincipal principal, CancellationToken ct) =>
        ReadC2(principal, async (db, tx, tenant, actor) =>
        {
            var roles = (await db.QueryAsync<string>(Sql("""
                SELECT RoleCode FROM dbo.App02ActorRoleGrant WHERE TenantId=@Tenant AND ActorId=@Actor AND IsActive=1
                """, new { Tenant = tenant, Actor = actor }, tx, ct))).AsList();
            var nonce = await db.QuerySingleAsync<string>(Sql("SELECT Nonce FROM dbo.App00ScratchMarker", null, tx, ct));
            return new C2Identity(tenant, actor, nonce,
                roles.Contains("sales") && principal.HasClaim("synthetic_role", "sales"),
                roles.Contains("warehouse") && principal.HasClaim("synthetic_role", "warehouse"));
        }, ct);

    public Task<IReadOnlyList<C2OrderView>> OrdersC2(ClaimsPrincipal principal, CancellationToken ct, bool handoff = false) =>
        ReadC2<IReadOnlyList<C2OrderView>>(principal, async (db, tx, tenant, actor) =>
        {
            var access = handoff ? await ReadHandoffAccess(db, tx, tenant, actor, principal, ct) : null;
            using var grid = await db.QueryMultipleAsync(Sql("""
                DECLARE @visible TABLE (Id uniqueidentifier PRIMARY KEY);
                INSERT @visible SELECT d.Id FROM dbo.C2OrderDraft d
                WHERE d.TenantId=@Tenant AND (@ShowDrafts=1 OR EXISTS (
                    SELECT 1 FROM dbo.C2Confirmation x WHERE x.TenantId=d.TenantId AND x.OrderId=d.Id));
                SELECT d.Id,c.Name AS Customer,t.Code AS Trip,t.Id AS TripId,t.Status AS TripStatus,t.BusinessDate AS TripDate,
                    COALESCE(s.Address,JSON_VALUE(sel.Payload,'$.line')) AS ShipTo,o.BusinessDate,x.ConfirmSourceId,
                    x.ShipToSnapshotId,sel.Origin AS ShipToOrigin
                FROM dbo.C2OrderDraft d
                JOIN @visible v ON v.Id=d.Id
                JOIN dbo.Customer c ON c.TenantId=d.TenantId AND c.Id=d.CustomerId
                JOIN dbo.Trip t ON t.TenantId=d.TenantId AND t.Id=d.TripId
                JOIN dbo.C1ShipToSelection sel ON sel.TenantId=d.TenantId AND sel.Id=d.SelectionId
                LEFT JOIN dbo.C2Confirmation x ON x.TenantId=d.TenantId AND x.OrderId=d.Id
                LEFT JOIN dbo.AOrder o ON o.TenantId=x.TenantId AND o.Id=x.OrderId
                LEFT JOIN dbo.ShipToSnapshot s ON s.TenantId=x.TenantId AND s.Id=x.ShipToSnapshotId
                WHERE d.TenantId=@Tenant ORDER BY d.Id;
                SELECT d.DraftId AS OrderId,d.LineNumber,ol.Id AS LineId,s.Code AS Sku,COALESCE(p.BaseUom,d.BaseUom) AS BaseUom,
                    d.BaseQuantity AS Ordered,COALESCE(h.Reserved,0) AS Reserved,
                    a.UnitPriceSnapshot AS UnitPrice,p.PolicyVersion,a.TermsDaysSnapshot AS TermsDays,
                    qty.Delivered,load.Loaded,load.HeldOnTrip,CONVERT(varchar(64),p.SourceHash,2) AS PolicySourceHash
                FROM dbo.C2OrderDraftLine d JOIN dbo.Sku s ON s.TenantId=d.TenantId AND s.Id=d.SkuId
                JOIN @visible v ON v.Id=d.DraftId
                LEFT JOIN dbo.AOrderLine ol ON ol.TenantId=d.TenantId AND ol.AOrderId=d.DraftId AND ol.LineNumber=d.LineNumber
                LEFT JOIN dbo.AConfirmedLineSnapshot a ON a.TenantId=ol.TenantId AND a.AOrderLineId=ol.Id
                LEFT JOIN dbo.C2LinePolicy p ON p.TenantId=ol.TenantId AND p.OrderLineId=ol.Id
                OUTER APPLY (
                    SELECT SUM(r.GrantedBaseQuantity-r.ConsumedBaseQuantity-r.ReleasedBaseQuantity) AS Reserved
                    FROM dbo.AReservation ar JOIN dbo.StockRight r ON r.TenantId=ar.TenantId AND r.Id=ar.StockRightId
                    WHERE ar.TenantId=ol.TenantId AND ar.AOrderLineId=ol.Id AND r.Status='HELD' AND r.RightKind='A'
                ) h
                CROSS APPLY (SELECT COALESCE(SUM(BaseQuantity),0) AS Delivered FROM dbo.SaleDelivery
                    WHERE TenantId=ol.TenantId AND AOrderLineId=ol.Id AND RightKind='A') qty
                CROSS APPLY (
                    SELECT COALESCE(SUM(l.LoadedBaseQuantity),0) AS Loaded,
                        COALESCE(SUM(l.LoadedBaseQuantity-used.Delivered),0) AS HeldOnTrip
                    FROM dbo.AReservation r JOIN dbo.TripManifestLine l
                      ON l.TenantId=r.TenantId AND l.StockRightId=r.StockRightId AND l.IsC3=1
                    CROSS APPLY (SELECT CONVERT(decimal(18,3),COALESCE(SUM(BaseQuantity),0)) AS Delivered FROM dbo.SaleDelivery
                        WHERE TenantId=l.TenantId AND TripManifestLineId=l.Id) used
                    WHERE r.TenantId=ol.TenantId AND r.AOrderLineId=ol.Id
                ) load
                WHERE d.TenantId=@Tenant ORDER BY d.DraftId,d.LineNumber;
                """, new { Tenant = tenant, ShowDrafts = access?.Sales ?? true }, tx, ct));
            var headers = (await grid.ReadAsync<C2Header>()).AsList();
            var lines = (await grid.ReadAsync<C2LineRow>()).ToLookup(l => l.OrderId);
            var confirmed = lines.SelectMany(group => group).Where(line => line.LineId.HasValue)
                .Select(line => line.LineId!.Value).ToArray();
            var pricing = new Dictionary<Guid, F04Snapshot>();
            if (connections.UsesDevF04)
                foreach (var batch in confirmed.Chunk(500))
                    foreach (var price in await ReadPrices(db, tx, tenant, batch, ct))
                        pricing.Add(price.OrderLineId, price);
            var handoffLines = handoff && headers.Count > 0
                ? await ReadHandoffLines(db, tx, tenant, headers.Select(h => h.Id).ToArray(), ct)
                : null;
            var readyToLoad = access is null || confirmed.Length == 0 ? [] :
                await OrdersReadyToLoad(db, tx, tenant, access.LoadTrips, ct);
            return headers.Select(h =>
            {
                var orderLines = lines[h.Id].Select(l => new C2OrderLineView(
                    l.LineId, l.Sku, l.BaseUom, l.Ordered, l.Reserved, l.UnitPrice, l.PolicyVersion, l.TermsDays,
                    l.Delivered, l.Loaded, l.HeldOnTrip, l.PolicySourceHash)
                {
                    Pricing = l.LineId is Guid id ? pricing.GetValueOrDefault(id)?.ToView() : null,
                    Handoff = handoffLines?.View(l)
                }).ToArray();
                string status = h.ConfirmSourceId is null ? "DRAFT" :
                    orderLines.All(l => l.Delivered == l.Ordered) ? "FULFILLED" :
                    orderLines.Any(l => l.Delivered > 0) ? "PARTIALLY_DELIVERED" :
                    orderLines.All(l => l.Reserved == l.Ordered) ? "CONFIRMED_FULLY_RESERVED" :
                    orderLines.Any(l => l.Reserved > 0) ? "CONFIRMED_PARTIALLY_RESERVED" : "CONFIRMED_UNRESERVED";
                return new C2OrderView(h.Id, h.Customer, h.Trip, h.ShipTo, h.BusinessDate, h.ConfirmSourceId, status,
                    orderLines, h.ShipToSnapshotId, h.ShipToOrigin)
                {
                    TripId = handoff ? h.TripId : null,
                    Actions = access?.Actions(h, orderLines, connections.UsesApp03BusinessDay, readyToLoad.Contains(h.Id))
                };
            }).ToArray();
        }, ct);

    public Task<BusinessCommandOutcome?> OutcomeC2(ClaimsPrincipal principal, Guid commandId, CancellationToken ct) =>
        ReadC2(principal, async (db, tx, tenant, actor) =>
        {
            var row = await db.QuerySingleOrDefaultAsync<ExistingCommand>(Sql("""
                SELECT c.Id,c.Status,c.OutcomeCode,c.ReasonCode FROM dbo.BusinessCommand c
                JOIN dbo.C2CommandScope s ON s.TenantId=c.TenantId AND s.CommandRowId=c.Id
                JOIN dbo.App02ActorRoleGrant g WITH (HOLDLOCK)
                    ON g.TenantId=c.TenantId AND g.ActorId=@Actor AND g.RoleCode=s.RoleCode AND g.IsActive=1
                WHERE c.TenantId=@Tenant AND c.ProducerId=@Producer AND c.CommandId=@Command
                    AND s.LoadTripId IS NULL AND s.DeliveryTripId IS NULL
                    AND c.ActorId COLLATE Latin1_General_100_BIN2=@Actor AND c.Status IN ('APPLIED','REJECTED')
                    AND ((@Sales=1 AND s.RoleCode='sales') OR (@Warehouse=1 AND s.RoleCode='warehouse'))
                """, new
            {
                Tenant = tenant, Producer = C2Producer(principal), Command = commandId.ToString("N"), Actor = actor,
                Sales = principal.HasClaim("synthetic_role", "sales"), Warehouse = principal.HasClaim("synthetic_role", "warehouse")
            }, tx, ct));
            if (row is null) return null;
            Guid? source = await db.QuerySingleOrDefaultAsync<Guid?>(Sql("""
                SELECT Id FROM dbo.BusinessSource WHERE TenantId=@Tenant AND BusinessCommandId=@Id
                """, new { Tenant = tenant, row.Id }, tx, ct));
            return new BusinessCommandOutcome(row.Status, row.OutcomeCode, row.ReasonCode, true, source);
        }, ct);

    private async Task<T> ReadC2<T>(ClaimsPrincipal principal,
        Func<SqlConnection, SqlTransaction, string, string, Task<T>> read, CancellationToken ct)
    {
        var tenant = App02a3Sql.Tenant(principal);
        var actor = App02a3Sql.Actor(principal);
        bool sales = principal.HasClaim("synthetic_role", "sales");
        bool warehouse = principal.HasClaim("synthetic_role", "warehouse");
        if (!sales && !warehouse) throw new UnauthorizedAccessException("C.2 read role required.");
        await using var db = await connections.OpenApp02c2Async(principal, ct);
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (await db.ExecuteScalarAsync<int>(Sql("""
            SELECT COUNT(*) FROM dbo.App02ActorRoleGrant WITH (HOLDLOCK)
            WHERE TenantId=@Tenant AND ActorId=@Actor AND IsActive=1
                AND ((@Sales=1 AND RoleCode='sales') OR (@Warehouse=1 AND RoleCode='warehouse'))
            """, new { Tenant = tenant, Actor = actor, Sales = sales, Warehouse = warehouse }, tx, ct)) == 0)
            throw new UnauthorizedAccessException("C.2 current role not granted.");
        T result = await read(db, tx, tenant, actor);
        await tx.CommitAsync(ct);
        return result;
    }
}
